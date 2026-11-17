using System;
using System.Collections.Generic;

namespace GladosPdmAddin
{
    /// <summary>
    /// In-process sliding-window limiter for the trigger call: 300 files per 60 seconds,
    /// matching the backend's own trigger limit (PdmTriggerRateLimiter on the Glados side, which
    /// counts every file of a batch) so a large batch operation (thousands of files moved or
    /// re-stated at once) backs off locally instead of spending the call budget on 429
    /// responses. Only TriggerHook and TriggerRetryQueue consume this.
    /// </summary>
    internal static class RateLimiter
    {
        private const int MaxCallsPerWindow = 300;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

        private static readonly object SyncRoot = new object();
        private static readonly Queue<DateTime> CallTimestampsUtc = new Queue<DateTime>();
        private static DateTime _lastWarningUtc = DateTime.MinValue;

        /// <summary>Reserves one call against the current window and returns true, or returns
        /// false when the window is exhausted. Logs "rate limit reached, poller catches up" at
        /// most once per minute while calls keep being denied, rather than once per denied
        /// file.</summary>
        public static bool TryAcquire()
        {
            return TryAcquire(1);
        }

        /// <summary>Reserves `count` calls at once, all or nothing, the way the backend counts
        /// every file of a batch trigger. TriggerRetryQueue uses it for a whole retry
        /// batch.</summary>
        public static bool TryAcquire(int count)
        {
            lock (SyncRoot)
            {
                var now = DateTime.UtcNow;
                while (CallTimestampsUtc.Count > 0 && now - CallTimestampsUtc.Peek() > Window)
                    CallTimestampsUtc.Dequeue();

                if (CallTimestampsUtc.Count + count > MaxCallsPerWindow)
                {
                    if (now - _lastWarningUtc >= WarningInterval)
                    {
                        _lastWarningUtc = now;
                        Logging.Log("RateLimiter", 0, null, "rate limit reached, poller catches up");
                    }
                    return false;
                }

                for (var i = 0; i < count; i++)
                    CallTimestampsUtc.Enqueue(now);
                return true;
            }
        }
    }
}
