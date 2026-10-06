namespace Kurrent.Replicator;

/// <summary>Replication cannot continue in this process; the host should fail so it can be restarted.</summary>
public class ReplicatorFailedException(string message, Exception? innerException) : Exception(message, innerException);
