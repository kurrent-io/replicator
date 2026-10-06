#nullable enable
using System.Net.Http;
using System.Net.Sockets;
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;

namespace Kurrent.Replicator.Tests.Resilience;

public class TransientFailureTests {
    static RpcException Rpc(StatusCode code, Exception? debug = null) => new(new Status(code, "x", debug));

    [Test]
    [Arguments(StatusCode.Unavailable)]
    [Arguments(StatusCode.DeadlineExceeded)]
    [Arguments(StatusCode.ResourceExhausted)]
    [Arguments(StatusCode.Aborted)]
    public async Task Transient_grpc_status_codes_are_transient(StatusCode code) {
        await Assert.That(TransientFailure.IsTransient(Rpc(code))).IsTrue();
    }

    [Test]
    [Arguments(StatusCode.Unauthenticated)]
    [Arguments(StatusCode.PermissionDenied)]
    [Arguments(StatusCode.InvalidArgument)]
    [Arguments(StatusCode.FailedPrecondition)]
    [Arguments(StatusCode.Internal)]
    [Arguments(StatusCode.NotFound)]
    [Arguments(StatusCode.Cancelled)]
    [Arguments(StatusCode.Unimplemented)]
    public async Task Other_grpc_status_codes_are_not_transient(StatusCode code) {
        await Assert.That(TransientFailure.IsTransient(Rpc(code))).IsFalse();
    }

    [Test]
    public async Task Connection_failures_anywhere_in_the_chain_are_transient() {
        var tls = new HttpRequestException("The SSL connection could not be established", new IOException("reset"));

        await Assert.That(TransientFailure.IsTransient(Rpc(StatusCode.Internal, tls))).IsTrue();
        await Assert.That(TransientFailure.IsTransient(tls)).IsTrue();
        await Assert.That(TransientFailure.IsTransient(new IOException("x"))).IsTrue();
        await Assert.That(TransientFailure.IsTransient(new SocketException((int)SocketError.ConnectionRefused))).IsTrue();
        await Assert.That(TransientFailure.IsTransient(new InvalidOperationException("outer", new SocketException()))).IsTrue();
        await Assert.That(TransientFailure.IsTransient(new AggregateException(new InvalidOperationException(), Rpc(StatusCode.Unavailable)))).IsTrue();
    }

    [Test]
    public async Task Client_wrapper_for_a_leader_change_is_transient() {
        await Assert.That(TransientFailure.IsTransient(new NotLeaderException("leader", 2113))).IsTrue();
    }

    [Test]
    public async Task Application_and_auth_errors_are_not_transient() {
        await Assert.That(TransientFailure.IsTransient(new InvalidOperationException("x"))).IsFalse();
        await Assert.That(TransientFailure.IsTransient(new AccessDeniedException("x", Rpc(StatusCode.PermissionDenied)))).IsFalse();
        await Assert.That(TransientFailure.IsTransient(new OAuthTokenException("x"))).IsFalse();
        await Assert.That(TransientFailure.IsTransient(new OperationCanceledException())).IsFalse();
    }

    [Test]
    public async Task Describe_is_type_and_status_code_only() {
        var rpc = new RpcException(new Status(StatusCode.Unavailable, "Error starting gRPC call. SECRET-DETAIL", new HttpRequestException("SECRET-DEBUG")));

        await Assert.That(TransientFailure.Describe(rpc)).IsEqualTo("RpcException (Unavailable)");
        await Assert.That(TransientFailure.Describe(new HttpRequestException("SECRET"))).IsEqualTo("HttpRequestException");
        await Assert.That(TransientFailure.Describe(new InvalidOperationException("SECRET", rpc))).IsEqualTo("InvalidOperationException (Unavailable)");
    }
}
