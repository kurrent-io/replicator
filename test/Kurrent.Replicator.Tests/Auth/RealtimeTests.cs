#nullable enable
using System.Collections.Concurrent;
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class RealtimeTests {
    sealed class FakeSubscription : IDisposable {
        public volatile bool Disposed;
        public void Dispose() => Disposed = true;
    }

    sealed class Call {
        public required Func<ResolvedEvent, Task>                       OnEvent;
        public required Action<SubscriptionDroppedReason, Exception?>   OnDropped;
        public required CallAuth                                        Auth;
        public readonly TaskCompletionSource<IDisposable>               Result       = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly FakeSubscription                                Subscription = new();
        public string? Token => Auth.Lease?.Value;
        public void Succeed() => Result.SetResult(Subscription);
        public void Fail() => Result.SetException(new RpcException(new Status(StatusCode.Unavailable, "down")));
    }

    readonly FakeTimeProvider        _time     = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly ControllableTokenSource _source;
    readonly ConcurrentQueue<Call>   _calls = new();
    readonly StreamMetaCache         _cache = new(failClosedOnAuthErrors: true);
    readonly Realtime                _realtime;

    public RealtimeTests() {
        _source = new(_time) { Value = "A" };
        var auth = new GrpcAuthContext(_source, _shutdown.Token, _time, "reader");

        _realtime = new Realtime(
            (onEvent, onDropped, a, _) => {
                var call = new Call { OnEvent = onEvent, OnDropped = onDropped, Auth = a };
                _calls.Enqueue(call);

                return call.Result.Task;
            },
            _cache,
            auth
        );
    }

    Call Nth(int n) => _calls.ElementAt(n);

    async Task<Call> WaitForCall(int n) {
        await TimeDriver.Until(() => _calls.Count > n, _time);

        return Nth(n);
    }

    async Task Publish(int n) {
        var start = _realtime.Start(default);
        (await WaitForCall(n)).Succeed();
        await TimeDriver.Drive(start, _time);
    }

    static NotAuthenticatedException Unauthenticated() => new("x", new RpcException(new Status(StatusCode.Unauthenticated, "x")));

    [Test]
    public async Task First_subscribe_marks_cache_live() {
        await Assert.That(_cache.IsLive).IsFalse();
        await Publish(0);
        await Assert.That(_cache.IsLive).IsTrue();
        await Assert.That(_realtime.IsPublished).IsTrue();
    }

    [Test]
    public async Task Failed_initial_subscribe_is_retried() {
        var start = _realtime.Start(default);
        (await WaitForCall(0)).Fail();
        (await WaitForCall(1)).Succeed();
        await TimeDriver.Drive(start, _time);
        await Assert.That(_calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Drop_then_failing_resubscribes_keep_retrying_until_success() {
        await Publish(0);
        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, new RpcException(new Status(StatusCode.Unavailable, "x")));
        await Assert.That(_cache.IsLive).IsFalse();
        (await WaitForCall(1)).Fail();
        (await WaitForCall(2)).Fail();
        (await WaitForCall(3)).Succeed();
        await TimeDriver.Until(() => _realtime.IsPublished, _time);
        await Assert.That(_cache.IsLive).IsTrue();
    }

    [Test]
    public async Task Foreground_start_during_pending_resubscribe_shares_the_attempt() {
        await Publish(0);
        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, null);
        var pending = await WaitForCall(1);
        var start   = _realtime.Start(default);
        await Task.Delay(20);
        await Assert.That(_calls.Count).IsEqualTo(2);
        pending.Succeed();
        await TimeDriver.Drive(start, _time);
        await Assert.That(_calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Shutdown_ends_the_loop() {
        var start = _realtime.Start(default);
        (await WaitForCall(0)).Fail();
        await _shutdown.CancelAsync();
        await Assert.That(async () => await start.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Stale_callback_from_an_old_attempt_is_ignored() {
        await Publish(0);
        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, null); // non-token drop
        (await WaitForCall(1)).Succeed();
        await TimeDriver.Until(() => _realtime.IsPublished, _time);

        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated()); // delayed duplicate from A
        await Task.Delay(20);

        await Assert.That(_realtime.IsPublished).IsTrue();
        await Assert.That(_cache.IsLive).IsTrue();
        await Assert.That(_source.Invalidated).IsEmpty();
        await Assert.That(_calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Late_drop_of_failed_attempt_does_not_affect_the_pending_one() {
        var start = _realtime.Start(default);
        var a     = await WaitForCall(0);
        a.Fail();
        var b = await WaitForCall(1); // now pending
        a.OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated());
        await Assert.That(_source.Invalidated).IsEmpty();
        b.Succeed();
        await TimeDriver.Drive(start, _time);
        await Assert.That(_realtime.IsPublished).IsTrue();
    }

    [Test]
    public async Task Drop_of_pending_attempt_is_handled() {
        var start = _realtime.Start(default);
        var a     = await WaitForCall(0);
        a.OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated()); // early drop
        await Assert.That(_source.Invalidated.Single().Applied).IsTrue();
        _source.Value = "B";
        a.Succeed();                                                           // the subscribe call still returns
        // Result continuations run asynchronously, so the loop disposes on another thread: wait for it.
        await TimeDriver.Until(() => a.Subscription.Disposed || _calls.Count > 1, _time);
        await Assert.That(a.Subscription.Disposed).IsTrue();
        var b = await WaitForCall(1);
        await Assert.That(b.Token).IsEqualTo("B");
        b.Succeed();
        await TimeDriver.Drive(start, _time);
        await Assert.That(_realtime.IsPublished).IsTrue();
        await Assert.That(_source.Accepted.Count(l => l.Value == "A")).IsEqualTo(0);
    }

    [Test]
    public async Task Rejected_token_is_quarantined_before_anyone_can_observe_unpublished_state() {
        await Publish(0);
        var invalidatedWhenUnpublished = false;
        _realtime.OnUnpublishedUnderLock = () => invalidatedWhenUnpublished = _source.Invalidated.Any(i => i.Applied);

        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated());
        var start = _realtime.Start(default); // concurrent foreground start

        await Assert.That(invalidatedWhenUnpublished).IsTrue();
        for (var i = 0; i < 30; i++) { _time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(2); } // < quarantine
        await Assert.That(_calls.Count).IsEqualTo(1);                                                // A never handed out again

        _source.Value = "B";
        var b = await WaitForCall(1);
        await Assert.That(b.Token).IsEqualTo("B");
        b.Succeed();
        await TimeDriver.Drive(start, _time);
    }

    [Test]
    public async Task Drop_reports_with_the_subscription_lease_not_the_current_one() {
        await Publish(0);                    // subscribed with A
        _source.Value = "B";
        await _source.GetAccessToken(default); // source moves to B (new generation)

        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated());
        await Assert.That(_source.Invalidated.Single().Applied).IsFalse(); // stale lease, B untouched

        var next = await WaitForCall(1);
        await Assert.That(next.Token).IsEqualTo("B");
    }

    [Test]
    public async Task Cache_is_coherent_across_a_subscription_gap() {
        var serverMeta = new StreamMeta(false, null, null, 0);
        var metaReads  = 0;
        var filter = new ScavengedEventsFilter(
            (_, _, _) => { metaReads++; return Task.FromResult(serverMeta); },
            (_, _, _) => Task.FromResult(new StreamSize(10)),
            _cache,
            new GrpcAuthContext(_source, _shutdown.Token, _time, "reader")
        );

        await Publish(0);
        await Assert.That(await filter.Filter(TestEvents.Original("s", 5))).IsTrue(); // cached: no maxCount
        await filter.Filter(TestEvents.Original("s", 5));
        await Assert.That(metaReads).IsEqualTo(1);

        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, null);
        serverMeta = serverMeta with { MaxCount = 1 }; // changed while the subscription is down

        await Assert.That(await filter.Filter(TestEvents.Original("s", 5))).IsFalse(); // direct read sees the change
        await Assert.That(metaReads).IsEqualTo(2);

        (await WaitForCall(1)).Succeed();            // resubscribed; no events delivered yet
        await TimeDriver.Until(() => _realtime.IsPublished, _time);
        await Assert.That(await filter.Filter(TestEvents.Original("s", 5))).IsFalse(); // fresh fetch, not pre-gap cache
        await Assert.That(metaReads).IsEqualTo(3);
    }

    [Test]
    public async Task Drop_right_after_publish_before_the_loop_finishes_still_resubscribes() {
        // Run reports the lease accepted after the attempt is published but before the loop task completes:
        // deliver the drop exactly in that window.
        _source.OnAccepted = _ => {
            _source.OnAccepted = null;
            Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, null);
        };

        var start = _realtime.Start(default);
        (await WaitForCall(0)).Succeed();
        await TimeDriver.Drive(start, _time);

        (await WaitForCall(1)).Succeed(); // a new loop resubscribed instead of joining the finishing one
        await TimeDriver.Until(() => _realtime.IsPublished, _time);
        await Assert.That(_cache.IsLive).IsTrue();
        await Assert.That(_calls.Count).IsEqualTo(2);
    }
}
