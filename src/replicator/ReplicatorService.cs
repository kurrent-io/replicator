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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Replicator.Replicate(
                reader,
                writer,
                sinkOptions,
                prepareOptions,
                checkpointSeeder,
                checkpointStore,
                replicatorOptions,
                stoppingToken
            );
        } catch (ReplicatorFailedException) when (replicatorOptions.RestartOnFailure) {
            // Already logged by the replicator. Fault the service so the host stops (BackgroundServiceExceptionBehavior.StopHost),
            // and exit non-zero so that a process supervisor or container orchestrator restarts the replicator.
            Environment.ExitCode = 1;

            throw;
        } catch (ReplicatorFailedException) {
            // RestartOnFailure is off: keep the process (and its HTTP API) up, with replication stopped.
        }
    }
}
