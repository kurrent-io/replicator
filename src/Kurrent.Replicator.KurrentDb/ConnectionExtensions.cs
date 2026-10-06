namespace Kurrent.Replicator.KurrentDb;

static class ConnectionExtensions {
    /// <summary>
    /// The last event number of <paramref name="stream"/>, or <see cref="StreamSize.Empty"/> when it has no events: a
    /// stream that only has metadata (KurrentDB 26 creates such system streams, e.g. <c>$connectors-mngt/state-projection</c>)
    /// reads as not found (DEV-1903). Any other failure, auth failures included, propagates.
    /// In EventStore.Client 23.3.8 a not-found stream surfaces as <see cref="StreamNotFoundException"/> while enumerating;
    /// <c>ReadState</c> is not awaited because it never completes when the server sends no messages at all.
    /// </summary>
    public static async Task<StreamSize> GetStreamSize(this EventStoreClient client, string stream, UserCredentials? credentials = null, CancellationToken ct = default) {
        var read = client.ReadStreamAsync(Direction.Backwards, stream, StreamPosition.End, 1, userCredentials: credentials, cancellationToken: ct);

        try {
            var last = await read.ToArrayAsync(ct).ConfigureAwait(false);

            return last.Length == 0 ? StreamSize.Empty : new(last[0].OriginalEventNumber.ToInt64());
        } catch (StreamNotFoundException) {
            return StreamSize.Empty;
        }
    }

    public static async Task<StreamMeta> GetStreamMeta(this EventStoreClient client, string stream, UserCredentials? credentials = null, CancellationToken ct = default) {
        var streamMeta = await client.GetStreamMetadataAsync(stream, userCredentials: credentials, cancellationToken: ct).ConfigureAwait(false);

        var streamDeleted = streamMeta.StreamDeleted || streamMeta.Metadata.TruncateBefore == StreamPosition.End;

        return new(
            streamDeleted,
            streamMeta.Metadata.MaxAge,
            streamMeta.Metadata.MaxCount,
            // no revision: the stream has no metadata at all (nothing to scavenge by), not an error
            streamMeta.MetastreamRevision?.ToInt64() ?? -1
        );
    }
}
