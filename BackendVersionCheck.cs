using System;
using System.Reflection;
using System.Threading.Tasks;

namespace GladosPdmAddin
{
    /// <summary>
    /// Compares the pat contract the Glados server serves with the one this add-in was built
    /// for (GladosClient.SupportedPatContract) and words the warning that the Glados tab and the
    /// sign-in window show when the two differ. It is a warning only: a new backend minor may
    /// change or remove pat endpoints, but it may as well keep the ones this add-in calls, so
    /// notifications keep being sent and Glados' own answers decide. The check runs only where
    /// the add-in already talks to Glados interactively, after a successful token test, off the
    /// UI thread and never from a vault hook. A version call that fails is not a mismatch: it
    /// costs a log line and shows nothing. Since the tab checks again every time it is
    /// selected, a mismatch is logged once per process and a failure once per distinct reason.
    /// </summary>
    internal static class BackendVersionCheck
    {
        private static readonly object SyncRoot = new object();
        private static bool _mismatchLogged;
        private static string _lastLoggedFailure;

        /// <summary>The add-in's own version, the X.Y.Z of AssemblyInformationalVersion that
        /// users quote and the release tag carries.</summary>
        public static readonly string AddinVersion = ReadAddinVersion();

        /// <summary>Asks Glados for its version with the given sign-in and returns the warning
        /// sentence, or null when the contract matches or is not known. Never throws.</summary>
        public static async Task<string> WarningAsync(string serverUrl, string token)
        {
            try
            {
                var result = await GladosClient.GetBackendVersionAsync(serverUrl, token).ConfigureAwait(false);
                if (!result.Known)
                {
                    LogFailure(result.Detail);
                    return null;
                }
                if (!result.IsUnsupported)
                    return null;

                LogMismatch(result);
                return "This GLADOS server runs version " + result.Version + " (API contract " + result.PatContract
                    + "), but this add-in, version " + AddinVersion + ", supports API contract "
                    + GladosClient.SupportedPatContract + ". Changes are still sent to GLADOS. Ask your PDM administrator"
                    + " to update the add-in to a release that supports GLADOS " + result.PatContract + ".x.";
            }
            catch (Exception ex)
            {
                LogFailure(ex.Message);
                return null;
            }
        }

        private static void LogMismatch(BackendVersionResult result)
        {
            lock (SyncRoot)
            {
                if (_mismatchLogged)
                    return;
                _mismatchLogged = true;
            }
            Logging.Log("Version", 0, null, "GLADOS " + result.Version + " serves pat contract " + result.PatContract
                + ", add-in " + AddinVersion + " supports " + GladosClient.SupportedPatContract
                + "; notifications are still sent, update the add-in");
        }

        private static void LogFailure(string detail)
        {
            lock (SyncRoot)
            {
                if (string.Equals(detail, _lastLoggedFailure, StringComparison.Ordinal))
                    return;
                _lastLoggedFailure = detail;
            }
            Logging.Log("Version", 0, null, "GLADOS version not checked: " + detail);
        }

        private static string ReadAddinVersion()
        {
            try
            {
                var attribute = typeof(BackendVersionCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (attribute != null && !string.IsNullOrEmpty(attribute.InformationalVersion))
                    return attribute.InformationalVersion;
            }
            catch (Exception)
            {
                // Falls through to the placeholder; the warning still names both contracts.
            }
            return "unknown";
        }
    }
}
