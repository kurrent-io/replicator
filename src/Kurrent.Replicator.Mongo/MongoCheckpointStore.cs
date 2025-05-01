using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Logging;
using MongoDB.Driver;

namespace Kurrent.Replicator.Mongo;

public class MongoCheckpointStore : ICheckpointStore {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    readonly string                       _id;
    readonly int                          _checkpointAfter;
    readonly IMongoCollection<Checkpoint> _collection;

    public MongoCheckpointStore(string connectionString, string database, string id, int checkpointAfter) {
        _id              = id;
        _checkpointAfter = checkpointAfter;
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        var db       = new MongoClient(settings).GetDatabase(database);
        _collection = db.GetCollection<Checkpoint>("checkpoint");
    }

    int          _counter;
    LogPosition? _lastPosition;

    public async ValueTask<bool> HasStoredCheckpoint(CancellationToken cancellationToken) {
        var doc = await _collection
            .Find(x => x.Id == _id)
            .Limit(1)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return doc != null;
    }

    public async ValueTask<LogPosition> LoadCheckpoint(CancellationToken cancellationToken) {
        if (_lastPosition != null) {
            Log.Info("Starting from a previously known checkpoint {LastKnown}", _lastPosition);

            return _lastPosition;
        }

        var doc = await _collection
            .Find(x => x.Id == _id)
            .Limit(1)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (doc == null) {
            Log.Info("No checkpoint file found, starting from the beginning");

            await _collection.InsertOneAsync(new(_id, LogPosition.Start), cancellationToken: cancellationToken).ConfigureAwait(false);

            return LogPosition.Start;
        }

        Log.Info("Loaded the checkpoint from MongoDB: {Checkpoint}", doc.LogPosition);

        return doc.LogPosition;
    }

    public async ValueTask StoreCheckpoint(LogPosition logPosition, CancellationToken cancellationToken) {
        _lastPosition = logPosition;

        Interlocked.Increment(ref _counter);

        if (_counter < _checkpointAfter) return;

        await Flush(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _counter, 0);
    }

    public async ValueTask Flush(CancellationToken cancellationToken) {
        if (_lastPosition == null) return;

        await _collection.ReplaceOneAsync(Builders<Checkpoint>.Filter.Eq(x => x.Id, _id), new(_id, _lastPosition), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    record Checkpoint(string Id, LogPosition LogPosition);
}
