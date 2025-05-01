using Kurrent.Replicator.Shared;

namespace Kurrent.Replicator;

public class NoCheckpointSeeder : ICheckpointSeeder {
    public ValueTask Seed(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}