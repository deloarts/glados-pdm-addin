using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GladosPdmAddin
{
    /// <summary>
    /// Content of the Glados tab in the bottom panel of a File Explorer vault view: a status
    /// line with a button that opens GladosSignInForm, below it a warning line that appears
    /// only when the Glados server serves a pat contract this add-in does not support
    /// (BackendVersionCheck), and below that the Glados sync history of the files selected in
    /// the vault view, merged into one list, newest first. ExplorerTabHook
    /// creates one instance per vault view window, hands its window handle to PDM and forwards
    /// that window's selection events. PDM reports a selection one item at a time and a
    /// deselection by file and folder name only (EdmCmd_DeSelectItem carries no id), so the
    /// control keeps the selected files keyed by folder and name; a file selected in another
    /// folder starts a new selection, since a vault view only selects within one folder and a
    /// deselection PDM never sends must not leave stale files behind. Selection changes come in
    /// bursts, so the history is fetched once the selection has been quiet for a moment, only
    /// while the tab is shown (otherwise when it is shown next), and an answer that arrives
    /// after a newer request is dropped.
    /// Built in code rather than with the designer. This control lives inside explorer.exe,
    /// where an unhandled exception in an event handler would take Explorer down, so every
    /// handler catches everything and every network call runs off the UI thread and reports
    /// back through BeginInvoke.
    /// </summary>
    internal sealed class GladosTabControl : UserControl
    {
        private const int SelectionQuietMilliseconds = 300;

        private readonly Label _status;
        private readonly Button _signIn;
        private readonly Label _versionWarning;
        private readonly ListView _history;
        private readonly Label _note;
        private readonly Timer _selectionQuiet;

        // Selected files of this vault view, keyed by folder and file name (see the summary).
        private readonly Dictionary<string, int> _selected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private string _selectionFolder;
        private string _vaultName;

        // Incremented on every token test and history request, so a slow answer that arrives
        // after a newer one cannot overwrite what is shown. The version check that follows a
        // token test shares the test's number.
        private int _testSequence;
        private int _historySequence;

        // Set when the selection changed while the tab was hidden; fetched once it is shown.
        private bool _historyStale;

        // The last history failure written to the log, so a Glados outage logs once rather
        // than once per click.
        private string _lastLoggedFailure;

        public GladosTabControl()
        {
            _status = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            _signIn = new Button { Text = "Sign in...", AutoSize = true, Dock = DockStyle.Right };

            var header = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 4, 8, 4) };
            header.Controls.Add(_status);
            header.Controls.Add(_signIn);

            // The tooltip colours follow the Windows theme and set the line apart from the
            // status above it; AutoEllipsis shows the whole sentence on hover when it is cut.
            _versionWarning = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 4, 8, 4),
                BackColor = SystemColors.Info,
                ForeColor = SystemColors.InfoText,
                Visible = false,
            };

            _history = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false,
                HideSelection = true,
                ShowItemToolTips = true,
            };
            _history.Columns.Add("When", 120);
            _history.Columns.Add("File", 180);
            _history.Columns.Add("What GLADOS did", 130);
            _history.Columns.Add("Why", 170);
            _history.Columns.Add("By", 130);
            _history.Columns.Add("Details", 400);

            _note = new Label { AutoSize = false, Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 4, 8, 4), AutoEllipsis = true };

            // Docking runs from the last control added to the first, so the header takes the
            // top edge first and the version warning sits right below it.
            Controls.Add(_history);
            Controls.Add(_note);
            Controls.Add(_versionWarning);
            Controls.Add(header);

            _selectionQuiet = new Timer { Interval = SelectionQuietMilliseconds };
            _selectionQuiet.Tick += OnSelectionQuiet;
            _signIn.Click += OnSignInClick;
            VisibleChanged += OnVisibleChanged;

            ShowNote("Select one or more files to see what GLADOS did with them.");
        }

        /// <summary>Shows the current sign-in state and, when credentials are stored, tests
        /// them in the background, then reloads the history of the selection. Called when the
        /// tab is created, whenever it is selected and after the sign-in window changed the
        /// sign-in; each of these means the tab is about to be seen, so the history loads
        /// without the visibility check.</summary>
        public void RefreshStatus()
        {
            OnUi(() =>
            {
                string serverUrl, token;
                if (!GladosCredentials.TryGet(out serverUrl, out token))
                {
                    _testSequence++;
                    HideVersionWarning();
                    _signIn.Text = "Sign in...";
                    _status.Text = "Not signed in. Changes are not sent to GLADOS until you sign in; GLADOS still picks them up on its regular poll.";
                    LoadHistory(true);
                    return;
                }

                _signIn.Text = "Sign-in settings...";
                RunTest(serverUrl, token);
                LoadHistory(true);
            });
        }

        /// <summary>An item was selected in this vault view (EdmCmd_SelectItem). Folders, which
        /// come without a file name, are not tracked.</summary>
        public void SelectItem(string vaultName, int fileId, string fileName, string folderName)
        {
            OnUi(() =>
            {
                if (!string.IsNullOrEmpty(vaultName))
                    _vaultName = vaultName;
                if (fileId <= 0 || string.IsNullOrEmpty(fileName))
                    return;

                if (!string.Equals(_selectionFolder, folderName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    _selected.Clear();
                    _selectionFolder = folderName ?? string.Empty;
                }
                _selected[Key(folderName, fileName)] = fileId;
                SelectionChanged();
            });
        }

        /// <summary>An item was deselected in this vault view (EdmCmd_DeSelectItem, which names
        /// the file and folder but carries no id).</summary>
        public void DeselectItem(string fileName, string folderName)
        {
            OnUi(() =>
            {
                if (string.IsNullOrEmpty(fileName))
                    return;
                if (_selected.Remove(Key(folderName, fileName)))
                    SelectionChanged();
            });
        }

        private static string Key(string folderName, string fileName)
        {
            return (folderName ?? string.Empty) + "\\" + fileName;
        }

        private void SelectionChanged()
        {
            _selectionQuiet.Stop();
            _selectionQuiet.Start();
        }

        private void OnSelectionQuiet(object sender, EventArgs e)
        {
            try
            {
                _selectionQuiet.Stop();
                LoadHistory(false);
            }
            catch (Exception ex)
            {
                Logging.Log("Tab", 0, null, "history refresh failed: " + ex.Message);
            }
        }

        private void OnVisibleChanged(object sender, EventArgs e)
        {
            try
            {
                if (_historyStale && IsShown())
                    LoadHistory(false);
            }
            catch (Exception ex)
            {
                Logging.Log("Tab", 0, null, "history refresh failed: " + ex.Message);
            }
        }

        /// <summary>PDM shows and hides the tab's window itself, by handle, so the native
        /// window state is asked rather than the Visible property WinForms keeps.</summary>
        private bool IsShown()
        {
            return IsHandleCreated && IsWindowVisible(Handle);
        }

        private void LoadHistory(bool shown)
        {
            var sequence = ++_historySequence;
            var fileIds = _selected.Values.Distinct().OrderBy(id => id).ToList();

            if (fileIds.Count == 0)
            {
                _historyStale = false;
                _history.Items.Clear();
                ShowNote("Select one or more files to see what GLADOS did with them.");
                return;
            }

            string serverUrl, token;
            if (!GladosCredentials.TryGet(out serverUrl, out token))
            {
                _historyStale = false;
                _history.Items.Clear();
                ShowNote("Sign in to see what GLADOS did with the selected files.");
                return;
            }

            if (!shown && !IsShown())
            {
                _historyStale = true;
                return;
            }
            _historyStale = false;

            if (string.IsNullOrEmpty(_vaultName))
            {
                ShowNote("The vault of this window is not known yet; select the files again.");
                return;
            }

            var truncated = fileIds.Count > GladosClient.MaxHistoryFiles;
            if (truncated)
                fileIds = fileIds.Take(GladosClient.MaxHistoryFiles).ToList();

            var vaultName = _vaultName;
            ShowNote("Loading the GLADOS sync history...");

            Task.Run(() => GladosClient.GetFileHistoryAsync(vaultName, fileIds)).ContinueWith(task =>
            {
                var result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : FileHistoryResult.Failed("Loading the history failed unexpectedly.");
                PostToUi(() =>
                {
                    if (sequence != _historySequence)
                        return;
                    ShowHistory(result, fileIds.Count, truncated);
                });
            });
        }

        private void ShowHistory(FileHistoryResult result, int fileCount, bool truncated)
        {
            _history.BeginUpdate();
            try
            {
                _history.Items.Clear();
                foreach (var entry in result.Entries)
                    _history.Items.Add(Row(entry));
            }
            finally
            {
                _history.EndUpdate();
            }

            if (!result.Success)
            {
                if (!string.Equals(result.Detail, _lastLoggedFailure, StringComparison.Ordinal))
                {
                    _lastLoggedFailure = result.Detail;
                    Logging.Log("History", 0, null, "history not loaded: " + result.Detail);
                }
                ShowNote(result.Detail);
                return;
            }
            _lastLoggedFailure = null;

            var prefix = truncated ? "Showing the first " + GladosClient.MaxHistoryFiles + " selected files. " : string.Empty;
            if (result.Entries.Count == 0)
                ShowNote(prefix + "GLADOS has no sync history for " + (fileCount == 1 ? "this file" : "these files")
                    + " yet. A change still waiting in GLADOS' queue shows up once it is processed; GLADOS keeps 30 days.");
            else
                ShowNote(prefix + result.Entries.Count + " entries for " + fileCount + (fileCount == 1 ? " file" : " files")
                    + ", newest first.");
        }

        private static ListViewItem Row(FileHistoryEntry entry)
        {
            var details = entry.Message ?? string.Empty;
            if (!string.IsNullOrEmpty(entry.RunError))
                details = details.Length == 0 ? entry.RunError : details + " (" + entry.RunError + ")";

            var item = new ListViewItem(entry.At.HasValue
                ? entry.At.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : string.Empty);
            item.SubItems.Add(entry.Name ?? ("#" + entry.FileId));
            item.SubItems.Add(DescribeAction(entry.Action));
            item.SubItems.Add(DescribeWhy(entry));
            item.SubItems.Add(DescribeBy(entry));
            item.SubItems.Add(details);
            item.ToolTipText = (entry.Path ?? entry.Name) + Environment.NewLine + details;
            return item;
        }

        private static string DescribeAction(string action)
        {
            switch (action)
            {
                case "created": return "Created item";
                case "revised": return "New revision";
                case "updated": return "Updated";
                case "status": return "Status updated";
                case "claimed": return "Linked to item";
                case "skipped": return "Skipped";
                case "conflict": return "Conflict";
                case "missing": return "Marked missing";
                case "error": return "Error";
                case null: return string.Empty;
                default: return Words(action);
            }
        }

        private static string DescribeWhy(FileHistoryEntry entry)
        {
            switch (entry.ChangeReason)
            {
                case "state_change":
                    return string.IsNullOrEmpty(entry.ChangeState) ? "State changed" : "State changed to " + entry.ChangeState;
                case "check_in": return "Checked in";
                case "delete": return "Deleted";
                case "move": return "Moved";
                case "rename": return "Renamed";
                case "resync": return "Resync requested";
                case null: break;
                default: return Words(entry.ChangeReason);
            }

            switch (entry.Trigger)
            {
                case "file_trigger": return "Change reported";
                case "retry": return "Retried in GLADOS";
                case "scheduled": return "Regular sync";
                case "manual": return "Sync started in GLADOS";
                case "initial": return "First sync";
                case "reconciliation": return "Reconciliation";
                case null: return string.Empty;
                default: return Words(entry.Trigger);
            }
        }

        private static string DescribeBy(FileHistoryEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.RequestedBy))
                return entry.RequestedBy;
            switch (entry.Trigger)
            {
                case "scheduled":
                case "initial":
                case "reconciliation":
                    return "GLADOS";
                default:
                    return string.Empty;
            }
        }

        private static string Words(string value)
        {
            var text = value.Replace('_', ' ');
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private void ShowNote(string text)
        {
            _note.Text = text;
        }

        private void OnSignInClick(object sender, EventArgs e)
        {
            try
            {
                bool changed;
                using (var form = new GladosSignInForm())
                {
                    form.ShowDialog(OwnerWindow());
                    changed = form.SignInChanged;
                }
                if (changed)
                    RefreshStatus();
            }
            catch (Exception ex)
            {
                Logging.Log("Tab", 0, null, "sign-in window failed: " + ex.Message);
            }
        }

        private void RunTest(string serverUrl, string token)
        {
            var sequence = ++_testSequence;
            _status.Text = "Checking the stored sign-in...";
            HideVersionWarning();

            Task.Run(() => GladosClient.TestTokenAsync(serverUrl, token)).ContinueWith(task =>
            {
                var result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : TokenTestResult.Unreachable("unexpected failure");
                PostToUi(() =>
                {
                    if (sequence != _testSequence)
                        return;
                    _status.Text = GladosSignInForm.Describe(result, serverUrl);
                    if (result.Success)
                        CheckBackendVersion(serverUrl, token, sequence);
                });
            });
        }

        /// <summary>Asks Glados for its pat contract once the token is known to work and shows
        /// the update warning when the contract is not the supported one. Runs on the thread
        /// pool; a failed call shows nothing (BackendVersionCheck logs it).</summary>
        private void CheckBackendVersion(string serverUrl, string token, int sequence)
        {
            Task.Run(() => BackendVersionCheck.WarningAsync(serverUrl, token)).ContinueWith(task =>
            {
                var warning = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                if (warning == null)
                    return;
                PostToUi(() =>
                {
                    if (sequence != _testSequence)
                        return;
                    _versionWarning.Text = warning;
                    _versionWarning.Visible = true;
                });
            });
        }

        private void HideVersionWarning()
        {
            _versionWarning.Visible = false;
            _versionWarning.Text = string.Empty;
        }

        /// <summary>Runs the action on the control's UI thread, now when already on it, and
        /// catches everything, since the callers are PDM hooks and Explorer events.</summary>
        private void OnUi(Action action)
        {
            if (InvokeRequired)
            {
                PostToUi(action);
                return;
            }
            try
            {
                if (!IsDisposed)
                    action();
            }
            catch (Exception ex)
            {
                Logging.Log("Tab", 0, null, "tab update failed: " + ex.Message);
            }
        }

        /// <summary>Runs the action on the control's UI thread unless the control is already
        /// gone (Explorer window closed while a call was running).</summary>
        private void PostToUi(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated)
                    return;
                BeginInvoke((MethodInvoker)(() =>
                {
                    try
                    {
                        if (!IsDisposed)
                            action();
                    }
                    catch (Exception ex)
                    {
                        Logging.Log("Tab", 0, null, "tab update failed: " + ex.Message);
                    }
                }));
            }
            catch (Exception)
            {
                // The handle was destroyed between the check and BeginInvoke; nothing to show.
            }
        }

        /// <summary>The Explorer window this tab sits in, as the owner of the sign-in window.
        /// The control is hosted in a PDM window by handle, so FindForm has no form to
        /// return.</summary>
        private IWin32Window OwnerWindow()
        {
            var root = GetAncestor(Handle, GaRoot);
            return root != IntPtr.Zero ? new WindowHandle(root) : null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _testSequence++;
                _historySequence++;
                _selectionQuiet.Dispose();
            }
            base.Dispose(disposing);
        }

        private const uint GaRoot = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        private sealed class WindowHandle : IWin32Window
        {
            public WindowHandle(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}
