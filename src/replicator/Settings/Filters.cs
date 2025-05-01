using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Pipeline;

namespace replicator.Settings; 

public static class EventFilters {
    public static FilterEvent? GetFilter(Replicator settings, IEventReader reader) {
        var filters = (settings.Filters ?? Array.Empty<Filter>()).Select(Configure).ToList();
        if (settings.Scavenge) filters.Add(reader.Filter);

        return filters.Count > 1
            ? x => Filters.CombinedFilter(x, filters.ToArray())
            : filters.Count == 0 ? null : filters[0];

        static FilterEvent Configure(Filter cfg) => cfg.Type switch {
            "eventType"  => new Filters.EventTypeFilter(cfg.Include, cfg.Exclude).Filter,
            "streamName" => new Filters.StreamNameFilter(cfg.Include, cfg.Exclude).Filter,
            _            => throw new ArgumentException($"Unknown filter: {cfg.Type}")
        };
    }
}