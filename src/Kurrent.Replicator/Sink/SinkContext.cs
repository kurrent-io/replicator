using Kurrent.Replicator.Shared.Contracts;
using GreenPipes;

namespace Kurrent.Replicator.Sink;

public class SinkContext(BaseProposedEvent proposedEvent, CancellationToken cancellationToken) : BasePipeContext(cancellationToken), PipeContext, IEventDetailsContext {
    public BaseProposedEvent ProposedEvent { get; } = proposedEvent;
    public EventDetails      EventDetails  => ProposedEvent.EventDetails;
}
