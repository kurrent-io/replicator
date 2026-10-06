#nullable enable
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb.Auth;

namespace Kurrent.Replicator.Tests.Auth;

public class AuthFailureTests {
    static RpcException Rpc(StatusCode code, Exception? debug = null) => new(new Status(code, "x", debug));

    [Test]
    public async Task Token_failures_are_recognised() {
        await Assert.That(AuthFailure.IsTokenFailure(new OAuthTokenException("x"))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(Rpc(StatusCode.Unauthenticated))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(new NotAuthenticatedException("x", Rpc(StatusCode.Unauthenticated)))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(Rpc(StatusCode.Internal, new OAuthTokenException("x")))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(new AggregateException(new InvalidOperationException(), new OAuthTokenException("x")))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(new InvalidOperationException("outer", new OAuthTokenException("x")))).IsTrue();
    }

    [Test]
    public async Task Non_token_failures_are_not_token_failures() {
        await Assert.That(AuthFailure.IsTokenFailure(Rpc(StatusCode.PermissionDenied))).IsFalse();
        await Assert.That(AuthFailure.IsTokenFailure(new AccessDeniedException("x", Rpc(StatusCode.PermissionDenied)))).IsFalse();
        await Assert.That(AuthFailure.IsTokenFailure(Rpc(StatusCode.Unavailable))).IsFalse();
        await Assert.That(AuthFailure.IsTokenFailure(new OperationCanceledException())).IsFalse();
    }

    [Test]
    public async Task Permission_denied_is_recognised() {
        await Assert.That(AuthFailure.IsPermissionDenied(Rpc(StatusCode.PermissionDenied))).IsTrue();
        await Assert.That(AuthFailure.IsPermissionDenied(new AccessDeniedException("x", Rpc(StatusCode.PermissionDenied)))).IsTrue();
        await Assert.That(AuthFailure.IsPermissionDenied(Rpc(StatusCode.Unauthenticated))).IsFalse();
    }

    [Test]
    public async Task Self_referencing_chains_terminate() {
        var agg = new AggregateException(new AggregateException(new AggregateException(new InvalidOperationException())));
        await Assert.That(AuthFailure.IsTokenFailure(agg)).IsFalse();
    }

    [Test]
    public async Task Describe_returns_the_oauth_exception_message_verbatim() {
        await Assert.That(AuthFailure.Describe(new OAuthTokenException("token source is quarantined"))).IsEqualTo("token source is quarantined");
    }

    [Test]
    public async Task Describe_never_echoes_server_supplied_status_detail() {
        var rpc = Rpc(StatusCode.Unauthenticated); // status detail is "x", not secret, but Describe must not use it at all
        await Assert.That(AuthFailure.Describe(rpc)).IsEqualTo("KurrentDB rejected the access token (Unauthenticated)");
        await Assert.That(AuthFailure.Describe(rpc)).DoesNotContain("x");

        var notAuth = new NotAuthenticatedException("Bearer SECRET-XYZ leaked here", rpc);
        await Assert.That(AuthFailure.Describe(notAuth)).IsEqualTo("KurrentDB rejected the access token (Unauthenticated)");
        await Assert.That(AuthFailure.Describe(notAuth)).DoesNotContain("SECRET-XYZ");
    }

    [Test]
    public async Task Describe_falls_back_to_exception_type_and_status_code() {
        await Assert.That(AuthFailure.Describe(new InvalidOperationException("secret detail"))).IsEqualTo("InvalidOperationException");
        await Assert.That(AuthFailure.Describe(Rpc(StatusCode.Unavailable))).IsEqualTo("RpcException (Unavailable)");
    }
}
