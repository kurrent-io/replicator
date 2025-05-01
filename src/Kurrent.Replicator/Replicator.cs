using System.Threading.Channels;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Prepare;
using Kurrent.Replicator.Read;
using Kurrent.Replicator.Sink;
using Ubiquitous.Metrics;

namespace Kurrent.Replicator;

public static class Replicator {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    public static async Task Replicate(
            IEventReader           reader,
            IEventWriter           writer,
            SinkPipeOptions        sinkPipeOptions,
            PreparePipelineOptions preparePipeOptions,
            ICheckpointSeeder      checkpointSeeder,
            ICheckpointStore       checkpointStore,
            ReplicatorOptions      replicatorOptions,
            CancellationToken      stoppingToken
        ) {
        ReplicationMetrics.SetCapacity(preparePipeOptions.BufferSize, sinkPipeOptions.BufferSize);

        var cts            = new CancellationTokenSource();
        var linkedCts      = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);
        var prepareChannel = Channel.CreateBounded<PrepareContext>(preparePipeOptions.BufferSize);
        var sinkChannel    = Channel.CreateBounded<SinkContext>(sinkPipeOptions.BufferSize);

        var readerPipe = new ReaderPipe(
            reader,
            checkpointStore,
            ctx => prepareChannel.Writer.WriteAsync(ctx, ctx.CancellationToken)
        );

        var preparePipe = new PreparePipe(
            preparePipeOptions.Filter,
            preparePipeOptions.Transform,
            ctx => sinkChannel.Writer.WriteAsync(ctx, ctx.CancellationToken)
        );
        var sinkPipe  = new SinkPipe(writer, sinkPipeOptions, checkpointStore);
        var writerCts = new CancellationTokenSource();

        var prepareTask = CreateChannelShovel(
            "Prepare",
            prepareChannel,
            preparePipe.Send,
            ReplicationMetrics.PrepareChannelSize,
            linkedCts.Token
        );

        var writerTask = CreateChannelShovel(
            "Writer",
            sinkChannel,
            sinkPipe.Send,
            ReplicationMetrics.SinkChannelSize,
            writerCts.Token
        );
        var reporter = Task.Run(Report, stoppingToken);

        await writer.Start();

        var stopping = false;

        await checkpointSeeder.Seed(stoppingToken);

        try {
            while (!stopping) {
                ReplicationStatus.Start();

                await readerPipe.Start(linkedCts.Token).ConfigureAwait(false);
                stopping = linkedCts.IsCancellationRequested || !replicatorOptions.RunContinuously;

                if (stopping) {
                    Log.Info("Replicator stopping");
                }

                if (!replicatorOptions.RunContinuously) {
                    do {
                        Log.Info("Closing the prepare channel...");
                        await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
                    } while (!prepareChannel.Writer.TryComplete());
                }

                ReplicationStatus.Stop();

                while (sinkChannel.Reader.Count > 0) {
                    await checkpointStore.Flush(CancellationToken.None).ConfigureAwait(false);
                    Log.Info("Waiting for the sink pipe to exhaust ({Left} left)...", sinkChannel.Reader.Count);
                    await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
                }

                await Flush().ConfigureAwait(false);

                if (stopping) {
                    sinkChannel.Writer.Complete();
                    await writerCts.CancelAsync();

                    break;
                }

                Log.Info("Will restart in {0} sec", replicatorOptions.RestartDelay.TotalSeconds);

                if (replicatorOptions.RestartDelay != TimeSpan.Zero) {
                    try {
                        await Task.Delay(replicatorOptions.RestartDelay, stoppingToken);
                    } catch (OperationCanceledException) {
                        // stopping now
                        break;
                    }
                }
            }
        } catch (Exception e) {
            Log.Error(e, "Replicator crashed");
        } finally {
            Stop();
        }

        try {
            await prepareTask.ConfigureAwait(false);
            await writerTask.ConfigureAwait(false);
        } catch (OperationCanceledException) { } catch (Exception e) {
            Log.Error(e, "Error stopping pending tasks");
        }

        await Flush().ConfigureAwait(false);

        Log.Info("Replicator stopped");

        return;

        void Stop() {
            Log.Info("Replicator stopping...");
            stopping = true;
            writerCts.Cancel();
        }

        async Task Flush() {
            Log.Info("Storing the last known checkpoint");
            await checkpointStore.Flush(CancellationToken.None).ConfigureAwait(false);
        }

        static Task CreateChannelShovel<T>(
                string            name,
                Channel<T>        channel,
                Func<T, Task>     send,
                IGaugeMetric      size,
                CancellationToken token
            )
            => Task.Run(() => channel.Shovel(send, () => Log.Info($"{name} started"), () => Log.Info($"{name} stopped"), size, token), token);

        async Task Report() {
            try {
                while (!stoppingToken.IsCancellationRequested) {
                    var position = await reader.GetLastPosition(stoppingToken).ConfigureAwait(false);

                    if (position.HasValue) {
                        ReplicationMetrics.LastSourcePosition.Set(position.Value);
                    }

                    await Task.Delay(replicatorOptions.ReportMetricsFrequency, stoppingToken).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) {
                // it's ok
            }

            Log.Info("Reporting stopped");
        }
    }
}

static class ChannelExtensions {
    public static async Task Shovel<T>(
            this Channel<T>   channel,
            Func<T, Task>     send,
            Action            beforeStart,
            Action            afterStop,
            IGaugeMetric      channelSizeGauge,
            CancellationToken token
        ) {
        beforeStart();

        try {
            while (!token.IsCancellationRequested         &&
                   !channel.Reader.Completion.IsCompleted &&
                   await channel.Reader.WaitToReadAsync(token).ConfigureAwait(false)) {
                await foreach (var ctx in channel.Reader.ReadAllAsync(token).ConfigureAwait(false)) {
                    await send(ctx).ConfigureAwait(false);
                    channelSizeGauge.Set(channel.Reader.Count);
                }
            }
        } catch (OperationCanceledException) {
            // it's ok
        }

        afterStop();
    }
}
