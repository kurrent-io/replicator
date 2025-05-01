using Kurrent.Replicator;
using Kurrent.Replicator.Prepare;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Sink;

namespace replicator;

public class ReplicatorService(
        IEventReader           reader,
        IEventWriter           writer,
        SinkPipeOptions        sinkOptions,
        PreparePipelineOptions prepareOptions,
        ReplicatorOptions      replicatorOptions,
        ICheckpointSeeder      checkpointSeeder,
        ICheckpointStore       checkpointStore
    )
    : BackgroundService {
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Replicator.Replicate(
            reader,
            writer,
            sinkOptions,
            prepareOptions,
            checkpointSeeder,
            checkpointStore,
            replicatorOptions,
            stoppingToken
        );
}