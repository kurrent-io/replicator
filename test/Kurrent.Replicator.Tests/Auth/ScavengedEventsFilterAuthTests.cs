#nullable enable
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class ScavengedEventsFilterAuthTests {
    readonly FakeTimeProvider        _time = new();
    readonly ControllableTokenSource _source;
    readonly GrpcAuthContext         _auth;

    public ScavengedEventsFilterAuthTests() {
        _source = new(_time) { Value = "A" };
        _auth   = new(_source, CancellationToken.None, _time, "reader");
    }

    static readonly StreamMeta Deleted = new(true, null, null, 0);
    static readonly StreamMeta Plain   = new(false, null, null, 0);

    static Task<StreamSize> NoSize(string s, CallAuth a, CancellationToken c) => Task.FromResult(new StreamSize(0));

    [Test]
    public async Task Oauth_permission_denied_fails_closed() {
        var filter = new ScavengedEventsFilter(
            (_, _, _) => throw new AccessDeniedException("x", new RpcException(new Status(StatusCode.PermissionDenied, "x"))),
            NoSize, new StreamMetaCache(true), _auth
        );
        await Assert.That(async () => await filter.Filter(TestEvents.Original("s", 0))).Throws<AccessDeniedException>();
    }

    [Test]
    public async Task Basic_auth_permission_denied_keeps_today_behaviour() {
        var filter = new ScavengedEventsFilter(
            (_, _, _) => throw new AccessDeniedException("x", new RpcException(new Status(StatusCode.PermissionDenied, "x"))),
            NoSize, new StreamMetaCache(), GrpcAuthContext.None
        );
        await Assert.That(await filter.Filter(TestEvents.Original("s", 0))).IsTrue();
    }

    [Test]
    public async Task Transient_unauthenticated_is_retried_and_the_filter_then_decides_correctly() {
        var calls = 0;
        var filter = new ScavengedEventsFilter(
            (_, _, _) => ++calls == 1
                ? throw new NotAuthenticatedException("x", new RpcException(new Status(StatusCode.Unauthenticated, "x")))
                : Task.FromResult(Deleted),
            NoSize, new StreamMetaCache(true), _auth
        );
        _source.OnInvalidate = _ => _source.Value = "B";

        var keep = await TimeDriver.Drive(filter.Filter(TestEvents.Original("s", 0)).AsTask(), _time);
        await Assert.That(keep).IsFalse();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Token_outage_after_read_began_makes_the_filter_wait() {
        var filter = new ScavengedEventsFilter((_, _, _) => Task.FromResult(Deleted), NoSize, new StreamMetaCache(true), _auth);
        await Assert.That(await filter.Filter(TestEvents.Original("s", 0))).IsFalse();

        _source.Value = null; // outage
        var pending = filter.Filter(TestEvents.Original("s", 1)).AsTask();
        await TimeDriver.Until(() => _source.Calls >= 4, _time);
        await Assert.That(pending.IsCompleted).IsFalse();
        _source.Value = "B";
        await Assert.That(await TimeDriver.Drive(pending, _time)).IsFalse();
    }

    [Test]
    public async Task Metadata_and_size_reads_carry_per_call_bearer() {
        var           handler = new FakeKurrentDbHandler();
        FallbackUsage usage   = null!;
        var           client  = FakeKurrentDbHandler.Client(handler, configure: s => usage = GrpcAuthentication.Apply(s, _source));
        var           filter  = new ScavengedEventsFilter(client, new StreamMetaCache(true), _auth);

        await filter.Filter(TestEvents.Original("s", 0)); // Unavailable is not an auth error -> null meta -> keep
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Single()).IsEqualTo("Bearer A");
        await Assert.That(_source.Accepted).IsEmpty();
        // EventStore.Client issues server-feature discovery RPCs per client instance using DefaultCredentials
        // (the sentinel), which fall back through GrpcAuthentication.Apply - observed 2 here (controller ruling:
        // assert the observed discovery-only count, not 0, and keep the per-RPC Authorization assertions above).
        // If the metadata Read itself fell back to DefaultCredentials, this count would be higher.
        await Assert.That(usage.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Stream_size_read_carries_per_call_bearer() {
        // The fallback hook uses a different source, so a Read that fell back to DefaultCredentials would carry
        // "Bearer FALLBACK" instead of the per-call "Bearer A".
        var handler  = new FakeKurrentDbHandler();
        var fallback = new ControllableTokenSource(_time) { Value = "FALLBACK" };
        var client   = FakeKurrentDbHandler.Client(handler, configure: s => GrpcAuthentication.Apply(s, fallback));
        var metaAuth = new List<CallAuth>();
        var sizeAuth = new List<CallAuth>();

        var sizeReader = ScavengedEventsFilter.SizeReader(client);

        var filter = new ScavengedEventsFilter(
            (_, a, _) => {
                metaAuth.Add(a);

                return Task.FromResult(new StreamMeta(false, null, 10, 0)); // MaxCount set: OverMaxCount reads the stream size
            },
            (s, a, c) => {
                sizeAuth.Add(a);

                return sizeReader(s, a, c); // production reader, same as the EventStoreClient-based ctor
            },
            new StreamMetaCache(true),
            _auth
        );

        // the fake server answers the size Read with Unavailable, which is not an auth error and propagates
        await Assert.That(async () => await filter.Filter(TestEvents.Original("s", 0))).ThrowsException();

        await Assert.That(metaAuth.Single().Lease!.Value.Value).IsEqualTo("A");
        await Assert.That(sizeAuth.Single().Lease!.Value.Value).IsEqualTo("A");
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Single()).IsEqualTo("Bearer A");
    }
    [Test]
    public async Task Shutdown_cancels_an_in_flight_metadata_read() {
        using var shutdown = new CancellationTokenSource();
        var       auth     = new GrpcAuthContext(_source, shutdown.Token, _time, "reader");
        var       started  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var filter = new ScavengedEventsFilter(
            async (_, _, c) => {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, c);

                return Plain;
            },
            NoSize, new StreamMetaCache(true), auth
        );

        var pending = filter.Filter(TestEvents.Original("s", 0)).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.CancelAsync();

        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Shutdown_cancels_an_in_flight_stream_size_read() {
        using var shutdown = new CancellationTokenSource();
        var       auth     = new GrpcAuthContext(_source, shutdown.Token, _time, "reader");
        var       started  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var filter = new ScavengedEventsFilter(
            (_, _, _) => Task.FromResult(new StreamMeta(false, null, 10, 0)),
            async (_, _, c) => {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, c);

                return new StreamSize(0);
            },
            new StreamMetaCache(true), auth
        );

        var pending = filter.Filter(TestEvents.Original("s", 0)).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.CancelAsync();

        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }
}
