namespace Kurrent.Replicator.KurrentDb;

static class ConnectionExtensions {
    public static async Task<StreamSize> GetStreamSize(this EventStoreClient client, string stream, UserCredentials? credentials = null, CancellationToken ct = default) {
        var read = client.ReadStreamAsync(Direction.Backwards, stream, StreamPosition.End, 1, userCredentials: credentials, cancellationToken: ct);
        var last = await read.ToArrayAsync(ct).ConfigureAwait(false);

        return new(last[0].OriginalEventNumber.ToInt64());
    }

    public static async Task<StreamMeta> GetStreamMeta(this EventStoreClient client, string stream, UserCredentials? credentials = null, CancellationToken ct = default) {
        var streamMeta = await client.GetStreamMetadataAsync(stream, userCredentials: credentials, cancellationToken: ct).ConfigureAwait(false);

        var streamDeleted = streamMeta.StreamDeleted || streamMeta.Metadata.TruncateBefore == StreamPosition.End;

        return new(
            streamDeleted,
            streamMeta.Metadata.MaxAge,
            streamMeta.Metadata.MaxCount,
            streamMeta.MetastreamRevision!.Value.ToInt64()
        );
    }
}
