namespace Kurrent.Replicator.Shared;

public interface ICheckpointSeeder {
    ValueTask Seed(CancellationToken cancellationToken);
}