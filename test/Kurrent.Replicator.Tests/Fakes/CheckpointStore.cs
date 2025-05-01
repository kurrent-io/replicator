using Kurrent.Replicator.Shared;

namespace Kurrent.Replicator.Tests.Fakes; 

public class CheckpointStore : ICheckpointStore {
    public ValueTask<bool> HasStoredCheckpoint(CancellationToken cancellationToken)
        => ValueTask.FromResult(true);
    
    public ValueTask<LogPosition> LoadCheckpoint(CancellationToken cancellationToken)
        => ValueTask.FromResult(LogPosition.Start);

    public ValueTask StoreCheckpoint(LogPosition logPosition, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask Flush(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}