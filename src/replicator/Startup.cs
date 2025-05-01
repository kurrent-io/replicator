using Kurrent.Replicator;
using Kurrent.Replicator.EventStore;
using Kurrent.Replicator.Js;
using Kurrent.Replicator.Kafka;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.Mongo;
using Kurrent.Replicator.Prepare;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Sink;
using Prometheus;
using replicator.HttpApi;
using replicator.Settings;
using Ensure = Kurrent.Replicator.Shared.Ensure;
using Replicator = replicator.Settings.Replicator;

namespace replicator;

static class Startup {
    public static void ConfigureServices(WebApplicationBuilder builder) {
        Measurements.ConfigureMetrics(builder.Environment.EnvironmentName);

        var replicatorOptions = builder.Configuration.GetAs<Replicator>();

        var services = builder.Services;

        services.AddSingleton<Factory>();

        services.AddSingleton<IConfigurator, TcpConfigurator>(_ => new(replicatorOptions.Reader.PageSize));
        services.AddSingleton<IConfigurator, GrpcConfigurator>();
        services.AddSingleton<IConfigurator, KafkaConfigurator>(_ => new(replicatorOptions.Sink.Router));

        services.AddSingleton(sp => sp.GetRequiredService<Factory>()
            .GetReader(
                replicatorOptions.Reader.Protocol,
                Ensure.NotEmpty(replicatorOptions.Reader.ConnectionString, "Reader connection string")
            )
        );

        services.AddSingleton(sp => sp.GetRequiredService<Factory>()
            .GetWriter(
                replicatorOptions.Sink.Protocol,
                Ensure.NotEmpty(replicatorOptions.Sink.ConnectionString, "Sink connection string")
            )
        );

        services.AddSingleton(sp =>
            new PreparePipelineOptions(
                EventFilters.GetFilter(replicatorOptions, sp.GetRequiredService<IEventReader>()),
                Transformers.GetTransformer(replicatorOptions),
                1,
                replicatorOptions.Transform?.BufferSize ?? 1
            )
        );

        services.AddSingleton(
            new SinkPipeOptions(
                replicatorOptions.Sink.PartitionCount,
                replicatorOptions.Sink.BufferSize,
                FunctionLoader.LoadFile(replicatorOptions.Sink.Partitioner, "Partitioner")
            )
        );

        services.AddSingleton(
            new ReplicatorOptions(
                replicatorOptions.RestartOnFailure,
                replicatorOptions.RunContinuously,
                TimeSpan.FromSeconds(replicatorOptions.RestartDelayInSeconds),
                TimeSpan.FromSeconds(replicatorOptions.ReportMetricsFrequencyInSeconds)
            )
        );

        RegisterCheckpointStore(replicatorOptions.Checkpoint, services);
        RegisterCheckpointSeeder(replicatorOptions.Checkpoint.Seeder, services);
        services.AddHostedService<ReplicatorService>();
        services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<CountersKeep>();

        services.AddSpaStaticFiles(configuration => configuration.RootPath = "ClientApp/dist");
        services.AddControllers();
        services.AddCors();
    }

    public static void Configure(WebApplication app) {
        app.UseDeveloperExceptionPage();

        app.UseCors(cfg => {
                cfg.AllowAnyMethod();
                cfg.AllowAnyOrigin();
                cfg.AllowAnyHeader();
            }
        );

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseSpaStaticFiles();

        app.MapControllers();
        app.MapMetrics();

        app.UseSpa(spa => spa.Options.SourcePath = "ClientApp");
    }

    static void RegisterCheckpointStore(Checkpoint settings, IServiceCollection services) {
        ICheckpointStore store = settings.Type switch {
            "file" => new FileCheckpointStore(settings.Path, settings.CheckpointAfter),
            "mongo" => new MongoCheckpointStore(
                settings.Path,
                settings.Database,
                settings.InstanceId,
                settings.CheckpointAfter
            ),
            _ => throw new ArgumentOutOfRangeException($"Unknown checkpoint store type: {settings.Type}")
        };
        services.AddSingleton(store);
    }

    static void RegisterCheckpointSeeder(CheckpointSeeder settings, IServiceCollection services) {
        services.AddSingleton<ICheckpointSeeder>(sp =>
            settings.Type switch {
                "none" => new NoCheckpointSeeder(),
                "chaser" => new ChaserCheckpointSeeder(
                    settings.Path,
                    sp.GetRequiredService<ICheckpointStore>()
                ),
                _ => throw new ArgumentOutOfRangeException($"Unknown checkpoint seeder type: {settings.Type}")
            }
        );
    }
}
