namespace GladosPdmAddin
{
    /// <summary>
    /// Outcome of GladosClient.TestTokenAsync, shaped for the status line of the Glados tab
    /// rather than for logging. Rejected (401/403: wrong, expired or revoked token) and
    /// Unreachable (anything else that is not a 200) are kept apart because they ask the user
    /// for different things: a new token versus checking the server URL or waiting for Glados.
    /// HasAddinPermission reflects the token's own permission subset, which Glados reports in
    /// place of the user's permissions for a token request, so a SolidWorks add-in token pasted
    /// here by mistake is caught at sign-in instead of failing silently on every trigger and
    /// history read. use:pdm_addin is the one permission both need.
    /// </summary>
    internal sealed class TokenTestResult
    {
        public bool Success { get; private set; }
        public bool IsRejected { get; private set; }
        public string UserName { get; private set; }
        public bool HasAddinPermission { get; private set; }
        public string Detail { get; private set; }

        public static TokenTestResult Accepted(string userName, bool hasAddinPermission)
        {
            return new TokenTestResult { Success = true, UserName = userName, HasAddinPermission = hasAddinPermission };
        }

        public static TokenTestResult Rejected(string detail)
        {
            return new TokenTestResult { IsRejected = true, Detail = detail };
        }

        public static TokenTestResult Unreachable(string detail)
        {
            return new TokenTestResult { Detail = detail };
        }
    }
}
