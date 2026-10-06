#nullable enable
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;

namespace Kurrent.Replicator.Tests.Resilience;

public class GrpcEventWriterRetryTests {
    readonly FakeTimeProvider        _time     = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly FakeKurrentDbHandler    _handler  = new();
    int                              _appends;

    public GrpcEventWriterRetryTests() => ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));

    int Appends => Volatile.Read(ref _appends);

    /// <summary>Basic-auth style writer (no token source): the retry must not depend on OAuth being configured.</summary>
    GrpcEventWriter Writer() => new(FakeKurrentDbHandler.Client(_handler), new GrpcAuthContext(null, _shutdown.Token, _time, "sink"));

    void FailAppends(int times, StatusCode code = StatusCode.Unavailable)
        => _handler.Respond = s => Task.FromResult(
            s.Path != FakeKurrentDbHandler.AppendPath        ? FakeKurrentDbHandler.TrailersOnly(StatusCode.Internal) :
            Interlocked.Increment(ref _appends) <= times     ? FakeKurrentDbHandler.TrailersOnly(code) :
                                                               FakeKurrentDbHandler.AppendSuccess()
        );

    static async Task RealWait(Func<bool> condition, int timeoutMs = 10000) {
        for (var waited = 0; !condition() && waited < timeoutMs; waited += 10) await Task.Delay(10);

        if (!condition()) throw new TimeoutException("Condition not reached");
    }

    async Task Drive(Task task) {
        // the real EventStoreClient needs real time for each round trip through the fake handler
        for (var i = 0; i < 2000 && !task.IsCompleted; i++) {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15);
        }

        await task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task Write_retries_through_an_outage_with_backoff_then_succeeds() {
        FailAppends(3);
        var writer = Writer();

        var write = writer.WriteEvent(TestEvents.Proposed("s"), CancellationToken.None);
        await RealWait(() => Appends >= 1);

        // no fake time passes: the writer must be waiting, not spinning
        await Task.Delay(300);
        await Assert.That(Appends).IsEqualTo(1);
        await Assert.That(write.IsCompleted).IsFalse();

        // first backoff is 1 s
        _time.Advance(TimeSpan.FromMilliseconds(900));
        await Task.Delay(200);
        await Assert.That(Appends).IsEqualTo(1);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await RealWait(() => Appends >= 2);

        await Drive(write);

        await Assert.That(await write).IsEqualTo(1L);
        await Assert.That(Appends).IsEqualTo(4);
    }

    [Test]
    public async Task Shutdown_during_an_outage_ends_the_wait_with_cancellation() {
        FailAppends(int.MaxValue);
        var writer = Writer();

        var write = writer.WriteEvent(TestEvents.Proposed("s"), CancellationToken.None);
        await RealWait(() => Appends >= 1);
        await _shutdown.CancelAsync();

        await Assert.That(async () => await write.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(Appends).IsEqualTo(1);
    }

    [Test]
    public async Task Caller_cancellation_during_an_outage_ends_the_wait_with_cancellation() {
        FailAppends(int.MaxValue);
        var writer = Writer();
        using var cts = new CancellationTokenSource();

        var write = writer.WriteEvent(TestEvents.Proposed("s"), cts.Token);
        await RealWait(() => Appends >= 1);
        await cts.CancelAsync();

        await Assert.That(async () => await write.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Non_transient_failure_is_not_retried() {
        FailAppends(int.MaxValue, StatusCode.FailedPrecondition);
        var writer = Writer();

        await Assert.That(async () => await writer.WriteEvent(TestEvents.Proposed("s"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))
            .Throws<Exception>();
        await Assert.That(Appends).IsEqualTo(1);
    }

    [Test]
    public async Task Delete_and_metadata_writes_are_retried_too() {
        var deletes = 0;
        _handler.Respond = s => Task.FromResult(
            s.Path == FakeKurrentDbHandler.DeletePath && Interlocked.Increment(ref deletes) <= 2 ? FakeKurrentDbHandler.TrailersOnly(StatusCode.Unavailable) :
            s.Path == FakeKurrentDbHandler.DeletePath                                          ? FakeKurrentDbHandler.TrailersOnly(StatusCode.Internal) :
            Interlocked.Increment(ref _appends) <= 2                                           ? FakeKurrentDbHandler.TrailersOnly(StatusCode.Unavailable) :
                                                                                                 FakeKurrentDbHandler.AppendSuccess()
        );
        var writer = Writer();

        var meta = writer.WriteEvent(TestEvents.Meta("s"), CancellationToken.None); // metadata is an Append to $$s
        await Drive(meta);
        await Assert.That(Appends).IsEqualTo(3);

        // the delete fails transiently twice, then with a non-transient error that surfaces
        var delete = writer.WriteEvent(TestEvents.Delete("s"), CancellationToken.None);
        for (var i = 0; i < 2000 && !delete.IsCompleted; i++) {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15);
        }

        await Assert.That(async () => await delete.WaitAsync(TimeSpan.FromSeconds(10))).Throws<Exception>();
        await Assert.That(Volatile.Read(ref deletes)).IsEqualTo(3);
    }
}
