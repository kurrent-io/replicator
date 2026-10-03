#nullable enable
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class StreamMetaCacheTests {
    static readonly StreamMeta Meta = new(false, null, null, 0);

    static Exception Denied()          => new AccessDeniedException("x", new RpcException(new Status(StatusCode.PermissionDenied, "x")));
    static Exception Unauthenticated() => new NotAuthenticatedException("x", new RpcException(new Status(StatusCode.Unauthenticated, "x")));

    [Test]
    public async Task Not_live_never_caches() {
        var cache = new StreamMetaCache();
        var calls = 0;
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await Assert.That(cache.IsLive).IsFalse();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Live_caches_and_MarkLive_clears() {
        var cache = new StreamMetaCache();
        cache.MarkLive();
        var calls = 0;
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await Assert.That(calls).IsEqualTo(1);

        await cache.GetOrAddStreamSize("s", _ => { calls++; return Task.FromResult(new StreamSize(5)); });
        cache.MarkNotLive();
        cache.MarkLive();
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await cache.GetOrAddStreamSize("s", _ => { calls++; return Task.FromResult(new StreamSize(5)); });
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    public async Task Fetch_started_before_MarkLive_is_not_stored() {
        var cache = new StreamMetaCache();
        var slow  = new TaskCompletionSource<StreamMeta>();
        var fetch = cache.GetOrAddStreamMeta("s", _ => slow.Task);
        cache.MarkLive();
        slow.SetResult(Meta with { IsDeleted = true });
        await fetch;

        var fresh = await cache.GetOrAddStreamMeta("s", _ => Task.FromResult(Meta));
        await Assert.That(fresh!.IsDeleted).IsFalse();
    }

    [Test]
    public async Task Without_flag_every_failure_returns_null_as_today() {
        var cache = new StreamMetaCache();
        await Assert.That(await cache.GetOrAddStreamMeta("s", _ => throw Denied())).IsNull();
        await Assert.That(await cache.GetOrAddStreamMeta("s", _ => throw Unauthenticated())).IsNull();
        await Assert.That(await cache.GetOrAddStreamMeta("s", _ => throw new InvalidOperationException())).IsNull();
    }

    [Test]
    public async Task With_flag_auth_failures_propagate_and_others_return_null() {
        var cache = new StreamMetaCache(failClosedOnAuthErrors: true);
        await Assert.That(async () => await cache.GetOrAddStreamMeta("s", _ => throw Denied())).Throws<AccessDeniedException>();
        await Assert.That(async () => await cache.GetOrAddStreamMeta("s", _ => throw Unauthenticated())).Throws<NotAuthenticatedException>();
        await Assert.That(async () => await cache.GetOrAddStreamMeta("s", _ => throw new OAuthTokenException("x"))).Throws<OAuthTokenException>();
        await Assert.That(async () => await cache.GetOrAddStreamMeta("s", _ => throw new OperationCanceledException())).Throws<OperationCanceledException>();
        await Assert.That(await cache.GetOrAddStreamMeta("s", _ => throw new InvalidOperationException())).IsNull();
    }
}
