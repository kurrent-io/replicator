using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using GreenPipes;
using GreenPipes.Agents;
using Kurrent.Replicator.Sink;

namespace Kurrent.Replicator.Partitioning; 

public class PartitionChannel : Agent {
    readonly int                             _index;
    readonly bool                            _ignoreMetadataEventsForPartitioning;
    readonly Task                            _reader;
    readonly ChannelWriter<DelayedOperation> _writer;

    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    long _writeSequence;
    
    // Tracks the last successfully written event for this partition so we can
    // log the boundary between "good" and "bad" ordering on sequence errors.
    BaseProposedEvent? _lastWrittenEvent;

    // Best-effort in-memory buffer snapshot of the most recent events routed
    // through this partition. This does not affect sequencing logic and is
    // only used for diagnostic logging when a sequence error occurs.
    readonly Queue<BaseProposedEvent> _recentEvents = new();

    // To avoid logging unbounded state, keep only a small sliding window of
    // recent events per partition.
    const int RecentEventsLimit = 32;

    public PartitionChannel(int index, bool ignoreMetadataEventsForPartitioning) {
        _index = index;
        _ignoreMetadataEventsForPartitioning = ignoreMetadataEventsForPartitioning;
        var channel = Channel.CreateBounded<DelayedOperation>(1);
        _reader = Task.Run(() => Reader(channel.Reader));
        _writer = channel.Writer;
    }

    public async Task Send<T>(T context, IPipe<T> next) where T : class, PipeContext
        => await _writer.WriteAsync(new(context as SinkContext, next as IPipe<SinkContext>));

    async Task Reader(ChannelReader<DelayedOperation> reader) {
        while (!IsStopping) {
            try {
                var (context, pipe) = await reader.ReadAsync(Stopping);

                if (context == null || pipe == null)
                    throw new InvalidCastException("Wrong context type, expected SinkContext");

                var proposedEvent    = context.ProposedEvent;
                // Use the global commit position for ordering instead of SequenceNumber,
                // which is per-stream and can reset to 0 for new streams causing false
                // sequence errors.
                var proposedSequence = (long)proposedEvent.SourceLogPosition.EventPosition;

                // Maintain a sliding window of recent events for buffer dumps
                // on sequence errors. This is best-effort diagnostics only.
                TrackRecentEvent(proposedEvent);

                if (_ignoreMetadataEventsForPartitioning && proposedEvent.IsMetadata) {
                    if (ReplicationDebugOptions.DebugPartitionSequences && Log.IsDebugEnabled()) {
                        Log.Debug(
                            "Skipping sequence check for metadata event. Partition={PartitionId}, Key={Key}, EventType={EventType}, EventId={EventId}, Position=C:{EventNumber}/P:{EventPosition}, ReplicationMessageId={ReplicationMessageId}",
                            _index,
                            proposedEvent.EventDetails.Stream,
                            proposedEvent.EventDetails.EventType,
                            proposedEvent.EventDetails.EventId,
                            proposedEvent.SourceLogPosition.EventNumber,
                            proposedEvent.SourceLogPosition.EventPosition,
                            proposedEvent.ReplicationMessageId
                        );
                    }

                    await pipe.Send(context);

                    // Do not advance _writeSequence when metadata events are ignored
                    // for partitioning; they should not affect domain event ordering.
                    continue;
                }
                
                if (proposedSequence < _writeSequence)
                    Log.Warn(
                        "Wrong sequence for {EventType}. Partition={PartitionId}, EventId={EventId}, Position=C:{EventNumber}/P:{EventPosition}, ProposedSequence={ProposedSequence}, WriteSequence={WriteSequence}, Stream={Stream}, IsMetadata={IsMetadata}, ReplicationMessageId={ReplicationMessageId}",
                        proposedEvent.EventDetails.EventType,
                        _index,
                        proposedEvent.EventDetails.EventId,
                        proposedEvent.SourceLogPosition.EventNumber,
                        proposedEvent.SourceLogPosition.EventPosition,
                        proposedSequence,
                        _writeSequence,
                        proposedEvent.EventDetails.Stream,
                        proposedEvent.IsMetadata,
                        proposedEvent.ReplicationMessageId
                    );
                
                if (proposedSequence < _writeSequence && ReplicationDebugOptions.DebugPartitionSequences && Log.IsDebugEnabled()) {
                    // 5.1. Log the current buffer content (best-effort dump of
                    // recent events in this partition).
                    Log.Debug(
                        "Partition buffer dump on sequence error. Partition={PartitionId}, Key={Key}, Count={Count}, Items={Items}",
                        _index,
                        proposedEvent.EventDetails.Stream,
                        _recentEvents.Count,
                        _recentEvents
                            .Select(x => new {
                                x.SequenceNumber,
                                x.EventDetails.EventType,
                                x.EventDetails.EventId,
                                x.IsMetadata,
                                Position = $"C:{x.SourceLogPosition.EventNumber}/P:{x.SourceLogPosition.EventPosition}",
                                x.ReplicationMessageId
                            })
                            .ToArray()
                    );

                    // 5.2. Log previous event that advanced writeSequence so
                    // we can see the exact boundary between "good" and "bad"
                    // ordering.
                    if (_lastWrittenEvent != null) {
                        Log.Debug(
                            "Last written before error. Partition={PartitionId}, Key={Key}, EventType={EventType}, EventId={EventId}, Position=C:{EventNumber}/P:{EventPosition}, WriteSequence={WriteSequence}, IsMetadata={IsMetadata}, ReplicationMessageId={ReplicationMessageId}",
                            _index,
                            _lastWrittenEvent.EventDetails.Stream,
                            _lastWrittenEvent.EventDetails.EventType,
                            _lastWrittenEvent.EventDetails.EventId,
                            _lastWrittenEvent.SourceLogPosition.EventNumber,
                            _lastWrittenEvent.SourceLogPosition.EventPosition,
                            _writeSequence,
                            _lastWrittenEvent.IsMetadata,
                            _lastWrittenEvent.ReplicationMessageId
                        );
                    }
                }
                
                if (ReplicationDebugOptions.DebugPartitionSequences && Log.IsDebugEnabled()) {
                    Log.Debug(
                        "Sequence transition in partition {PartitionId}: {PreviousSequence} -> {NewSequence} (delta={Delta}), EventType={EventType}, Stream={Stream}, Position=C:{EventNumber}/P:{EventPosition}, IsMetadata={IsMetadata}, ReplicationMessageId={ReplicationMessageId}",
                        _index,
                        _writeSequence,
                        proposedSequence,
                        proposedSequence - _writeSequence,
                        proposedEvent.EventDetails.EventType,
                        proposedEvent.EventDetails.Stream,
                        proposedEvent.SourceLogPosition.EventNumber,
                        proposedEvent.SourceLogPosition.EventPosition,
                        proposedEvent.IsMetadata,
                        proposedEvent.ReplicationMessageId
                    );
                }
                
                _writeSequence = proposedSequence;
                        
                await pipe.Send(context);
                
                // Track the last successfully written event per partition so
                // we can log it when a future sequence error happens.
                _lastWrittenEvent = proposedEvent;

                if (ReplicationDebugOptions.DebugPartitionSequences && Log.IsDebugEnabled()) {
                    Log.Debug(
                        "Seq OK. Partition={PartitionId}, Key={Key}, EventType={EventType}, EventId={EventId}, Position=C:{EventNumber}/P:{EventPosition}, ProposedSequence={ProposedSequence}, WriteSequence={WriteSequence}, IsMetadata={IsMetadata}, ReplicationMessageId={ReplicationMessageId}",
                        _index,
                        proposedEvent.EventDetails.Stream,
                        proposedEvent.EventDetails.EventType,
                        proposedEvent.EventDetails.EventId,
                        proposedEvent.SourceLogPosition.EventNumber,
                        proposedEvent.SourceLogPosition.EventPosition,
                        proposedSequence,
                        _writeSequence,
                        proposedEvent.IsMetadata,
                        proposedEvent.ReplicationMessageId
                    );
                }
            }
            catch (OperationCanceledException) {
                if (!IsStopping) throw;
            }
        }
    }

    void TrackRecentEvent(BaseProposedEvent proposedEvent) {
        _recentEvents.Enqueue(proposedEvent);

        while (_recentEvents.Count > RecentEventsLimit) {
            _recentEvents.Dequeue();
        }
    }

    protected override async Task StopAgent(StopContext context) {
        await _reader.ConfigureAwait(false);
        await base.StopAgent(context).ConfigureAwait(false);
    }

    public void Probe(ProbeContext context) => context.CreateScope($"partition-{_index}");
}

record DelayedOperation(SinkContext? Context, IPipe<SinkContext>? Pipe);