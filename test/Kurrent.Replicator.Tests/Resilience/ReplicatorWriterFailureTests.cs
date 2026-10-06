#nullable enable
using System.Collections.Concurrent;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Prepare;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Sink;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;

namespace Kurrent.Replicator.Tests.Resilience;

public class ReplicatorWriterFailureTests {
    public ReplicatorWriterFailureTests() => ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));

    sealed class FakeReader(int count, bool blockAfter) : IEventReader {
        public int Cycles;

        public readonly ConcurrentQueue<CancellationToken> PositionQueryTokens = new();

        public string Protocol => "fake";

        public async Task ReadEvents(LogPosition fromLogPosition, Func<BaseOriginalEvent, ValueTask> next, CancellationToken cancellationToken) {
            Interlocked.Increment(ref Cycles);

            for (var i = 0; i < count; i++) await next(TestEvents.Original("s", i));

            if (blockAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<long?> GetLastPosition(CancellationToken cancellationToken) {
            PositionQueryTokens.Enqueue(cancellationToken);

            return Task.FromResult<long?>(count);
        }

        public ValueTask<bool> Filter(BaseOriginalEvent originalEvent) => ValueTask.FromResult(true);
    }

    sealed class BrokenWriter : IEventWriter {
        public int Calls;

        public Task Start() => Task.CompletedTask;

        public Task<long> WriteEvent(BaseProposedEvent proposedEvent, CancellationToken cancellationToken) {
            Interlocked.Increment(ref Calls);

            throw new InvalidOperationException("non-transient sink failure");
        }
    }

    sealed class MemoryCheckpointStore : ICheckpointStore {
        public readonly ConcurrentQueue<LogPosition> Stored = new();

        public ValueTask<bool>        HasStoredCheckpoint(CancellationToken ct) => ValueTask.FromResult(false);
        public ValueTask<LogPosition> LoadCheckpoint(CancellationToken ct)      => ValueTask.FromResult(LogPosition.Start);
        public ValueTask              Flush(CancellationToken ct)               => ValueTask.CompletedTask;

        public ValueTask StoreCheckpoint(LogPosition logPosition, CancellationToken ct) {
            Stored.Enqueue(logPosition);

            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task Writer_failure_ends_replication_with_an_error_instead_of_looping() {
        using var stopping = new CancellationTokenSource();
        var       reader   = new FakeReader(5, blockAfter: true);
        var       store    = new MemoryCheckpointStore();

        var run = Replicator.Replicate(
            reader,
            new BrokenWriter(),
            new SinkPipeOptions(1, 10),
            new PreparePipelineOptions(null, null, 1, 10),
            new NoCheckpointSeeder(),
            store,
            new ReplicatorOptions(true, true, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            stopping.Token
        );

        try {
            await Assert.That(async () => await run.WaitAsync(TimeSpan.FromSeconds(20))).Throws<ReplicatorFailedException>();
            await Assert.That(store.Stored).IsEmpty();
            await Assert.That(Volatile.Read(ref reader.Cycles)).IsEqualTo(1);
        } finally {
            await stopping.CancelAsync();
        }
    }

    [Test]
    public async Task Failed_replication_stops_its_background_work_before_returning() {
        using var stopping = new CancellationTokenSource();
        var       reader   = new FakeReader(5, blockAfter: true);

        var run = Replicator.Replicate(
            reader,
            new BrokenWriter(),
            new SinkPipeOptions(1, 10),
            new PreparePipelineOptions(null, null, 1, 10),
            new NoCheckpointSeeder(),
            new MemoryCheckpointStore(),
            new ReplicatorOptions(true, true, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            stopping.Token
        );

        try {
            await Assert.That(async () => await run.WaitAsync(TimeSpan.FromSeconds(20))).Throws<ReplicatorFailedException>();
            // the metrics reporter was stopped inside Replicate, not left running until the host stops
            await Assert.That(reader.PositionQueryTokens).IsNotEmpty();
            await Assert.That(reader.PositionQueryTokens.All(t => t.IsCancellationRequested)).IsTrue();
        } finally {
            await stopping.CancelAsync();
        }
    }

    [Test]
    public async Task Normal_shutdown_still_ends_cleanly() {
        using var stopping = new CancellationTokenSource();
        var       store    = new MemoryCheckpointStore();

        var run = Replicator.Replicate(
            new FakeReader(5, blockAfter: true),
            new GrpcEventWriter(FakeKurrentDbHandler.Client(new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.AppendSuccess()) })),
            new SinkPipeOptions(1, 10),
            new PreparePipelineOptions(null, null, 1, 10),
            new NoCheckpointSeeder(),
            store,
            new ReplicatorOptions(true, true, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            stopping.Token
        );

        for (var i = 0; i < 1000 && store.Stored.Count < 5; i++) await Task.Delay(10);
        await Assert.That(store.Stored.Count).IsEqualTo(5);

        await stopping.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task Transient_sink_outage_does_not_stop_replication() {
        var time     = new FakeTimeProvider();
        var handler  = new FakeKurrentDbHandler();
        var appends  = 0;
        var store    = new MemoryCheckpointStore();

        handler.Respond = s => Task.FromResult(
            s.Path != FakeKurrentDbHandler.AppendPath  ? FakeKurrentDbHandler.TrailersOnly(StatusCode.Internal) :
            Interlocked.Increment(ref appends) <= 15   ? FakeKurrentDbHandler.TrailersOnly(StatusCode.Unavailable) :
                                                         FakeKurrentDbHandler.AppendSuccess()
        );

        var writer = new GrpcEventWriter(FakeKurrentDbHandler.Client(handler), new GrpcAuthContext(null, CancellationToken.None, time, "sink"));

        var run = Replicator.Replicate(
            new FakeReader(5, blockAfter: false),
            writer,
            new SinkPipeOptions(1, 10),
            new PreparePipelineOptions(null, null, 1, 10),
            new NoCheckpointSeeder(),
            store,
            new ReplicatorOptions(true, false, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            CancellationToken.None
        );

        // well past the GreenPipes retry budget (~0.5 s) in fake time, with real time for each gRPC round trip
        for (var i = 0; i < 2000 && !run.IsCompleted; i++) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(20));
        await Assert.That(Volatile.Read(ref appends)).IsEqualTo(20);
        await Assert.That(store.Stored.Count).IsEqualTo(5);
    }
}
