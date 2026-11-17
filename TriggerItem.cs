namespace GladosPdmAddin
{
    /// <summary>
    /// One changed file as the trigger call sends it: the vault-issued file id plus the path and
    /// version hints Glados re-reads itself, so either may be null, and why the file changed:
    /// Reason is one of the backend's change reasons (state_change, check_in, delete, move,
    /// rename, resync, add, copy) and State the destination state name of a state change, otherwise null.
    /// Glados records both on the file's sync history and refreshes the file the same way
    /// without them. Immutable, so the same instance can sit in a batch and in
    /// TriggerRetryQueue without being copied.
    /// </summary>
    internal sealed class TriggerItem
    {
        public int FileId { get; }
        public string Path { get; }
        public int? Version { get; }
        public string Reason { get; }
        public string State { get; }

        public TriggerItem(int fileId, string path, int? version, string reason, string state)
        {
            FileId = fileId;
            Path = path;
            Version = version;
            Reason = reason;
            State = state;
        }
    }
}
