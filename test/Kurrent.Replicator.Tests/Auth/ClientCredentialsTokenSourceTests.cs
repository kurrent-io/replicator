#nullable enable
using System.Net;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class ClientCredentialsTokenSourceTests {
    readonly FakeTimeProvider        _time     = new();
    readonly StubTokenEndpoint       _stub     = new();
    readonly CancellationTokenSource _shutdown = new();

    static readonly GrpcAuthOptions Options = new() {
        Type = GrpcAuthType.OAuthClientCredentials, TokenEndpoint = "https://idp.example.com/t", ClientId = "c", ClientSecret = "s"
    };

    ClientCredentialsTokenSource NewSource(GrpcAuthOptions? o = null) => new(o ?? Options, "sink", _stub, _time, _shutdown.Token);

    void Respond(params string[] tokens) {
        var i = 0;
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token(tokens[Math.Min(i++, tokens.Length - 1)]));
    }

    void Fail() => _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":\"temporarily_unavailable\"}");

    [Test]
    public async Task Caches_until_refresh_window() {
        Respond("A", "B");
        var s = NewSource();
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
        _time.Advance(TimeSpan.FromSeconds(3299)); // 3600 - 300 - 1
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("B");
        await Assert.That(_stub.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Very_short_lifetime_does_not_refresh_on_every_call() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token("A", 60));
        var s = NewSource(); // refreshBefore 300 > lifetime: window capped at 30s
        await s.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(29));
        await s.GetAccessToken(default);
        await s.GetAccessToken(default);
        await Assert.That(_stub.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_callers_share_one_successful_request() {
        Respond("A");
        _stub.Gate = new TaskCompletionSource();
        var s     = NewSource();
        var calls = Enumerable.Range(0, 10).Select(_ => s.GetAccessToken(default).AsTask()).ToList();
        await Task.Delay(50);
        _stub.Gate.SetResult();
        var leases = await Task.WhenAll(calls);
        await Assert.That(_stub.Count).IsEqualTo(1);
        await Assert.That(leases.Select(l => l.Value).Distinct().Single()).IsEqualTo("A");
    }

    [Test]
    public async Task Concurrent_callers_share_one_failing_request_and_its_exception() {
        Fail();
        _stub.Gate = new TaskCompletionSource();
        var s     = NewSource();
        var calls = Enumerable.Range(0, 10).Select(_ => s.GetAccessToken(default).AsTask()).ToList();
        await Task.Delay(50);
        _stub.Gate.SetResult();

        foreach (var call in calls) await Assert.That(async () => await call).Throws<OAuthTokenException>();
        await Assert.That(_stub.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cooldown_suppresses_requests_for_five_seconds() {
        Fail();
        var s = NewSource();
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
        _time.Advance(TimeSpan.FromSeconds(4));
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
        await Assert.That(_stub.Count).IsEqualTo(1);
        _time.Advance(TimeSpan.FromSeconds(1));
        Respond("A");
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
        await Assert.That(_stub.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Refresh_failure_falls_back_to_usable_cached_token() {
        Respond("A");
        var s = NewSource();
        await s.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(3400)); // inside refresh window, 200s before expiry
        Fail();
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
    }

    [Test]
    public async Task Refresh_failure_with_expired_token_throws_with_status_and_code() {
        Respond("A");
        var s = NewSource();
        await s.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(3580)); // within 30s of expiry: not usable
        Fail();
        var ex = await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).Contains("503");
        await Assert.That(ex.Message).Contains("temporarily_unavailable");
    }

    [Test]
    public async Task Invalidate_forces_refresh_and_disables_fallback() {
        Respond("A");
        var s = NewSource();
        var a = await s.GetAccessToken(default);
        s.Invalidate(a);
        Fail();
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
        await Assert.That(_stub.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Reissued_rejected_token_is_quarantined_then_probed() {
        Respond("A");
        var s = NewSource();
        s.Invalidate(await s.GetAccessToken(default));
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>(); // endpoint re-issues A
        _time.Advance(TimeSpan.FromSeconds(61));
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
    }

    [Test]
    public async Task Same_value_refresh_does_not_lose_rejection() {
        Respond("A");
        var s     = NewSource();
        var call1 = await s.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(3300)); // refresh re-issues A, same generation
        var again = await s.GetAccessToken(default);
        await Assert.That(again.Generation).IsEqualTo(call1.Generation);
        s.Invalidate(call1);
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Caller_cancellation_does_not_cancel_shared_request() {
        Respond("A");
        _stub.Gate = new TaskCompletionSource();
        var s = NewSource();
        using var cts = new CancellationTokenSource();
        var cancelled = s.GetAccessToken(cts.Token).AsTask();
        var other     = s.GetAccessToken(default).AsTask();
        await Task.Delay(20);
        await cts.CancelAsync();
        await Assert.That(async () => await cancelled).Throws<OperationCanceledException>();
        _stub.Gate.SetResult();
        await Assert.That((await other).Value).IsEqualTo("A");
        await Assert.That(_stub.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Shutdown_aborts_in_flight_request() {
        Respond("A");
        _stub.Gate = new TaskCompletionSource(); // never released
        var s    = NewSource();
        var call = s.GetAccessToken(default).AsTask();
        await Task.Delay(20);
        await _shutdown.CancelAsync();
        await Assert.That(async () => await call.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Request_times_out_after_30_seconds() {
        Respond("A");
        _stub.Gate = new TaskCompletionSource(); // never released
        var s    = NewSource();
        var call = s.GetAccessToken(default).AsTask();
        await Task.Delay(20);
        _time.Advance(TimeSpan.FromSeconds(31));
        var ex = await Assert.That(async () => await call.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).Contains("timed out");
    }
}
