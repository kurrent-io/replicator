using System;

namespace Kurrent.Replicator.Shared.Observe;

/// <summary>
/// Holds contextual information about the current replication run.
/// A new RunId is generated at the start of each Replicator execution
/// and is used to correlate logs across components.
/// </summary>
public static class ReplicationRun {
    /// <summary>
    /// Identifier of the current replication run.
    /// </summary>
    public static Guid RunId { get; private set; }

    /// <summary>
    /// Starts a new replication run by generating a fresh RunId.
    /// </summary>
    public static void StartNew() => RunId = Guid.NewGuid();
}
