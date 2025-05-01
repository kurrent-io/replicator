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
    public int    PartitionCount { get; init; } = 1;
    public string Router         { get; init; }
    public string Partitioner    { get; init; }
    public int    BufferSize     { get; init; } = 1000;
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
    public int               RestartDelayInSeconds           { get; init; } = 5;
    public int               ReportMetricsFrequencyInSeconds { get; init; } = 5;
    public Checkpoint        Checkpoint                      { get; init; } = new();
    public TransformSettings Transform                       { get; init; } = new();
    public Filter[]          Filters                         { get; init; }
}

public static class ConfigExtensions {
    public static T GetAs<T>(this IConfiguration configuration) where T : new() {
        T result = new();
        configuration.GetSection(typeof(T).Name).Bind(result);

        return result;
    }
}
