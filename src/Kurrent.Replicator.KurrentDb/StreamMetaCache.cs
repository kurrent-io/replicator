using System.Collections.Concurrent;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared.Logging;

namespace Kurrent.Replicator.KurrentDb;

/// <summary>
/// Metadata and last-event-number cache used by the scavenge filter. Only trusted while "live", i.e. while the
/// realtime subscription is delivering updates; otherwise every read goes to the server.
/// </summary>
class StreamMetaCache(bool failClosedOnAuthErrors = false) {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    readonly ConcurrentDictionary<string, StreamSize> _streamsSize = new();
    readonly ConcurrentDictionary<string, StreamMeta> _streamsMeta = new();
    readonly object                                   _lock        = new();

    bool _live;
    long _epoch;

    public bool IsLive {
        get { lock (_lock) return _live; }
    }

    public void MarkNotLive() {
        lock (_lock) _live = false;
    }

    public void MarkLive() {
        lock (_lock) {
            _streamsMeta.Clear();
            _streamsSize.Clear();
            _epoch++;
            _live = true;
        }
    }

    public async Task<StreamMeta?> GetOrAddStreamMeta(string stream, Func<string, Task<StreamMeta>> getMeta) {
        try {
            return await GetOrFetch(_streamsMeta, stream, getMeta).ConfigureAwait(false);
        } catch (Exception e) when (!FailClosed(e)) {
            Log.Warn(e, "Unable to read metadata for stream {Stream}", stream);

            return null;
        }
    }

    public Task<StreamSize> GetOrAddStreamSize(string stream, Func<string, Task<StreamSize>> getSize)
        => GetOrFetch(_streamsSize, stream, getSize);

    bool FailClosed(Exception e)
        => failClosedOnAuthErrors && (e is OperationCanceledException || AuthFailure.IsTokenFailure(e) || AuthFailure.IsPermissionDenied(e));

    async Task<T> GetOrFetch<T>(ConcurrentDictionary<string, T> dict, string key, Func<string, Task<T>> fetch) {
        long epoch;

        lock (_lock) {
            epoch = _epoch;

            if (_live && dict.TryGetValue(key, out var cached)) return cached;
        }

        var value = await fetch(key).ConfigureAwait(false);

        lock (_lock) {
            if (_live && _epoch == epoch) dict.TryAdd(key, value);
        }

        return value;
    }

    public void UpdateStreamMeta(string stream, StreamMetadata streamMetadata, long version) {
        var isDeleted = IsStreamDeleted(streamMetadata);

        if (!_streamsMeta.TryGetValue(stream, out var meta)) {
            _streamsMeta[stream] = new(isDeleted, streamMetadata.MaxAge, streamMetadata.MaxCount, version);

            return;
        }

        if (meta.Version > version)
            return;

        if (streamMetadata.MaxAge.HasValue)
            meta = meta with { MaxAge = streamMetadata.MaxAge };

        if (streamMetadata.MaxCount.HasValue)
            meta = meta with { MaxCount = streamMetadata.MaxCount };

        if (isDeleted)
            meta = meta with { IsDeleted = true };

        _streamsMeta[stream] = meta;
    }

    public void UpdateStreamLastEventNumber(string stream, long lastEventNumber) {
        if (!_streamsSize.TryGetValue(stream, out var size) || size.LastEventNumber < lastEventNumber) {
            _streamsSize[stream] = new(lastEventNumber);
        }
    }

    static bool IsStreamDeleted(StreamMetadata meta) => meta.TruncateBefore == long.MaxValue;
}

record StreamSize(long LastEventNumber) {
    /// <summary>A stream with no events (not found, or metadata only): no event number is over its max count.</summary>
    public static readonly StreamSize Empty = new(-1);
}

record StreamMeta(bool IsDeleted, TimeSpan? MaxAge, long? MaxCount, long Version);
