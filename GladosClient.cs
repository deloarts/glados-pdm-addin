using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GladosPdmAddin
{
    /// <summary>
    /// Thin HTTP client for the four Glados endpoints this add-in calls: the fire-and-forget
    /// file-changed trigger and the file history read the Glados tab shows for the selected
    /// files, both on the personal access token surface (glados backend/src/modules/
    /// external_data/adapters/inbound/api/v1/pat/endpoints/pdm.py), the token test the
    /// sign-in window runs (backend/src/modules/platform/adapters/inbound/api/v1/auth/
    /// endpoints/pat_login.py), and the backend version read after a successful token test
    /// (backend/src/modules/platform/adapters/inbound/api/v1/pat/endpoints/version.py). Server URL
    /// and token come from GladosCredentials on every call and travel in the X-Api-Key header
    /// of each request rather than as a default header, so a new sign-in takes effect at once
    /// and a signed-out user's call never leaves the machine. JSON is built and parsed by hand
    /// with JavaScriptSerializer (System.Web.Extensions, part of .NET Framework, no NuGet
    /// dependency); the wire field names already match the plain snake_case the backend's
    /// Pydantic schemas use, so no naming translation is needed in either direction. One
    /// static HttpClient instance is reused for the process lifetime, as recommended for
    /// HttpClient; per-call timeouts are enforced with a CancellationTokenSource instead of
    /// HttpClient.Timeout, since the trigger (5 s) and the token test (10 s) need different
    /// budgets from the same client.
    /// </summary>
    internal static class GladosClient
    {
        private const string TokenHeaderName = "X-Api-Key";
        private const string TriggerPath = "api/v1/pat/pdm/files/changed/batch";
        private const string TestTokenPath = "api/v1/auth/login/test-personal-access-token";
        private const string HistoryPath = "api/v1/pat/pdm/files/history";
        private const string VersionPath = "api/v1/pat/version";
        private const string AddinPermission = "use:pdm_addin";

        /// <summary>The pat contract minor this add-in was built and tested against, the
        /// pat_contract a supported backend reports (backend 0.1.x serves "0.1"). Raise it
        /// together with the CHANGELOG's supported backend range once the add-in has been
        /// checked against a new backend minor.</summary>
        public const string SupportedPatContract = "0.1";

        /// <summary>Most entries one history call asks for across all its files.</summary>
        private const int HistoryLimit = 50;

        private static readonly HttpClient Http = CreateHttpClient();

        /// <summary>TLS 1.2 is pinned on the client's own handler rather than inherited from
        /// ServicePointManager.SecurityProtocol: that setting is process-wide, and inside
        /// explorer.exe any shell extension or other add-in can narrow it, which a server that
        /// refuses older protocols answers with "Could not create SSL/TLS secure channel". TLS
        /// 1.3 is left out because Windows 10's Schannel does not support it. Redirects are not
        /// followed: HttpClientHandler would send the X-Api-Key header on to whatever host the
        /// redirect names, over http as well, and a correct Glados URL never redirects. A 3xx
        /// answer is reported with its target instead (RedirectTarget), so the user can enter
        /// that address.</summary>
        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12, AllowAutoRedirect = false };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        /// <summary>Most files one trigger call may carry; the backend rejects a larger
        /// batch.</summary>
        public const int MaxTriggerBatch = 100;

        /// <summary>Most files one history call may name; the backend rejects more.</summary>
        public const int MaxHistoryFiles = 100;

        /// <summary>
        /// Fire-and-forget file-changed trigger for up to MaxTriggerBatch files, POST
        /// api/v1/pat/pdm/files/changed/batch. Never throws; the result carries what Logging.Log
        /// and TriggerRetryQueue need, and is a permanent failure without a request when nobody
        /// is signed in. Five second timeout for the whole batch. vault_name and every file_id
        /// are always sent; path and version are hints Glados re-reads itself, and reason and
        /// state only label the file's history, so each is omitted when not available instead
        /// of sending a placeholder.
        /// </summary>
        public static async Task<GladosCallResult> TriggerBatchAsync(string vaultName, IList<TriggerItem> items)
        {
            string serverUrl, token;
            if (!GladosCredentials.TryGet(out serverUrl, out token))
                return GladosCallResult.NotSignedIn();

            var files = new List<object>();
            foreach (var item in items)
            {
                var file = new Dictionary<string, object> { { "file_id", item.FileId } };
                if (!string.IsNullOrEmpty(item.Path))
                    file["path"] = item.Path;
                if (item.Version.HasValue)
                    file["version"] = item.Version.Value;
                if (!string.IsNullOrEmpty(item.Reason))
                    file["reason"] = item.Reason;
                if (!string.IsNullOrEmpty(item.State))
                    file["state"] = item.State;
                files.Add(file);
            }
            var body = new Dictionary<string, object>
            {
                { "vault_name", vaultName },
                { "files", files },
            };

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    var json = new JavaScriptSerializer().Serialize(body);
                    using (var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(serverUrl, TriggerPath)))
                    {
                        request.Headers.Add(TokenHeaderName, token);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        using (var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false))
                        {
                            return GladosCallResult.FromStatus(response.StatusCode, RedirectTarget(response, TriggerPath));
                        }
                    }
                }
                catch (Exception ex)
                {
                    return GladosCallResult.FromException(ex);
                }
            }
        }

        /// <summary>
        /// Checks a server URL and token the user just entered, POST
        /// api/v1/auth/login/test-personal-access-token, no body. Glados answers with the
        /// token's user and the token's own permission subset, so the result also says whether
        /// the token can send triggers at all. Takes its arguments instead of reading
        /// GladosCredentials, since the tab tests what is in its fields. Ten second budget,
        /// never throws, never retried.
        /// </summary>
        public static async Task<TokenTestResult> TestTokenAsync(string serverUrl, string token)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(serverUrl, TestTokenPath)))
                    {
                        request.Headers.Add(TokenHeaderName, token);
                        using (var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false))
                        {
                            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var code = (int)response.StatusCode;
                            if (code == 401 || code == 403)
                                return TokenTestResult.Rejected(ReadDetail(raw) ?? "HTTP " + code);
                            var redirect = RedirectTarget(response, TestTokenPath);
                            if (redirect != null)
                                return TokenTestResult.Unreachable("it redirects to " + redirect + ", enter that address instead");
                            if (!response.IsSuccessStatusCode)
                                return TokenTestResult.Unreachable("HTTP " + code);

                            return ParseUser(raw);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return TokenTestResult.Unreachable("no answer within 10 seconds");
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    return TokenTestResult.Unreachable(inner);
                }
            }
        }

        /// <summary>
        /// Reads the Glados sync history of up to MaxHistoryFiles files, POST
        /// api/v1/pat/pdm/files/history, merged and newest first, at most HistoryLimit
        /// entries. Read-only on both sides. Credentials come from GladosCredentials like the
        /// trigger's. Ten second budget, never throws, never retried: a failure is returned
        /// for the tab to show, and the next selection simply asks again.
        /// </summary>
        public static async Task<FileHistoryResult> GetFileHistoryAsync(string vaultName, IList<int> fileIds)
        {
            string serverUrl, token;
            if (!GladosCredentials.TryGet(out serverUrl, out token))
                return FileHistoryResult.Refused("Not signed in to GLADOS.");

            var body = new Dictionary<string, object>
            {
                { "vault_name", vaultName },
                { "file_ids", fileIds },
                { "limit", HistoryLimit },
            };

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    var json = new JavaScriptSerializer().Serialize(body);
                    using (var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(serverUrl, HistoryPath)))
                    {
                        request.Headers.Add(TokenHeaderName, token);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        using (var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false))
                        {
                            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var code = (int)response.StatusCode;
                            if (code == 200)
                                return ParseHistory(raw);
                            if (code == 401)
                                return FileHistoryResult.Refused("GLADOS rejected the token: " + (ReadDetail(raw) ?? "HTTP 401") + ".");
                            if (code == 403)
                                return FileHistoryResult.Refused("This token cannot read the sync history (it lacks the " + AddinPermission
                                    + " permission). Create a new PDM add-in token once an administrator has granted it to you.");
                            if (code == 400)
                                return FileHistoryResult.Refused("GLADOS does not know the vault \"" + vaultName + "\".");
                            if (code == 409)
                                return FileHistoryResult.Refused("The SolidWorks PDM integration is switched off in GLADOS.");
                            if (code == 404)
                                return FileHistoryResult.Refused("This GLADOS version does not offer the sync history yet.");
                            var redirect = RedirectTarget(response, HistoryPath);
                            if (redirect != null)
                                return FileHistoryResult.Refused("GLADOS redirects to " + redirect + ". Enter that address in the sign-in window.");
                            return FileHistoryResult.Failed("GLADOS answered HTTP " + code + ".");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return FileHistoryResult.Failed("GLADOS did not answer within 10 seconds.");
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    return FileHistoryResult.Failed("GLADOS could not be reached: " + inner);
                }
            }
        }

        /// <summary>
        /// Reads the backend version and the pat contract minor it serves, GET
        /// api/v1/pat/version. Any valid token may call it. Takes its arguments like the token
        /// test, since it runs right after one. Ten second budget, never throws, never retried.
        /// Anything but a 200 with a pat_contract is Unknown, never a mismatch: an older Glados
        /// without the route answers 404, which says nothing about the contract.
        /// </summary>
        public static async Task<BackendVersionResult> GetBackendVersionAsync(string serverUrl, string token)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(serverUrl, VersionPath)))
                    {
                        request.Headers.Add(TokenHeaderName, token);
                        using (var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false))
                        {
                            var code = (int)response.StatusCode;
                            if (code == 404)
                                return BackendVersionResult.Unknown("this GLADOS offers no version route (HTTP 404)");
                            if (code != 200)
                                return BackendVersionResult.Unknown("HTTP " + code);

                            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            return ParseVersion(raw);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return BackendVersionResult.Unknown("no answer within 10 seconds");
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    return BackendVersionResult.Unknown(inner);
                }
            }
        }

        private static BackendVersionResult ParseVersion(string raw)
        {
            try
            {
                var body = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                if (body == null)
                    return BackendVersionResult.Unknown("unexpected answer");

                var contract = Text(body, "pat_contract");
                if (contract == null)
                    return BackendVersionResult.Unknown("the answer names no pat contract");
                return BackendVersionResult.Read(Text(body, "version") ?? contract + ".x", contract);
            }
            catch (Exception)
            {
                return BackendVersionResult.Unknown("unexpected answer");
            }
        }

        private static FileHistoryResult ParseHistory(string raw)
        {
            try
            {
                var body = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                object value;
                var rows = body != null && body.TryGetValue("entries", out value) ? value as object[] : null;
                if (rows == null)
                    return FileHistoryResult.Failed("GLADOS sent an unexpected answer.");

                var entries = new List<FileHistoryEntry>();
                foreach (var row in rows)
                {
                    var entry = row as Dictionary<string, object>;
                    if (entry == null)
                        continue;
                    int fileId;
                    if (!int.TryParse(Convert.ToString(Field(entry, "file_id"), CultureInfo.InvariantCulture), out fileId))
                        continue;

                    entries.Add(new FileHistoryEntry(
                        fileId,
                        Text(entry, "name"),
                        Text(entry, "path"),
                        Text(entry, "action"),
                        Text(entry, "message"),
                        Time(entry, "created_at"),
                        Text(entry, "trigger"),
                        Text(entry, "run_error"),
                        Text(entry, "change_reason"),
                        Text(entry, "change_state"),
                        Text(entry, "requested_by_name")));
                }
                return FileHistoryResult.Loaded(entries);
            }
            catch (Exception)
            {
                return FileHistoryResult.Failed("GLADOS sent an unexpected answer.");
            }
        }

        private static object Field(Dictionary<string, object> entry, string key)
        {
            object value;
            return entry.TryGetValue(key, out value) ? value : null;
        }

        private static string Text(Dictionary<string, object> entry, string key)
        {
            var text = Field(entry, key) as string;
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>The backend writes ISO 8601 timestamps with an offset; anything else is
        /// treated as unknown rather than guessed.</summary>
        private static DateTimeOffset? Time(Dictionary<string, object> entry, string key)
        {
            DateTimeOffset parsed;
            var text = Text(entry, key);
            if (text != null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
                return parsed;
            return null;
        }

        private static TokenTestResult ParseUser(string raw)
        {
            try
            {
                var user = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                if (user == null)
                    return TokenTestResult.Unreachable("unexpected answer");

                object value;
                var name = user.TryGetValue("full_name", out value) ? value as string : null;
                if (string.IsNullOrWhiteSpace(name))
                    name = user.TryGetValue("username", out value) ? value as string : null;

                var hasAddinPermission = false;
                var permissions = user.TryGetValue("permissions", out value) ? value as object[] : null;
                if (permissions != null)
                {
                    foreach (var permission in permissions)
                    {
                        if (string.Equals(permission as string, AddinPermission, StringComparison.Ordinal))
                            hasAddinPermission = true;
                    }
                }

                return TokenTestResult.Accepted(name ?? "unknown user", hasAddinPermission);
            }
            catch (Exception)
            {
                return TokenTestResult.Unreachable("unexpected answer");
            }
        }

        /// <summary>FastAPI's error body is {"detail": "..."}; the PAT check puts its reason
        /// there ("Personal access token has expired", "... has been revoked").</summary>
        private static string ReadDetail(string raw)
        {
            try
            {
                var body = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                object detail;
                if (body != null && body.TryGetValue("detail", out detail))
                    return detail as string;
            }
            catch (Exception)
            {
                // Not JSON; the status code alone has to do.
            }
            return null;
        }

        /// <summary>For a 3xx answer with a Location header, the server address the redirect
        /// points at: scheme, host, port and any base path, with the endpoint path the request
        /// used stripped again and without user info, so it can be entered as the server URL as
        /// it stands. Null for any other answer.</summary>
        private static string RedirectTarget(HttpResponseMessage response, string relativePath)
        {
            try
            {
                var code = (int)response.StatusCode;
                var location = response.Headers.Location;
                if (code < 300 || code > 399 || location == null)
                    return null;
                if (!location.IsAbsoluteUri)
                    location = new Uri(response.RequestMessage.RequestUri, location);

                var path = location.AbsolutePath;
                var suffix = "/" + relativePath;
                path = path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    ? path.Substring(0, path.Length - suffix.Length)
                    : string.Empty;
                return location.Scheme + "://" + location.Authority + path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Builds an absolute URL from the stored server URL and a path with no
        /// leading slash, without parsing the server URL as a Uri ahead of time, so a malformed
        /// value surfaces inside the callers' own try/catch blocks.</summary>
        private static string BuildUrl(string serverUrl, string relativePath)
        {
            return serverUrl.TrimEnd('/') + "/" + relativePath;
        }
    }
}
