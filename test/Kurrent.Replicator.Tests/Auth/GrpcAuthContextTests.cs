using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcAuthContextTests {
    readonly FakeTimeProvider        _time     = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly ControllableTokenSource _source;
    readonly GrpcAuthContext         _ctx;

    public GrpcAuthContextTests() {
        _source = new(_time) { Value = "A" };
        _ctx    = new(_source, _shutdown.Token, _time, "sink");
    }

    [Test]
    public async Task CallAuth_ToString_never_prints_the_token() {
        _source.Value = "tok-secret";
        var auth = await _ctx.AcquireCredentials(default);
        await Assert.That(auth.ToString()).DoesNotContain("tok-secret");
        await Assert.That(auth.ToString()).Contains("Generation=");
        await Assert.That(default(CallAuth).ToString()).IsEqualTo("CallAuth(none)");
    }

    static NotAuthenticatedException Unauthenticated() => new("no", new RpcException(new Status(StatusCode.Unauthenticated, "no")));

    [Test]
    public async Task None_passes_no_credentials_and_calls_once() {
        var calls = 0;
        CallAuth seen = default;
        var result = await GrpcAuthContext.None.Run((a, _) => { calls++; seen = a; return Task.FromResult(42); }, default);
        await Assert.That(result).IsEqualTo(42);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(seen.Credentials).IsNull();
        await Assert.That((await GrpcAuthContext.None.AcquireCredentials(default)).Credentials).IsNull();
        await Assert.That(GrpcAuthContext.None.Enabled).IsFalse();
    }

    [Test]
    public async Task AcquireCredentials_returns_bearer_with_lease() {
        var auth = await _ctx.AcquireCredentials(default);
        await Assert.That(auth.Credentials!.ToString()).IsEqualTo("Bearer A");
        await Assert.That(auth.Lease!.Value.Value).IsEqualTo("A");
    }

    [Test]
    public async Task Run_reports_acceptance_with_the_lease_the_call_used() {
        AccessTokenLease? used = null;
        await _ctx.Run((a, _) => { used = a.Lease; return Task.CompletedTask; }, default);
        await Assert.That(_source.Accepted.Single()).IsEqualTo(used!.Value);
    }

    [Test]
    public async Task Run_passes_the_caller_token_to_the_call() {
        using var caller = new CancellationTokenSource();
        CancellationToken seen = default;
        await _ctx.Run((_, c) => { seen = c; return Task.CompletedTask; }, caller.Token);
        await Assert.That(seen).IsEqualTo(caller.Token);
    }

    [Test]
    public async Task Run_retries_token_rejection_with_invalidation_and_new_token() {
        var headers = new List<string>();

        var run = _ctx.Run((a, _) => {
            headers.Add(a.Credentials!.ToString());
            if (headers.Count == 1) {
                _source.Value = "B";
                throw Unauthenticated();
            }

            return Task.FromResult(1);
        }, default);

        await TimeDriver.Drive(run, _time);
        await Assert.That(headers).IsEquivalentTo(new[] { "Bearer A", "Bearer B" });
        await Assert.That(_source.Invalidated.Single().Applied).IsTrue();
        await Assert.That(_source.Invalidated.Single().Lease.Value).IsEqualTo("A");
    }

    [Test]
    public async Task Run_propagates_permission_denied_and_other_errors_immediately() {
        var calls = 0;
        var denied = new AccessDeniedException("no", new RpcException(new Status(StatusCode.PermissionDenied, "no")));
        await Assert.That(async () => await _ctx.Run((_, _) => { calls++; throw denied; }, default)).Throws<AccessDeniedException>();
        await Assert.That(async () => await _ctx.Run((_, _) => { calls++; throw new InvalidOperationException(); }, default)).Throws<InvalidOperationException>();
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(_source.Invalidated).IsEmpty();
    }

    [Test]
    public async Task Shutdown_ends_the_retry_loop() {
        _source.Value = null;
        var run = _ctx.Run((_, _) => Task.FromResult(1), default);
        await TimeDriver.Until(() => _source.Calls >= 2, _time);
        await _shutdown.CancelAsync();
        await Assert.That(async () => await run).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ReportFailure_only_invalidates_on_token_failures() {
        var auth = await _ctx.AcquireCredentials(default);
        _ctx.ReportFailure(auth, new InvalidOperationException());
        _ctx.ReportFailure(auth, null);
        await Assert.That(_source.Invalidated).IsEmpty();
        _ctx.ReportFailure(auth, Unauthenticated());
        await Assert.That(_source.Invalidated.Single().Applied).IsTrue();
    }
}
