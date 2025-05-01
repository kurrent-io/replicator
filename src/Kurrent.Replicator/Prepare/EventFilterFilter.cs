using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Shared.Pipeline;
using GreenPipes;
using Ubiquitous.Metrics;

namespace Kurrent.Replicator.Prepare;

public class EventFilterFilter(FilterEvent filter) : IFilter<PrepareContext> {
    public async Task Send(PrepareContext context, IPipe<PrepareContext> next) {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (context.OriginalEvent == null) return;

        var accept = await Metrics
            .MeasureValueTask(() => filter(context.OriginalEvent), ReplicationMetrics.PrepareHistogram)
            .ConfigureAwait(false);

        if (!accept) context.IgnoreEvent();
        await next.Send(context).ConfigureAwait(false);
    }

    public void Probe(ProbeContext context) { }
}

public class EventFilterSpecification(FilterEvent? filter) : IPipeSpecification<PrepareContext> {
    public void Apply(IPipeBuilder<PrepareContext> builder) => builder.AddFilter(new EventFilterFilter(filter!));

    public IEnumerable<ValidationResult> Validate() {
        if (filter == null) {
            yield return this.Failure("validationFilterPipe", "Event filter is missing");
        }
    }
}

public static class EventFilterPipeExtensions {
    public static void UseEventFilter(this IPipeConfigurator<PrepareContext> configurator, FilterEvent filter)
        => configurator.AddPipeSpecification(new EventFilterSpecification(filter));
}
