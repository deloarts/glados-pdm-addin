using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GladosPdmAddin
{
    /// <summary>
    /// Holds trigger calls that failed for a reason that should pass on its own
    /// (GladosCallResult.Transient: Glados unreachable or timing out, 502/503/504 while it
    /// restarts or is in maintenance, 429 while the user's trigger budget is spent) and sends them
    /// again in the background after 10 s, 30 s, 1 min, 2 min and then every 5 min, so a short
    /// Glados outage no longer leaves the files it covered to the next scheduled poll. Keyed by
    /// file id, so a file changed again while it waits is sent once, with its newest hints, and a
    /// later successful trigger for it removes it. In memory only, per client process, and capped
    /// at 5000 files: a file still queued when the process exits, one that keeps failing for an
    /// hour, or one beyond the cap is dropped and left to the Glados poller, which picks up every
    /// change regardless. One background task drains the queue while it holds anything, so no
    /// hook ever waits for a retry.
    /// </summary>
    internal static class TriggerRetryQueue
    {
        private const int MaxEntries = 5000;
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
        private static readonly TimeSpan[] Delays =
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
        };
        private static readonly TimeSpan RateLimitedWait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<int, Entry> Pending = new Dictionary<int, Entry>();
        private static bool _draining;
        private static DateTime _lastOverflowWarningUtc = DateTime.MinValue;

        private sealed class Entry
        {
            public string VaultName;
            public TriggerItem Item;
            public int Attempts;
            public DateTime FirstFailedUtc;
            public DateTime NextAttemptUtc;
        }

        /// <summary>Queues files whose trigger call failed transiently. A file already queued
        /// keeps its schedule and takes the newer hints.</summary>
        public static void Add(string vaultName, IList<TriggerItem> items, string reason)
        {
            var dropped = 0;
            lock (SyncRoot)
            {
                var now = DateTime.UtcNow;
                foreach (var item in items)
                {
                    Entry existing;
                    if (Pending.TryGetValue(item.FileId, out existing))
                    {
                        // A new entry on the old schedule, so a resend already in flight
                        // with the old hints does not remove it (see Record).
                        Pending[item.FileId] = new Entry
                        {
                            VaultName = vaultName,
                            Item = item,
                            Attempts = existing.Attempts,
                            FirstFailedUtc = existing.FirstFailedUtc,
                            NextAttemptUtc = existing.NextAttemptUtc,
                        };
                        continue;
                    }
                    if (Pending.Count >= MaxEntries)
                    {
                        dropped++;
                        continue;
                    }
                    Pending[item.FileId] = new Entry
                    {
                        VaultName = vaultName,
                        Item = item,
                        Attempts = 0,
                        FirstFailedUtc = now,
                        NextAttemptUtc = now + Delays[0],
                    };
                }
                StartDrainIfIdle();
            }

            Logging.Log("Retry", 0, null, (items.Count - dropped) + " file(s) queued for retry after: " + reason);
            if (dropped > 0)
                WarnOverflow(dropped);
        }

        /// <summary>Removes files a successful trigger call has just covered.</summary>
        public static void Forget(IList<TriggerItem> items)
        {
            lock (SyncRoot)
            {
                foreach (var item in items)
                    Pending.Remove(item.FileId);
            }
        }

        private static void StartDrainIfIdle()
        {
            if (_draining)
                return;
            _draining = true;
            Task.Run(() => DrainAsync());
        }

        private static async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    string vaultName;
                    List<Entry> due;
                    TimeSpan wait;
                    lock (SyncRoot)
                    {
                        var now = DateTime.UtcNow;
                        DropExpired(now);
                        if (Pending.Count == 0)
                        {
                            _draining = false;
                            return;
                        }
                        due = TakeDue(now, out vaultName);
                        wait = due.Count == 0 ? TimeUntilNextDue(now) : TimeSpan.Zero;
                    }

                    if (due.Count == 0)
                    {
                        await Task.Delay(wait).ConfigureAwait(false);
                        continue;
                    }

                    if (!RateLimiter.TryAcquire(due.Count))
                    {
                        await Task.Delay(RateLimitedWait).ConfigureAwait(false);
                        continue;
                    }

                    var items = new List<TriggerItem>();
                    foreach (var entry in due)
                        items.Add(entry.Item);
                    var result = await GladosClient.TriggerBatchAsync(vaultName, items).ConfigureAwait(false);
                    Record(due, result);
                    Logging.Log("Retry", 0, null, "resent " + due.Count + " file(s): " + result.Describe());
                }
            }
            catch (Exception ex)
            {
                lock (SyncRoot)
                {
                    _draining = false;
                }
                Logging.Log("Retry", 0, null, "retry loop stopped: " + ex.Message);
            }
        }

        /// <summary>Up to one batch of entries that are due, all for the same vault.</summary>
        private static List<Entry> TakeDue(DateTime now, out string vaultName)
        {
            vaultName = null;
            var due = new List<Entry>();
            foreach (var entry in Pending.Values)
            {
                if (entry.NextAttemptUtc > now)
                    continue;
                if (vaultName == null)
                    vaultName = entry.VaultName;
                else if (!string.Equals(vaultName, entry.VaultName, StringComparison.OrdinalIgnoreCase))
                    continue;
                due.Add(entry);
                if (due.Count >= GladosClient.MaxTriggerBatch)
                    break;
            }
            return due;
        }

        private static TimeSpan TimeUntilNextDue(DateTime now)
        {
            var next = DateTime.MaxValue;
            foreach (var entry in Pending.Values)
            {
                if (entry.NextAttemptUtc < next)
                    next = entry.NextAttemptUtc;
            }
            var wait = next - now;
            return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
        }

        /// <summary>Removes entries the call delivered or can never deliver, and schedules the
        /// next attempt for the rest. An entry Add replaced while the call was in flight stays
        /// queued, since its newer hints have not been sent yet.</summary>
        private static void Record(List<Entry> sent, GladosCallResult result)
        {
            lock (SyncRoot)
            {
                var now = DateTime.UtcNow;
                foreach (var entry in sent)
                {
                    Entry current;
                    if (!Pending.TryGetValue(entry.Item.FileId, out current) || !ReferenceEquals(current, entry))
                        continue;
                    if (result.Success || !result.Transient)
                    {
                        Pending.Remove(entry.Item.FileId);
                        continue;
                    }
                    entry.Attempts++;
                    entry.NextAttemptUtc = now + Delays[Math.Min(entry.Attempts, Delays.Length - 1)];
                }
            }
        }

        private static void DropExpired(DateTime now)
        {
            var expired = new List<int>();
            foreach (var pair in Pending)
            {
                if (now - pair.Value.FirstFailedUtc > MaxAge)
                    expired.Add(pair.Key);
            }
            foreach (var fileId in expired)
                Pending.Remove(fileId);
            if (expired.Count > 0)
                Logging.Log("Retry", 0, null, "gave up on " + expired.Count + " file(s) after an hour, poller catches up");
        }

        private static void WarnOverflow(int dropped)
        {
            var now = DateTime.UtcNow;
            lock (SyncRoot)
            {
                if (now - _lastOverflowWarningUtc < WarningInterval)
                    return;
                _lastOverflowWarningUtc = now;
            }
            Logging.Log("Retry", 0, null, "retry queue full, " + dropped + " file(s) left to the poller");
        }
    }
}
