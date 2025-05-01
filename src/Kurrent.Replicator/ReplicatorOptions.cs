namespace Kurrent.Replicator; 

public record ReplicatorOptions(bool RestartOnFailure, bool RunContinuously, TimeSpan RestartDelay, TimeSpan ReportMetricsFrequency);