using System.Text.Json;
using EventStore.Client;
using EventStore.ClientAPI;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.EventStore;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.Tests.Fixtures;
using Kurrent.Replicator.Tests.Logging;
using Serilog;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;
using EventData = EventStore.ClientAPI.EventData;
using Position = EventStore.Client.Position;

namespace Kurrent.Replicator.Tests;

[ClassDataSource<ContainerFixture>]
public class ChaserCheckpointSeedingTests {
    readonly ContainerFixture _fixture;

    public ChaserCheckpointSeedingTests(ContainerFixture fixture) {
        _fixture = fixture;
        ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.TestOutput()
            .CreateLogger()
            .ForContext<ChaserCheckpointSeedingTests>();
    }

    [Test]
    public async Task Verify() {
        var v5DataPath = _fixture.V5DataPath;

        await SeedV5WithEvents("ItHappenedBefore", 1000);

        await Task.Delay(TimeSpan.FromSeconds(2)); // give it some time to settle

        // Snapshot chaser.chk
        var chaserChkCopy = Path.GetTempFileName();
        File.Copy(Path.Combine(v5DataPath.FullName, "chaser.chk"), chaserChkCopy, true);

        await SeedV5WithEvents("ItHappenedAfterwards", 1001);

        var store = new FileCheckpointStore(Path.GetTempFileName(), 100);

        using var       eventStoreClient = _fixture.GetV5Client();
        await using var kurrentClient    = _fixture.GetKurrentClient();

        await
            Replicator.Replicate(
                new TcpEventReader(eventStoreClient),
                new GrpcEventWriter(kurrentClient),
                new(),
                new(null, null),
                new ChaserCheckpointSeeder(chaserChkCopy, store),
                store,
                new(false, false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
                TestContext.Current?.CancellationToken ?? CancellationToken.None
            );

        var events = await kurrentClient.ReadAllAsync(Direction.Forwards, Position.Start)
            .Where(evt => !evt.Event.EventType.StartsWith('$'))
            .ToArrayAsync(TestContext.Current!.CancellationToken);

        await Assert.That(events).HasCount(1000);
    }

    async Task SeedV5WithEvents(string named, int count) {
        using var connection = _fixture.GetV5Client();
        await connection.ConnectAsync();

        var emptyBody = JsonSerializer.SerializeToUtf8Bytes("{}");

        for (var index = 0; index < count; index++) {
            await connection.AppendToStreamAsync(
                $"stream-{Random.Shared.Next(0, 10)}",
                ExpectedVersion.Any,
                new EventData(Guid.NewGuid(), named, true, emptyBody, [])
            );
        }

        connection.Close();
    }
}
