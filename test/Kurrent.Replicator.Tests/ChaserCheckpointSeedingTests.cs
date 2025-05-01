using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using EventStore.Client;
using EventStore.ClientAPI;
using Kurrent.Replicator.Shared.Observe;
using FluentAssertions;
using Kurrent.Replicator.EventStore;
using Kurrent.Replicator.KurrentDb;
using Serilog;
using Testcontainers.EventStoreDb;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;
using Xunit;
using Xunit.Abstractions;
using EventData = EventStore.ClientAPI.EventData;
using Position = EventStore.Client.Position;

namespace Kurrent.Replicator.Tests;

public class ChaserCheckpointSeedingTests {
    public ChaserCheckpointSeedingTests(ITestOutputHelper output) {
        ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.TestOutput(output)
            .CreateLogger()
            .ForContext<ChaserCheckpointSeedingTests>();
    }

    [Fact]
    public async Task Verify() {
        await using var network = BuildNetwork();

        var v5_data_path = Directory.CreateTempSubdirectory();

        await using var v5 =
            BuildV5Container(network, v5_data_path);

        await using var v23 =
            BuildV23Container(network);

        await Task.WhenAll(v5.StartAsync(), v23.StartAsync());

        await Task.Delay(TimeSpan.FromSeconds(2)); // give it some time to spin up

        await SeedV5WithEvents(v5, "ItHappenedBefore", 1000);

        await Task.Delay(TimeSpan.FromSeconds(2)); // give it some time to settle

        // Snapshot chaser.chk
        var chaser_chk_copy = Path.GetTempFileName();
        File.Copy(Path.Combine(v5_data_path.FullName, "chaser.chk"), chaser_chk_copy, true);

        await SeedV5WithEvents(v5, "ItHappenedAfterwards", 1001);

        var store = new FileCheckpointStore(Path.GetTempFileName(), 100);

        await
            Replicator.Replicate(
                new TcpEventReader(ConfigureTcpClient(v5)),
                new GrpcEventWriter(ConfigureEventStoreGrpc(v23)),
                new(),
                new(null, null),
                new ChaserCheckpointSeeder(chaser_chk_copy, store),
                store,
                new(false, false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
                CancellationToken.None
            );

        await using var client = ConfigureEventStoreGrpc(v23);

        var events = await client.ReadAllAsync(Direction.Forwards, Position.Start)
            .Where(evt => !evt.Event.EventType.StartsWith("$"))
            .ToArrayAsync();
        events.Length.Should().Be(1000);

        await Task.WhenAll(v5.StopAsync(), v23.StopAsync());
    }

    static INetwork BuildNetwork() => new NetworkBuilder().WithName("replicator").Build();

    static EventStoreDbContainer BuildV23Container(INetwork network) => new EventStoreDbBuilder()
        .WithNetwork(network)
        .WithName("target")
        .WithEnvironment("EVENTSTORE_RUN_PROJECTIONS", "None")
        .WithEnvironment("EVENTSTORE_START_STANDARD_PROJECTIONS", "false")
        .WithImage("ghcr.io/eventstore/eventstore:23.10.0-focal")
        .WithEnvironment("EVENTSTORE_ENABLE_ATOM_PUB_OVER_HTTP", Boolean.TrueString)
        .Build();

    static IContainer BuildV5Container(INetwork network, DirectoryInfo data) => new ContainerBuilder()
        .WithNetwork(network)
        .WithName("source")
        .WithImage("eventstore/eventstore:5.0.11-bionic")
        .WithEnvironment("EVENTSTORE_CLUSTER_SIZE", "1")
        .WithEnvironment("EVENTSTORE_RUN_PROJECTIONS", "None")
        .WithEnvironment("EVENTSTORE_START_STANDARD_PROJECTIONS", "false")
        .WithEnvironment("EVENTSTORE_EXT_HTTP_PORT", "2113")
        .WithBindMount(data.FullName, "/var/lib/eventstore")
        .WithExposedPort(2113)
        .WithPortBinding(2113, 2113)
        .WithExposedPort(1113)
        .WithPortBinding(1113, 1113)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(1113))
        .Build();

    static async Task SeedV5WithEvents(IContainer v5, string named, int count) {
        using var connection = ConfigureTcpClient(v5);
        await connection.ConnectAsync();

        var emptyBody = JsonSerializer.SerializeToUtf8Bytes("{}");

        for (var index = 0; index < count; index++) {
            await connection.AppendToStreamAsync(
                $"stream-{Random.Shared.Next(0, 10)}",
                ExpectedVersion.Any,
                new EventData(
                    Guid.NewGuid(),
                    named,
                    true,
                    emptyBody,
                    Array.Empty<byte>()
                )
            );
        }

        connection.Close();
    }

    static IEventStoreConnection ConfigureTcpClient(IContainer container) {
        var connectionString =
            $"ConnectTo=tcp://admin:changeit@localhost:{container.GetMappedPublicPort(1113)}; HeartBeatTimeout=500; UseSslConnection=false;";

        var builder = ConnectionSettings.Create()
            .KeepReconnecting()
            .KeepRetrying();

        return EventStoreConnection.Create(connectionString, builder);
    }

    static EventStoreClient ConfigureEventStoreGrpc(IContainer container) {
        var connectionString = $"esdb://localhost:{container.GetMappedPublicPort(2113)}?tls=false";
        var settings         = EventStoreClientSettings.Create(connectionString);

        return new(settings);
    }
}
