using Kurrent.Replicator.Shared.Contracts;
using GreenPipes;

namespace Kurrent.Replicator.Prepare;

public class PrepareContext : BasePipeContext, PipeContext, IEventDetailsContext {
    public PrepareContext(BaseOriginalEvent originalEvent, CancellationToken cancellationToken) 
        : base( cancellationToken )
        => OriginalEvent = originalEvent;

    public BaseOriginalEvent OriginalEvent { get; private set; }

    public void IgnoreEvent()
        => OriginalEvent = new IgnoredOriginalEvent(
            OriginalEvent.Created,
            OriginalEvent.EventDetails,
            OriginalEvent.LogPosition,
            OriginalEvent.SequenceNumber,
            OriginalEvent.TracingMetadata
        );

    public EventDetails EventDetails => OriginalEvent.EventDetails;
}