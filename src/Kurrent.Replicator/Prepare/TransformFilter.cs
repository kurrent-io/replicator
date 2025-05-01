using System.Diagnostics;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Shared.Pipeline;
using GreenPipes;
using Ubiquitous.Metrics;

namespace Kurrent.Replicator.Prepare;

public class TransformFilter(TransformEvent transform) : IFilter<PrepareContext> {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    public async Task Send(PrepareContext context, IPipe<PrepareContext> next) {
        var transformed = context.OriginalEvent is OriginalEvent oe
            ? await Metrics.Measure(() => Transform(oe), ReplicationMetrics.PrepareHistogram)
                .ConfigureAwait(false)
            : TransformMeta(context.OriginalEvent);

        if (transformed is NoEvent)
            return;

        context.AddOrUpdatePayload(() => transformed, _ => transformed);

        await next.Send(context).ConfigureAwait(false);

        return;

        async Task<BaseProposedEvent> Transform(OriginalEvent originalEvent) {
            using var activity = new Activity("transform");

            activity.SetParentId(
                context.OriginalEvent.TracingMetadata.TraceId,
                context.OriginalEvent.TracingMetadata.SpanId
            );
            activity.Start();

            try {
                var res = await transform(originalEvent, context.CancellationToken).ConfigureAwait(false);
                activity.SetStatus(ActivityStatusCode.Ok);

                return res;
            } catch (Exception e) {
                activity.SetStatus(ActivityStatusCode.Error, e.Message);

                Log.Error(
                    e,
                    "Failed to transform event from stream {Stream} of type {EventType}",
                    context.OriginalEvent.EventDetails.Stream,
                    context.OriginalEvent.EventDetails.EventType
                );

                throw;
            }
        }
    }

    public void Probe(ProbeContext context) => context.Add("eventTransform", transform);

    static BaseProposedEvent TransformMeta(BaseOriginalEvent originalEvent)
        => originalEvent switch {
            StreamDeletedOriginalEvent deleted =>
                new ProposedDeleteStream(
                    deleted.EventDetails,
                    deleted.LogPosition,
                    deleted.SequenceNumber
                ),
            StreamMetadataOriginalEvent meta =>
                new ProposedMetaEvent(
                    meta.EventDetails,
                    meta.Data,
                    meta.LogPosition,
                    meta.SequenceNumber
                ),
            IgnoredOriginalEvent ignored => new IgnoredEvent(
                ignored.EventDetails,
                ignored.LogPosition,
                ignored.SequenceNumber
            ),
            _ => throw new InvalidOperationException("Unknown original event type")
        };
}

public class TransformSpecification(TransformEvent transform) : IPipeSpecification<PrepareContext> {
    public void Apply(IPipeBuilder<PrepareContext> builder)
        => builder.AddFilter(new TransformFilter(transform));

    public IEnumerable<ValidationResult> Validate() {
        yield return this.Success("filter");
    }
}

public static class TransformPipeExtensions {
    public static void UseEventTransform(this IPipeConfigurator<PrepareContext> configurator, TransformEvent transformEvent)
        => configurator.AddPipeSpecification(new TransformSpecification(transformEvent));
}
