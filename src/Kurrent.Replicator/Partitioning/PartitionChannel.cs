using System.Threading.Channels;
using Kurrent.Replicator.Shared.Logging;
using GreenPipes;
using GreenPipes.Agents;
using Kurrent.Replicator.Sink;

namespace Kurrent.Replicator.Partitioning; 

public class PartitionChannel : Agent {
    readonly int                             _index;
    readonly Task                            _reader;
    readonly ChannelWriter<DelayedOperation> _writer;

    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    long _writeSequence;

    public PartitionChannel(int index) {
        _index = index;
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

                if (context.ProposedEvent.SequenceNumber < _writeSequence)
                    Log.Warn("Wrong sequence for {Type}", context.ProposedEvent.EventDetails.EventType);
                _writeSequence = context.ProposedEvent.SequenceNumber;
                        
                await pipe.Send(context);
            }
            catch (OperationCanceledException) {
                if (!IsStopping) throw;
            }
        }
    }

    protected override async Task StopAgent(StopContext context) {
        await _reader.ConfigureAwait(false);
        await base.StopAgent(context).ConfigureAwait(false);
    }

    public void Probe(ProbeContext context) => context.CreateScope($"partition-{_index}");
}

record DelayedOperation(SinkContext? Context, IPipe<SinkContext>? Pipe);