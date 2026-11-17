using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GladosPdmAddin
{
    /// <summary>
    /// The sign-in window the Glados tab opens with its sign-in button: where a Windows user
    /// enters the Glados server URL and their personal access token, tests them, and signs out.
    /// Shown modally with the Explorer window as owner, so it stays in front of the vault view
    /// it belongs to. Built in code rather than with the designer, since it is a handful of
    /// controls. The stored token is never shown again; an empty token field on save keeps the
    /// stored one, so changing only the URL needs no new token. Saving always stores what was
    /// entered and then tests it, so a sign-in on a day Glados is down still sticks. A token
    /// that passes the test is followed by the backend version check (BackendVersionCheck),
    /// whose update warning, if any, is added below the result. This window
    /// runs inside explorer.exe, where an unhandled exception in an event handler would take
    /// Explorer down, so every handler catches everything and the token test runs off the UI
    /// thread and reports back through BeginInvoke. SignInChanged tells the caller whether it
    /// has to refresh, since it reads the stored sign-in itself.
    /// </summary>
    internal sealed class GladosSignInForm : Form
    {
        private const string TokenHint =
            "Create the token in GLADOS under Account, Personal Access Tokens, Create PDM Add-in Token.";

        private readonly TextBox _serverUrl;
        private readonly TextBox _token;
        private readonly Button _save;
        private readonly Button _signOut;
        private readonly Label _status;

        // Incremented on every test, so a slow answer that arrives after a newer test, a
        // sign-out or the window closing cannot overwrite the status line.
        private int _testSequence;

        public bool SignInChanged { get; private set; }

        public GladosSignInForm()
        {
            Text = "GLADOS sign-in";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
                Dock = DockStyle.Fill,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _serverUrl = new TextBox { Width = 360, Anchor = AnchorStyles.Left };
            _token = new TextBox { Width = 360, Anchor = AnchorStyles.Left, UseSystemPasswordChar = true };
            _save = new Button { Text = "Save and test", AutoSize = true };
            _signOut = new Button { Text = "Sign out", AutoSize = true };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            _status = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Padding = new Padding(0, 8, 0, 0) };

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            buttons.Controls.Add(_save);
            buttons.Controls.Add(_signOut);
            buttons.Controls.Add(close);

            layout.Controls.Add(Caption("Server URL"), 0, 0);
            layout.Controls.Add(_serverUrl, 1, 0);
            layout.Controls.Add(Caption("Personal access token"), 0, 1);
            layout.Controls.Add(_token, 1, 1);
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(360, 0), Text = TokenHint }, 1, 2);
            layout.Controls.Add(buttons, 1, 3);
            layout.Controls.Add(_status, 1, 4);

            Controls.Add(layout);
            AcceptButton = _save;
            CancelButton = close;

            _save.Click += OnSaveClick;
            _signOut.Click += OnSignOutClick;
            Shown += OnShown;
            FormClosed += (sender, e) => _testSequence++;

            _serverUrl.Text = GladosCredentials.GetServerUrl() ?? string.Empty;
        }

        private void OnShown(object sender, EventArgs e)
        {
            try
            {
                string serverUrl, token;
                if (!GladosCredentials.TryGet(out serverUrl, out token))
                {
                    SetIdle(_serverUrl.Text.Length == 0
                        ? "Not signed in. Enter the GLADOS server URL and your token."
                        : "Not signed in. Enter your token.");
                    _token.Select();
                    return;
                }

                RunTest(serverUrl, token, "Checking the stored sign-in...");
            }
            catch (Exception ex)
            {
                Logging.Log("SignIn", 0, null, "status check failed: " + ex.Message);
            }
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            try
            {
                var serverUrl = _serverUrl.Text.Trim().TrimEnd('/');
                Uri parsed;
                if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out parsed)
                    || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
                {
                    SetIdle("Enter the server URL as http://... or https://...");
                    return;
                }
                if (!ConfirmInsecureServerUrl(parsed))
                {
                    SetIdle("Not saved. Enter the https:// address of the GLADOS server.");
                    return;
                }

                var token = _token.Text.Trim();
                if (token.Length == 0)
                    token = GladosCredentials.GetToken();
                if (string.IsNullOrEmpty(token))
                {
                    SetIdle("Enter your personal access token.");
                    return;
                }

                if (!GladosCredentials.SaveServerUrl(serverUrl) || !GladosCredentials.SaveToken(token))
                {
                    SetIdle("The sign-in could not be saved on this computer. Details are in the add-in log.");
                    return;
                }

                SignInChanged = true;
                _serverUrl.Text = serverUrl;
                _token.Text = string.Empty;
                Logging.Log("SignIn", 0, null, "signed in, server " + serverUrl);
                RunTest(serverUrl, token, "Saved. Checking the token with GLADOS...");
            }
            catch (Exception ex)
            {
                Logging.Log("SignIn", 0, null, "save failed: " + ex.Message);
                SetIdle("Saving failed. Details are in the add-in log.");
            }
        }

        /// <summary>Warns before the token would travel over plain http and lets the user
        /// back out, as the SolidWorks add-in does. A warning rather than a block: an internal
        /// test server without TLS is a legitimate setup. Loopback hosts never leave the
        /// machine, so they pass without asking. Defaults to No.</summary>
        private bool ConfirmInsecureServerUrl(Uri serverUri)
        {
            if (serverUri.Scheme != Uri.UriSchemeHttp || serverUri.IsLoopback)
                return true;

            return MessageBox.Show(
                    this,
                    "The address " + serverUri.GetLeftPart(UriPartial.Authority) + " uses http, not https. "
                        + "Your personal access token would travel unencrypted, and anyone on the network "
                        + "could read it and act as you in GLADOS.\n\n"
                        + "Use https:// if the server supports it. Save the http address anyway?",
                    "GLADOS sign-in",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private void OnSignOutClick(object sender, EventArgs e)
        {
            try
            {
                GladosCredentials.DeleteToken();
                SignInChanged = true;
                _testSequence++;
                _token.Text = string.Empty;
                Logging.Log("SignIn", 0, null, "signed out");
                SetIdle("Signed out. Changes are not sent to GLADOS until you sign in; GLADOS still picks them up on its regular poll.");
            }
            catch (Exception ex)
            {
                Logging.Log("SignIn", 0, null, "sign-out failed: " + ex.Message);
            }
        }

        private void RunTest(string serverUrl, string token, string pendingText)
        {
            var sequence = ++_testSequence;
            _status.Text = pendingText;
            _save.Enabled = false;

            Task.Run(() => GladosClient.TestTokenAsync(serverUrl, token)).ContinueWith(task =>
            {
                var result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : TokenTestResult.Unreachable("unexpected failure");
                PostToUi(() =>
                {
                    if (sequence != _testSequence)
                        return;
                    SetIdle(Describe(result, serverUrl));
                    if (result.Success)
                        CheckBackendVersion(serverUrl, token, sequence);
                });
            });
        }

        /// <summary>Adds the update warning to the status line when Glados serves a pat
        /// contract this add-in does not support. Runs on the thread pool; a failed call adds
        /// nothing (BackendVersionCheck logs it).</summary>
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
                    _status.Text += Environment.NewLine + Environment.NewLine + warning;
                });
            });
        }

        /// <summary>The sentence for a token test result, shared with the tab's status
        /// line.</summary>
        public static string Describe(TokenTestResult result, string serverUrl)
        {
            if (result.Success && result.HasAddinPermission)
                return "Signed in to GLADOS as " + result.UserName + ".";
            if (result.Success)
                return "Signed in as " + result.UserName + ", but this token lacks the use:pdm_addin permission, "
                    + "so GLADOS will refuse its notifications and the sync history stays empty. " + TokenHint;
            if (result.IsRejected)
                return "GLADOS rejected the token: " + Sentence(result.Detail) + " " + TokenHint;
            return "GLADOS could not be reached at " + serverUrl + ": " + Sentence(result.Detail)
                + " The sign-in is kept; check the URL or try again later.";
        }

        /// <summary>Ends a detail with exactly one full stop; exception messages often bring
        /// their own.</summary>
        private static string Sentence(string detail)
        {
            var text = (detail ?? string.Empty).TrimEnd();
            return text.EndsWith(".", StringComparison.Ordinal) ? text : text + ".";
        }

        private void SetIdle(string text)
        {
            _status.Text = text;
            _save.Enabled = true;
        }

        /// <summary>Runs the action on the window's UI thread unless the window is already
        /// gone.</summary>
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
                        Logging.Log("SignIn", 0, null, "status update failed: " + ex.Message);
                    }
                }));
            }
            catch (Exception)
            {
                // The handle was destroyed between the check and BeginInvoke; nothing to show.
            }
        }

        private static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
        }
    }
}
