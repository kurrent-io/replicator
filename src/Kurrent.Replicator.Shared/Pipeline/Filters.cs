using System.Text.RegularExpressions;
using Kurrent.Replicator.Shared.Extensions;
using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.Shared.Pipeline;

public delegate ValueTask<bool> FilterEvent(BaseOriginalEvent originalEvent);

public static class Filters {
    public static async ValueTask<bool> CombinedFilter(BaseOriginalEvent originalEvent, params FilterEvent[] filters) {
        foreach (var filter in filters) {
            if (!await filter(originalEvent)) return false;
        }

        return true;
    }

    public static ValueTask<bool> EmptyFilter(BaseOriginalEvent originalEvent) => new(true);

    public abstract class RegExFilter(string? include, string? exclude, Func<BaseOriginalEvent, string> getProp) {
        readonly Regex? _include = include != null ? new Regex(include) : null;
        readonly Regex? _exclude = exclude != null ? new Regex(exclude) : null;

        public ValueTask<bool> Filter(BaseOriginalEvent originalEvent) {
            var propValue = getProp(originalEvent);
            var pass      = _include.IsNullOrMatch(propValue) && _exclude.IsNullOrDoesntMatch(propValue);

            return new(pass);
        }
    }

    public class EventTypeFilter(string? include, string? exclude) : RegExFilter(include, exclude, x => x.EventDetails.EventType);

    public class StreamNameFilter(string? include, string? exclude) : RegExFilter(include, exclude, x => x.EventDetails.Stream);

    public static ValueTask<bool> EmptyDataFilter(BaseOriginalEvent originalEvent)
        => new(originalEvent is OriginalEvent { Data.Length: > 0 });
}
