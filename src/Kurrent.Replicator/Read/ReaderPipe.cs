using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using GreenPipes;
using Kurrent.Replicator.Observers;
using Kurrent.Replicator.Prepare;

namespace Kurrent.Replicator.Read;

public class ReaderPipe {
    readonly IPipe<ReaderContext> _pipe;

    public ReaderPipe(IEventReader reader, ICheckpointStore checkpointStore, Func<PrepareContext, ValueTask> send) {
        var log = LogProvider.GetCurrentClassLogger();

        _pipe = Pipe.New<ReaderContext>(cfg => {
                cfg.UseConcurrencyLimit(1);

                cfg.UseRetry(retry => {
                        retry.Incremental(50, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
                        retry.ConnectRetryObserver(new LoggingRetryObserver());
                    }
                );
                cfg.UseLog();
                cfg.UseExecuteAsync(Reader);
            }
        );

        return;

        async Task Reader(ReaderContext ctx) {
            try {
                var start = await checkpointStore.LoadCheckpoint(ctx.CancellationToken).ConfigureAwait(false);
                log.Info("Reading from {Position}", start);

                await reader.ReadEvents(
                        start,
                        async read => {
                            ReplicationMetrics.ReadingPosition.Set(read.LogPosition.EventPosition);
                            await send(new(read, ctx.CancellationToken)).ConfigureAwait(false);
                        },
                        ctx.CancellationToken
                    )
                    .ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // it's ok
            } catch (Exception e) {
                log.Error(e, "Reader error");
            } finally {
                log.Info("Reader stopped");
            }
        }
    }

    public async Task Start(CancellationToken stoppingToken) {
        try {
            await _pipe.Send(new(stoppingToken));
        } catch (Exception e) {
            Console.WriteLine(e);

            throw;
        }
    }
}

public class ReaderContext(CancellationToken cancellationToken) : BasePipeContext(cancellationToken), PipeContext;
