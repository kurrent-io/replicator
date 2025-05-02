using System.Text.Json;
using EventStore.Client;
using EventStore.ClientAPI;
using Kurrent.Replicator.Prepare;
using Kurrent.Replicator.Sink;
using Kurrent.Replicator.EventStore;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.Tests.Fakes;
using Kurrent.Replicator.Tests.Fixtures;
using Kurrent.Replicator.Tests.Logging;
using Serilog;
using Assert = TUnit.Assertions.Assert;
using EventData = EventStore.ClientAPI.EventData;
using Position = EventStore.Client.Position;

namespace Kurrent.Replicator.Tests;

[ClassDataSource<ContainerFixture>]
public class ValuePartitionerTests {
    readonly ContainerFixture _fixture;

    public ValuePartitionerTests(ContainerFixture fixture) {
        _fixture = fixture;

        _tenants = Enumerable.Range(1, TenantsCount).Select(x => $"TEN{x}").ToArray();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.TestOutput()
            .CreateLogger()
            .ForContext<ValuePartitionerTests>();
    }

    [Test]
    public async Task ShouldKeepOrderWithinPartition() {
        var             checkpointStore = new CheckpointStore();
        using var       v5Client        = _fixture.GetV5Client();
        using var       seedV5Client    = _fixture.GetV5Client();
        await using var kdbClient       = _fixture.GetKurrentClient();

        await seedV5Client.ConnectAsync();
        await Timing.Measure("Seed", Seed(seedV5Client));

        var reader         = new TcpEventReader(v5Client, 1024);
        var writer         = new GrpcEventWriter(kdbClient);
        var prepareOptions = new PreparePipelineOptions(null, null);
        var partitioner    = await File.ReadAllTextAsync("partition.js");
        var sinkOptions    = new SinkPipeOptions(0, 100, partitioner);
        // var sinkOptions    = new SinkPipeOptions(writer, 0, 100);

        Log.Information("Replicating...");

        var replication = Replicator.Replicate(
            reader,
            writer,
            sinkOptions,
            prepareOptions,
            new NoCheckpointSeeder(),
            checkpointStore,
            new(false, false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
            CancellationToken.None
        );
        await Timing.Measure("Replication", replication);
        Log.Information("Replication complete");

        var read = kdbClient.ReadAllAsync(Direction.Forwards, Position.Start);

        var events = await read.Where(evt => !evt.Event.EventType.StartsWith('$')).ToListAsync();

        Log.Information("Retrieved {Count} replicated events", events.Count);
        await Assert.That(events).HasCount(EventsCount);

        var testEvents = events
            .Select(x => JsonSerializer.Deserialize<TestEvent>(x.Event.Data.Span))
            .GroupBy(x => x!.Tenant);

        foreach (var tenantGroup in testEvents) {
            Log.Information("Validating order for tenant {Tenant}", tenantGroup.Key);
            await Assert.That(tenantGroup).IsInOrder(new TestEventComparer());
        }
    }

    class TestEventComparer : IComparer<TestEvent> {
        public int Compare(TestEvent x, TestEvent y) {
            if (ReferenceEquals(x, y)) return 0;
            if (y is null) return 1;
            if (x is null) return -1;

            return x.Sequence.CompareTo(y.Sequence);
        }
    }

    readonly string[] _tenants;

    const int EventsCount  = 5000;
    const int TenantsCount = 10;

    async Task Seed(IEventStoreConnection client) {
        Log.Information("Seeding data...");
        var random = new Random();

        const int max = TenantsCount - 1;

        for (var counter = 0; counter < EventsCount; counter++) {
            var tenant = _tenants[random.Next(0, max)];
            var evt    = new TestEvent(tenant, counter);

            await client.AppendToStreamAsync(
                $"{tenant}-{Guid.NewGuid():N}",
                ExpectedVersion.Any,
                new EventData(
                    Guid.NewGuid(),
                    "TestEvent",
                    true,
                    JsonSerializer.SerializeToUtf8Bytes(evt),
                    null
                )
            );
        }

        Log.Information("Seeding complete");
    }

    record TestEvent(string Tenant, int Sequence);
}
