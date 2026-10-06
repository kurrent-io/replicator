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

    static readonly TimeSpan WriterStopGracePeriod = TimeSpan.FromSeconds(5);

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

        var writerCts = new CancellationTokenSource();

        var readerPipe = new ReaderPipe(
            reader,
            checkpointStore,
            ctx => prepareChannel.Writer.WriteAsync(ctx, ctx.CancellationToken)
        );

        // writerCts (not ctx.CancellationToken, which is None) so a hand-off blocked on a full sink channel ends at shutdown
        var preparePipe = new PreparePipe(
            preparePipeOptions.Filter,
            preparePipeOptions.Transform,
            ctx => sinkChannel.Writer.WriteAsync(ctx, writerCts.Token)
        );
        var sinkPipe = new SinkPipe(writer, sinkPipeOptions, checkpointStore);

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
        // The writer only ends on its own when a write failed beyond retry (or was cancelled by shutdown). Nothing read
        // from now on can be written, so stop the reader instead of re-reading the same events forever.
        var writerDied = false;

        _ = writerTask.ContinueWith(
            _ => {
                if (writerCts.IsCancellationRequested) return;

                writerDied = true;
                cts.Cancel();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        // Own token so the reporter also stops (and is awaited) when replication ends without a shutdown, e.g. on a
        // writer failure: nothing started by Replicate may outlive it.
        using var reporterCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var       reporter    = Task.Run(() => Report(reporterCts.Token), CancellationToken.None);

        try {
            await writer.Start();
            await checkpointSeeder.Seed(stoppingToken);
        } catch {
            await StopReporter().ConfigureAwait(false);

            throw;
        }

        var stopping = false;

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

                while (sinkChannel.Reader.Count > 0 && !writerTask.IsCompleted) {
                    await checkpointStore.Flush(CancellationToken.None).ConfigureAwait(false);
                    Log.Info("Waiting for the sink pipe to exhaust ({Left} left)...", sinkChannel.Reader.Count);
                    await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
                }

                if (writerTask.IsCompleted && sinkChannel.Reader.Count > 0) {
                    Log.Warn(
                        "Writer stopped with {Count} events not written; they will be read again from the last checkpoint",
                        sinkChannel.Reader.Count
                    );
                }

                await Flush().ConfigureAwait(false);

                if (stopping) {
                    await writerCts.CancelAsync();
                    sinkChannel.Writer.Complete();

                    break;
                }

                Log.Info("Will restart in {0} sec", replicatorOptions.RestartDelay.TotalSeconds);

                if (replicatorOptions.RestartDelay != TimeSpan.Zero) {
                    try {
                        await Task.Delay(replicatorOptions.RestartDelay, linkedCts.Token);
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
            if (!writerDied) Log.Error(e, "Error stopping pending tasks");
        }

        await Flush().ConfigureAwait(false);

        await StopReporter().ConfigureAwait(false);

        if (writerDied) {
            // A writer cancelled by shutdown (ApplicationStopping, e.g. while it waited out a sink outage) ends just
            // before the stopping token fires: give the host a moment before calling it a failure.
            if (!writerTask.IsFaulted) {
                try {
                    await Task.Delay(WriterStopGracePeriod, stoppingToken).ConfigureAwait(false);
                } catch (OperationCanceledException) { }
            }

            if (!stoppingToken.IsCancellationRequested) {
                var error = writerTask.Exception?.GetBaseException();

                if (error != null) Log.Error(error, "Writer stopped unexpectedly; replication cannot continue in this process");
                else Log.Error("Writer stopped unexpectedly; replication cannot continue in this process");

                throw new ReplicatorFailedException("The writer stopped unexpectedly; replication cannot continue in this process", error);
            }
        }

        Log.Info("Replicator stopped");

        return;

        void Stop() {
            Log.Info("Replicator stopping...");
            stopping = true;
            writerCts.Cancel();
        }

        async Task StopReporter() {
            await reporterCts.CancelAsync().ConfigureAwait(false);
            await reporter.ConfigureAwait(false);
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

        async Task Report(CancellationToken token) {
            DateTimeOffset? lastWarning = null;

            while (!token.IsCancellationRequested) {
                try {
                    var position = await reader.GetLastPosition(token).ConfigureAwait(false);

                    if (position.HasValue) {
                        ReplicationMetrics.LastSourcePosition.Set(position.Value);
                    }
                } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                    break;
                } catch (Exception e) {
                    var now = DateTimeOffset.UtcNow;

                    if (lastWarning is null || now - lastWarning > TimeSpan.FromMinutes(1)) {
                        lastWarning = now;
                        Log.Warn(e, "Unable to read the source position for metrics; will retry");
                    }
                }

                try {
                    await Task.Delay(replicatorOptions.ReportMetricsFrequency, token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
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
