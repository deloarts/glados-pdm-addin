using System;

namespace GladosPdmAddin
{
    /// <summary>
    /// One row of a file's Glados sync history as the history call returns it: when Glados
    /// handled the file, what it did (Action, with Message saying why) and what started it.
    /// Trigger is the run's trigger (file_trigger, retry, scheduled, manual, initial);
    /// ChangeReason, ChangeState and RequestedBy are only set when a trigger carried them, so a
    /// scheduled poll row has none. Wire values are kept as Glados sends them; the tab turns
    /// them into words. Immutable, built once by GladosClient.
    /// </summary>
    internal sealed class FileHistoryEntry
    {
        public int FileId { get; }
        public string Name { get; }
        public string Path { get; }
        public string Action { get; }
        public string Message { get; }
        public DateTimeOffset? At { get; }
        public string Trigger { get; }
        public string RunError { get; }
        public string ChangeReason { get; }
        public string ChangeState { get; }
        public string RequestedBy { get; }

        public FileHistoryEntry(int fileId, string name, string path, string action, string message, DateTimeOffset? at,
            string trigger, string runError, string changeReason, string changeState, string requestedBy)
        {
            FileId = fileId;
            Name = name;
            Path = path;
            Action = action;
            Message = message;
            At = at;
            Trigger = trigger;
            RunError = runError;
            ChangeReason = changeReason;
            ChangeState = changeState;
            RequestedBy = requestedBy;
        }
    }
}
