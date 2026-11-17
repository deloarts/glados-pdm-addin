using System;
using System.Net;
using System.Net.Http;

namespace GladosPdmAddin
{
    /// <summary>
    /// Outcome of a GladosClient.TriggerBatchAsync call, carrying just enough detail for one log
    /// line and the retry decision. The trigger endpoint answers 202 on success. Transient marks
    /// a failure that should pass on its own (Glados unreachable or timing out, the proxy
    /// answering 502/503/504 while the backend restarts or is in maintenance, or 429 when
    /// the user's trigger budget is spent), which TriggerRetryQueue sends again later. A
    /// rejected or expired token, a missing sign-in, a disabled integration, a wrong vault name
    /// or a rejected body is permanent and only logged, since sending it again cannot succeed.
    /// </summary>
    internal sealed class GladosCallResult
    {
        public bool Success { get; }
        public bool Transient { get; }
        public string Detail { get; }

        private GladosCallResult(bool success, bool transient, string detail)
        {
            Success = success;
            Transient = transient;
            Detail = detail;
        }

        /// <param name="redirectTarget">Where a 3xx answer points; such an answer is permanent,
        /// since the stored server URL is wrong until the user enters the new one.</param>
        public static GladosCallResult FromStatus(HttpStatusCode statusCode, string redirectTarget = null)
        {
            var code = (int)statusCode;
            var transient = code == 429 || code == 502 || code == 503 || code == 504;
            var detail = "HTTP " + code;
            if (redirectTarget != null)
                detail += ", GLADOS redirects to " + redirectTarget + ", enter that address on the GLADOS tab";
            return new GladosCallResult(statusCode == HttpStatusCode.Accepted, transient, detail);
        }

        /// <summary>No server URL or token stored for this Windows user; no request was
        /// sent.</summary>
        public static GladosCallResult NotSignedIn()
        {
            return new GladosCallResult(false, false, "not signed in to GLADOS");
        }

        /// <summary>A connection failure (HttpRequestException) or a timeout (the per-call
        /// CancellationTokenSource surfaces as an OperationCanceledException) is transient; any
        /// other exception points at a local problem such as a malformed base URL and is
        /// not.</summary>
        public static GladosCallResult FromException(Exception ex)
        {
            var transient = ex is HttpRequestException || ex is OperationCanceledException;
            return new GladosCallResult(false, transient, ex.GetType().Name + ": " + ex.Message);
        }

        /// <summary>One-line summary for Logging.Log, e.g. "ok HTTP 202" or "failed
        /// TaskCanceledException: A task was canceled.".</summary>
        public string Describe()
        {
            return (Success ? "ok " : "failed ") + Detail;
        }
    }
}
