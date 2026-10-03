#nullable enable
using System.Collections.Concurrent;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Prepare;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Sink;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;

namespace Kurrent.Replicator.Tests.Auth;

public class ReplicatorShutdownTests {
    public ReplicatorShutdownTests() => ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));

    sealed class FakeReader(int count, bool blockAfter) : IEventReader {
        public int    PositionCalls;
        public Func<int, long?>? Position { get; init; }

        public string Protocol => "fake";

        public async Task ReadEvents(LogPosition fromLogPosition, Func<BaseOriginalEvent, ValueTask> next, CancellationToken cancellationToken) {
            for (var i = 0; i < count; i++) await next(TestEvents.Original("s", i));

            if (blockAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<long?> GetLastPosition(CancellationToken cancellationToken) {
            var n = Interlocked.Increment(ref PositionCalls);

            return Task.FromResult(Position?.Invoke(n) ?? (long?)count);
        }

        public ValueTask<bool> Filter(BaseOriginalEvent originalEvent) => ValueTask.FromResult(true);
    }

    sealed class AuthGatedWriter(GrpcAuthContext auth) : IEventWriter {
        public readonly ConcurrentQueue<long> Written = new();

        public Task Start() => Task.CompletedTask;

        public Task<long> WriteEvent(BaseProposedEvent proposedEvent, CancellationToken cancellationToken)
            => auth.Run((_, _) => {
                    Written.Enqueue(proposedEvent.SourceLogPosition.EventNumber);

                    return Task.FromResult(1L);
                },
                cancellationToken
            );
    }

    sealed class RecordingCheckpointStore : ICheckpointStore {
        public readonly ConcurrentQueue<LogPosition> Stored = new();

        public ValueTask<bool>        HasStoredCheckpoint(CancellationToken ct)        => ValueTask.FromResult(false);
        public ValueTask<LogPosition> LoadCheckpoint(CancellationToken ct)             => ValueTask.FromResult(LogPosition.Start);
        public ValueTask              Flush(CancellationToken ct)                      => ValueTask.CompletedTask;

        public ValueTask StoreCheckpoint(LogPosition logPosition, CancellationToken ct) {
            Stored.Enqueue(logPosition);

            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task Sink_token_outage_pauses_replication_and_it_resumes_without_restart() {
        var time     = new FakeTimeProvider();
        var source   = new ControllableTokenSource(time); // no token yet
        var writer   = new AuthGatedWriter(new GrpcAuthContext(source, CancellationToken.None, time, "sink"));
        var store    = new RecordingCheckpointStore();

        var run = Replicator.Replicate(
            new FakeReader(5, blockAfter: false),
            writer,
            new SinkPipeOptions(1, 10),
            new PreparePipelineOptions(null, null, 1, 10),
            new NoCheckpointSeeder(),
            store,
            new ReplicatorOptions(false, false, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            CancellationToken.None
        );

        await TimeDriver.Until(() => source.Calls >= 5, time);
        await Assert.That(writer.Written).IsEmpty();
        source.Value = "A";
        await TimeDriver.Drive(run, time);

        await Assert.That(writer.Written.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Shutdown_during_outage_with_full_sink_channel_returns_promptly() {
        var time     = new FakeTimeProvider();
        var shutdown = new CancellationTokenSource();
        var stopping = new CancellationTokenSource();
        var source   = new ControllableTokenSource(time);
        var writer   = new AuthGatedWriter(new GrpcAuthContext(source, shutdown.Token, time, "sink"));
        var store    = new RecordingCheckpointStore();

        var run = Replicator.Replicate(
            new FakeReader(10, blockAfter: true),
            writer,
            new SinkPipeOptions(1, 1),                    // sink buffer of 1: fills immediately
            new PreparePipelineOptions(null, null, 1, 100), // prepare buffer larger than the event count
            new NoCheckpointSeeder(),
            store,
            new ReplicatorOptions(false, true, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            stopping.Token
        );

        await TimeDriver.Until(() => source.Calls >= 3, time);
        await Task.Delay(200); // let the prepare shovel block on the full sink channel

        await shutdown.CancelAsync(); // ApplicationStopping
        await stopping.CancelAsync(); // hosted service stop

        await run.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(writer.Written).IsEmpty();
        await Assert.That(store.Stored).IsEmpty();
    }

    [Test]
    public async Task Metrics_reporter_survives_a_failing_position_read() {
        using var stopping = new CancellationTokenSource();
        var reader = new FakeReader(0, blockAfter: true) { Position = n => n == 1 ? throw new InvalidOperationException("boom") : 1 };

        var run = Replicator.Replicate(
            reader,
            new AuthGatedWriter(GrpcAuthContext.None),
            new SinkPipeOptions(),
            new PreparePipelineOptions(null, null),
            new NoCheckpointSeeder(),
            new RecordingCheckpointStore(),
            new ReplicatorOptions(false, true, TimeSpan.Zero, TimeSpan.FromMilliseconds(20)),
            stopping.Token
        );

        await Task.Delay(500);
        await Assert.That(Volatile.Read(ref reader.PositionCalls)).IsGreaterThanOrEqualTo(3);
        await stopping.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
