using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EPDM.Interop.epdm;

namespace GladosPdmAddin.Hooks
{
    /// <summary>
    /// Shared fire-and-forget trigger path for every hook that just needs to tell Glados "look
    /// at file X": EdmCmd_PostState, EdmCmd_PostUnlock, EdmCmd_PostDelete, EdmCmd_PostMove,
    /// EdmCmd_PostRename, the add-in's own re-sync menu command (MenuHook), and through
    /// HandleNewFiles EdmCmd_PostAdd and EdmCmd_PostCopy. A new file is only sent when it is
    /// already checked in, as a task's output or a copy is: a file a user just added sits in
    /// private state, which Glados cannot read, so sending it would only record an unreadable
    /// skip; its check-in and the poller pick it up. Only the first file of a call is checked
    /// and the answer applies to all of them: the files of one add or copy share how they
    /// arrived (a drag and drop leaves every file checked out, a task or import checks every
    /// file in), and a check per file would cost one PDM round trip each inside the user's add.
    /// A call that mixes both is left to the poller for the files misjudged. The files of one OnCmd call go to Glados together, one HTTP call per
    /// GladosClient.MaxTriggerBatch files, on a background task, so the hook returns at once.
    /// Each file is gated by the process-wide RateLimiter; once a batch exceeds it the remaining
    /// files are skipped and left for the Glados poller to pick up. A call that fails
    /// transiently hands its files to TriggerRetryQueue; a successful one clears them from it.
    /// When this Windows user has not signed in on the Glados tab, nothing is collected or
    /// sent: one log line says how many files were left to the poller, which picks up every
    /// change regardless, so a missing sign-in only delays the sync.
    /// PDM may list the same file several times in one call (one state change was seen to
    /// carry seven entries for one file id, and a shared file has one entry per folder), so
    /// each file id is sent once, with the first entry's path and state. The rate limit and
    /// the backend's trigger limit count every file of a batch, so a duplicate would only
    /// spend that budget.
    /// </summary>
    internal static class TriggerHook
    {
        /// <summary>Longest state name the backend accepts; a longer one would get the whole
        /// batch rejected, so it is cut instead.</summary>
        private const int MaxStateLength = 200;

        /// <param name="hookName">Short name written to the log line, e.g. "PostState".</param>
        /// <param name="reason">The change reason sent with every file of this call, e.g.
        /// "state_change"; see TriggerItem.</param>
        /// <param name="vault">Vault of the current command (EdmCmd.mpoVault cast to IEdmVault5).</param>
        /// <param name="data">Affected files for this OnCmd call.</param>
        /// <param name="pathSelector">Reads the full local path directly out of an EdmCmdData
        /// entry when the hook provides one; returns null when it does not (EdmCmd_PostRename),
        /// in which case FileResolver.TryGetLocalPath is used as a fallback.</param>
        /// <param name="stateSelector">Reads the destination state name for a state change;
        /// null for every other hook.</param>
        public static void Handle(string hookName, string reason, IEdmVault5 vault, EdmCmdData[] data,
            Func<EdmCmdData, string> pathSelector, Func<EdmCmdData, string> stateSelector = null)
        {
            // mlObjectID1 is the file id and mlObjectID2 its folder for every hook routed here.
            Collect(hookName, reason, vault, data, entry => entry.mlObjectID1, entry => entry.mlObjectID2,
                pathSelector, stateSelector, false);
        }

        /// <summary>The trigger path for hooks that report a file new to the vault
        /// (EdmCmd_PostAdd, EdmCmd_PostCopy), whose EdmCmdData members differ from the other
        /// hooks: the caller says where the file id and the folder id are. Only calls whose
        /// first file is already checked in are sent; see the class summary.</summary>
        /// <param name="fileIdSelector">Reads or resolves the file id of an entry; 0 when there
        /// is none, which skips the entry.</param>
        /// <param name="folderIdSelector">Reads the id of the folder the file is in now.</param>
        public static void HandleNewFiles(string hookName, string reason, IEdmVault5 vault, EdmCmdData[] data,
            Func<EdmCmdData, int> fileIdSelector, Func<EdmCmdData, int> folderIdSelector,
            Func<EdmCmdData, string> pathSelector)
        {
            Collect(hookName, reason, vault, data, fileIdSelector, folderIdSelector, pathSelector, null, true);
        }

        private static void Collect(string hookName, string reason, IEdmVault5 vault, EdmCmdData[] data,
            Func<EdmCmdData, int> fileIdSelector, Func<EdmCmdData, int> folderIdSelector,
            Func<EdmCmdData, string> pathSelector, Func<EdmCmdData, string> stateSelector, bool checkedInOnly)
        {
            if (vault == null || data == null)
                return;

            var vaultName = SafeGetVaultName(vault);
            if (vaultName == null)
                return;

            string serverUrl, token;
            if (!GladosCredentials.TryGet(out serverUrl, out token))
            {
                Logging.Log(hookName, 0, null, "not signed in to GLADOS, " + CountFiles(data, fileIdSelector) + " file(s) left to the poller");
                return;
            }

            var items = new List<TriggerItem>();
            var seen = new HashSet<int>();
            var checkedIn = checkedInOnly ? (bool?)null : true;
            var notCheckedIn = 0;
            foreach (var entry in data)
            {
                var fileId = SafeInvoke(fileIdSelector, entry);
                if (fileId <= 0 || !seen.Add(fileId))
                    continue;

                // Asked once, for the first file. An unknown answer sends the files: Glados copes
                // with an unreadable one.
                if (!checkedIn.HasValue)
                    checkedIn = FileResolver.TryIsCheckedIn(vault, fileId) != false;
                if (checkedIn == false)
                {
                    notCheckedIn++;
                    continue;
                }

                if (!RateLimiter.TryAcquire())
                    break;

                var rawPath = SafeInvoke(pathSelector, entry);
                if (string.IsNullOrEmpty(rawPath))
                    rawPath = FileResolver.TryGetLocalPath(vault, fileId, SafeInvoke(folderIdSelector, entry));

                var relativePath = FileResolver.ToVaultRelativePath(vault, rawPath);
                var version = FileResolver.TryGetCurrentVersion(vault, fileId);

                var state = stateSelector != null ? SafeInvoke(stateSelector, entry) : null;
                if (string.IsNullOrEmpty(state))
                    state = null;
                else if (state.Length > MaxStateLength)
                    state = state.Substring(0, MaxStateLength);

                items.Add(new TriggerItem(fileId, relativePath, version, reason, state));
            }

            if (notCheckedIn > 0)
                Logging.Log(hookName, 0, null, notCheckedIn + " file(s) not checked in yet, left to their check-in and the poller");

            if (items.Count > 0)
                FireAndForget(hookName, vaultName, items);
        }

        private static void FireAndForget(string hookName, string vaultName, List<TriggerItem> items)
        {
            Task.Run(async () =>
            {
                for (var start = 0; start < items.Count; start += GladosClient.MaxTriggerBatch)
                {
                    var batch = items.GetRange(start, Math.Min(GladosClient.MaxTriggerBatch, items.Count - start));
                    try
                    {
                        var result = await GladosClient.TriggerBatchAsync(vaultName, batch).ConfigureAwait(false);
                        LogEach(hookName, batch, result.Describe());
                        if (result.Success)
                            TriggerRetryQueue.Forget(batch);
                        else if (result.Transient)
                            TriggerRetryQueue.Add(vaultName, batch, result.Describe());
                    }
                    catch (Exception ex)
                    {
                        // GladosClient.TriggerBatchAsync already catches everything itself; this
                        // is a last-resort net so nothing from the background task can go
                        // unlogged.
                        LogEach(hookName, batch, "unexpected failure: " + ex.Message);
                    }
                }
            });
        }

        /// <summary>One log line per file, as before batching, so the log still answers "what
        /// happened to file X".</summary>
        private static void LogEach(string hookName, List<TriggerItem> batch, string detail)
        {
            foreach (var item in batch)
                Logging.Log(hookName, item.FileId, item.Path, detail);
        }

        private static int CountFiles(EdmCmdData[] data, Func<EdmCmdData, int> fileIdSelector)
        {
            var seen = new HashSet<int>();
            foreach (var entry in data)
            {
                var fileId = SafeInvoke(fileIdSelector, entry);
                if (fileId > 0)
                    seen.Add(fileId);
            }
            return seen.Count;
        }

        private static string SafeGetVaultName(IEdmVault5 vault)
        {
            try
            {
                return vault.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int SafeInvoke(Func<EdmCmdData, int> selector, EdmCmdData entry)
        {
            try
            {
                return selector(entry);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string SafeInvoke(Func<EdmCmdData, string> selector, EdmCmdData entry)
        {
            try
            {
                return selector(entry);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
