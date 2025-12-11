// ReSharper disable UnusedAutoPropertyAccessor.Global

#nullable disable
namespace replicator.Settings;

public record EsdbSettings {
    public string ConnectionString { get; init; }
    public string Protocol         { get; init; }
    public int    PageSize         { get; init; } = 1024;
}

public record CheckpointSeeder {
    public string Path { get; init; }

    public string Type { get; init; } = "none"; // "chaser"
}

public record Checkpoint {
    public string           Path            { get; init; }
    public string           Type            { get; init; } = "file";
    public int              CheckpointAfter { get; init; } = 1000;
    public string           Database        { get; init; } = "replicator";
    public string           InstanceId      { get; init; } = "default";
    public CheckpointSeeder Seeder          { get; init; } = new();
}

public record SinkSettings : EsdbSettings {
    public int    PartitionCount                  { get; init; } = 1;
    public string Router                          { get; init; }
    public string Partitioner                     { get; init; }
    public int    BufferSize                      { get; init; } = 1000;
    /// <summary>
    /// When enabled, metadata/system events (e.g. $>, $@) are not used for partition sequencing checks.
    /// This is a diagnostic flag to help investigate ordering issues involving metadata events.
    /// </summary>
    public bool   IgnoreMetadataEventsForPartitioning { get; init; } = false;
}

public record TransformSettings {
    public string Type       { get; init; } = "default";
    public string Config     { get; init; }
    public int    BufferSize { get; init; } = 1;
}

public record Filter {
    public string Type    { get; init; }
    public string Include { get; init; }
    public string Exclude { get; init; }
}

public record Replicator {
    public EsdbSettings      Reader                          { get; init; }
    public SinkSettings      Sink                            { get; init; }
    public bool              Scavenge                        { get; init; }
    public bool              RestartOnFailure                { get; init; } = true;
    public bool              RunContinuously                 { get; init; } = true;
    public long              RestartDelayInMilliseconds      { get; init; } = 5000;
    public int               ReportMetricsFrequencyInSeconds { get; init; } = 5;
    public Checkpoint        Checkpoint                      { get; init; } = new();
    public TransformSettings Transform                       { get; init; } = new();
    public Filter[]          Filters                         { get; init; }
    /// <summary>
    /// Enables high-verbosity sequencing diagnostics (per-event sequence logs,
    /// partition buffer dumps, etc.). Intended for local or non-production
    /// environments only, as it can generate a large volume of logs.
    /// </summary>
    public bool              DebugPartitionSequences         { get; init; } = false;
}

public static class ConfigExtensions {
    public static T GetAs<T>(this IConfiguration configuration) where T : new() {
        T result = new();
        configuration.GetSection(typeof(T).Name).Bind(result);

        return result;
    }
}
