namespace Kurrent.Replicator.Shared.Observe;

/// <summary>
/// Holds runtime debug switches that control high-verbosity diagnostic logging
/// across the replicator components.
/// </summary>
public static class ReplicationDebugOptions {
    /// <summary>
    /// When enabled, partitions, readers and writers will emit detailed
    /// per-event sequencing logs (buffer dumps, sequence transitions, etc.).
    /// Intended for local troubleshooting only.
    /// </summary>
    public static bool DebugPartitionSequences { get; set; }
}
