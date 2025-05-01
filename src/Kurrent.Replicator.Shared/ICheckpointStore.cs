namespace Kurrent.Replicator.Shared; 

public interface ICheckpointStore {
    ValueTask<bool> HasStoredCheckpoint(CancellationToken cancellationToken);
    
    ValueTask<LogPosition> LoadCheckpoint(CancellationToken cancellationToken);

    ValueTask StoreCheckpoint(LogPosition logPosition, CancellationToken cancellationToken);

    ValueTask Flush(CancellationToken cancellationToken);
}