using System.Collections.Generic;

namespace GladosPdmAddin
{
    /// <summary>
    /// Outcome of GladosClient.GetFileHistoryAsync, shaped for the history list on the Glados
    /// tab. Success carries the entries, possibly none. Otherwise Detail is a sentence the tab
    /// shows as it is, and IsPermanent tells the tab that asking again will not help until the
    /// user changes something (a missing sign-in, a rejected token, a token without
    /// use:pdm_addin, a wrong vault or a disabled integration), as opposed to Glados being
    /// unreachable for a moment. Never retried either way: the next selection asks again.
    /// </summary>
    internal sealed class FileHistoryResult
    {
        public bool Success { get; private set; }
        public bool IsPermanent { get; private set; }
        public string Detail { get; private set; }
        public IList<FileHistoryEntry> Entries { get; private set; }

        public static FileHistoryResult Loaded(IList<FileHistoryEntry> entries)
        {
            return new FileHistoryResult { Success = true, Entries = entries };
        }

        public static FileHistoryResult Refused(string detail)
        {
            return new FileHistoryResult { IsPermanent = true, Detail = detail, Entries = new FileHistoryEntry[0] };
        }

        public static FileHistoryResult Failed(string detail)
        {
            return new FileHistoryResult { Detail = detail, Entries = new FileHistoryEntry[0] };
        }
    }
}
