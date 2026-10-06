using System.Net.Sockets;
using Grpc.Core;

namespace Kurrent.Replicator.KurrentDb;

/// <summary>
/// Classifies failures of a KurrentDB call that are worth retrying as-is: the node is unreachable, restarting,
/// overloaded or changing leader. Retrying a write is safe because the replicator appends with
/// <see cref="StreamState.Any"/> and fixed event ids, so a write that did land is deduplicated.
/// </summary>
public static class TransientFailure {
    public static bool IsTransient(Exception e)
        => AuthFailure.Chain(e).Any(
            x => x is RpcException {
                    StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted
                }
                or NotLeaderException
                or HttpRequestException
                or IOException
                or SocketException
        );

    /// <summary>
    /// Exception type plus gRPC status code. Never includes the status detail or exception messages, which can
    /// carry server-supplied text.
    /// </summary>
    public static string Describe(Exception e) => AuthFailure.Describe(e);
}
