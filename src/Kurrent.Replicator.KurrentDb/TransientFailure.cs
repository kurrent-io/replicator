using System.Net.Sockets;
using Grpc.Core;

namespace Kurrent.Replicator.KurrentDb;

/// <summary>
/// Classifies failures of a KurrentDB call that are worth retrying as-is: the node is unreachable, restarting,
/// overloaded or changing leader. A write whose response was lost may have landed, so a retry must be idempotent:
/// events and stream metadata are appended with <see cref="StreamState.Any"/> and the source event's id (metadata as a
/// <c>$metadata</c> event on <c>$$stream</c>, not via SetStreamMetadataAsync, which mints a new id per call), so
/// KurrentDB deduplicates the repeat. A repeated soft delete with <see cref="StreamState.Any"/> succeeds and leaves
/// the stream deleted.
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
