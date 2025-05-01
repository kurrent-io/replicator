using System.Text.Json;
using EventStore.Client;
using EventStore.ClientAPI;
using Kurrent.Replicator.Prepare;
using Kurrent.Replicator.Sink;
using FluentAssertions;
using Kurrent.Replicator.EventStore;
using Kurrent.Replicator.KurrentDb;
using Serilog;
using Xunit;
using Xunit.Abstractions;
using EventData = EventStore.ClientAPI.EventData;
using Position = EventStore.Client.Position;

namespace Kurrent.Replicator.Tests;

public class ValuePartitionerTests : IClassFixture<Fixture> {
    readonly Fixture _fixture;

    public ValuePartitionerTests(Fixture fixture, ITestOutputHelper output) {
        _fixture = fixture;

        _tenants = Enumerable.Range(1, TenantsCount).Select(x => $"TEN{x}").ToArray();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.TestOutput(output)
            .CreateLogger()
            .ForContext<ValuePartitionerTests>();
    }

    [Fact]
    public async Task ShouldKeepOrderWithinPartition() {
        await Timing.Measure("Seed", Seed());

        var reader         = new TcpEventReader(_fixture.TcpClient, 1024);
        var writer         = new GrpcEventWriter(_fixture.GrpcClient);
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
            _fixture.CheckpointStore,
            new(false, false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
            CancellationToken.None
        );
        await Timing.Measure("Replication", replication);
        Log.Information("Replication complete");

        var read = _fixture.GrpcClient.ReadAllAsync(Direction.Forwards, Position.Start);

        var events = await read.Where(evt => !evt.Event.EventType.StartsWith('$')).ToListAsync();

        Log.Information("Retrieved {Count} replicated events", events.Count);
        events.Count.Should().Be(EventsCount);

        var testEvents = events
            .Select(x => JsonSerializer.Deserialize<TestEvent>(x.Event.Data.Span))
            .GroupBy(x => x!.Tenant);

        foreach (var tenantGroup in testEvents) {
            Log.Information("Validating order for tenant {Tenant}", tenantGroup.Key);
            tenantGroup.Should().BeInAscendingOrder(x => x.Sequence);
        }
    }

    readonly string[] _tenants;

    const int EventsCount  = 5000;
    const int TenantsCount = 10;

    async Task Seed() {
        Log.Information("Seeding data...");
        var random = new Random();

        const int max = TenantsCount - 1;

        for (var counter = 0; counter < EventsCount; counter++) {
            var tenant = _tenants[random.Next(0, max)];
            var evt    = new TestEvent(tenant, counter);

            await _fixture.TcpClient.AppendToStreamAsync(
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
