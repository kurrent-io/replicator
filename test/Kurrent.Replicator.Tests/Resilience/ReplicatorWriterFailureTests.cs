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

        public string Protocol => "fake";

        public async Task ReadEvents(LogPosition fromLogPosition, Func<BaseOriginalEvent, ValueTask> next, CancellationToken cancellationToken) {
            Interlocked.Increment(ref Cycles);

            await PositionQueryGate.WaitAsync(cancellationToken);

            for (var i = 0; i < count; i++) await next(TestEvents.Original("s", i));

            if (blockAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        // Reading starts only once the metrics reporter is inside GetLastPosition, so a fast writer failure can't
        // cancel the reporter before it ever queried the position.
        public Task PositionQueryGate { get; init; } = Task.CompletedTask;

        public Func<CancellationToken, Task<long?>> OnGetLastPosition { get; init; } = _ => Task.FromResult<long?>(null);

        public Task<long?> GetLastPosition(CancellationToken cancellationToken) => OnGetLastPosition(cancellationToken);

        public ValueTask<bool> Filter(BaseOriginalEvent originalEvent) => ValueTask.FromResult(true);
    }

    /// <summary>A position query that signals when it starts and when it ends; it ends only on cancellation unless <paramref name="ignoreCancellation"/>.</summary>
    sealed class PositionQueryProbe(bool ignoreCancellation = false) {
        public readonly TaskCompletionSource Started   = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release   = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<long?> Query(CancellationToken cancellationToken) {
            Started.TrySetResult();

            try {
                if (ignoreCancellation) await Release.Task;
                else await Task.Delay(Timeout.Infinite, cancellationToken);
            } finally {
                Completed.TrySetResult();
            }

            return null;
        }

        public FakeReader Reader(int count, bool blockAfter) => new(count, blockAfter) { PositionQueryGate = Started.Task, OnGetLastPosition = Query };
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

        public bool FailFlush { get; init; }

        public ValueTask<bool>        HasStoredCheckpoint(CancellationToken ct) => ValueTask.FromResult(false);
        public ValueTask<LogPosition> LoadCheckpoint(CancellationToken ct)      => ValueTask.FromResult(LogPosition.Start);

        public ValueTask Flush(CancellationToken ct) => FailFlush ? ValueTask.FromException(new IOException("checkpoint store unavailable")) : ValueTask.CompletedTask;

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

    static Task Run(IEventReader reader, ICheckpointStore store, CancellationToken stoppingToken)
        => Replicator.Replicate(
            reader,
            new BrokenWriter(),
            new SinkPipeOptions(1, 10),
            new PreparePipelineOptions(null, null, 1, 10),
            new NoCheckpointSeeder(),
            store,
            new ReplicatorOptions(true, true, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            stoppingToken
        );

    // True when the position query had finished by the time Replicate's task completed (checked synchronously on completion).
    static Task<bool> QueryCompletedWhenRunCompleted(Task run, PositionQueryProbe probe)
        => run.ContinueWith(_ => probe.Completed.Task.IsCompleted, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    [Test]
    public async Task Failed_replication_stops_its_background_work_before_returning() {
        using var stopping = new CancellationTokenSource();
        var       probe    = new PositionQueryProbe();

        var run      = Run(probe.Reader(5, blockAfter: true), new MemoryCheckpointStore(), stopping.Token);
        var observed = QueryCompletedWhenRunCompleted(run, probe);

        try {
            await Assert.That(async () => await run.WaitAsync(TimeSpan.FromSeconds(20))).Throws<ReplicatorFailedException>();
            // the metrics reporter was cancelled and awaited inside Replicate, not left running until the host stops
            await Assert.That(await observed).IsTrue();
        } finally {
            await stopping.CancelAsync();
        }
    }

    [Test]
    public async Task Reporter_is_stopped_even_when_the_final_checkpoint_flush_fails() {
        using var stopping = new CancellationTokenSource();
        var       probe    = new PositionQueryProbe();

        var run      = Run(probe.Reader(5, blockAfter: true), new MemoryCheckpointStore { FailFlush = true }, stopping.Token);
        var observed = QueryCompletedWhenRunCompleted(run, probe);

        try {
            await Assert.That(async () => await run.WaitAsync(TimeSpan.FromSeconds(20))).Throws<IOException>();
            await Assert.That(await observed).IsTrue();
        } finally {
            await stopping.CancelAsync();
        }
    }

    [Test]
    public async Task Writer_failure_still_ends_replication_when_the_position_query_ignores_cancellation() {
        using var stopping = new CancellationTokenSource();
        var       probe    = new PositionQueryProbe(ignoreCancellation: true);
        var       reader   = probe.Reader(5, blockAfter: true);
        var       exited   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnReporterExited(IEventReader r) {
            if (ReferenceEquals(r, reader)) exited.TrySetResult();
        }

        Replicator.ReporterExited += OnReporterExited;

        try {
            var run = Run(reader, new MemoryCheckpointStore(), stopping.Token);

            // bounded: the reporter is given a short grace period after cancellation, then abandoned
            await Assert.That(async () => await run.WaitAsync(TimeSpan.FromSeconds(20))).Throws<ReplicatorFailedException>();
            await Assert.That(probe.Completed.Task.IsCompleted).IsFalse();
        } finally {
            probe.Release.TrySetResult();
            await stopping.CancelAsync();
            // the abandoned reporter logs once released: let it finish before the test (and its output capture) ends
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Replicator.ReporterExited -= OnReporterExited;
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
