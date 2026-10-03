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
}
