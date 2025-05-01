using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.Shared; 

public interface IEventReader {
    string Protocol { get; }
        
    Task ReadEvents(LogPosition fromLogPosition, Func<BaseOriginalEvent, ValueTask> next, CancellationToken cancellationToken);

    Task<long?> GetLastPosition(CancellationToken cancellationToken);

    ValueTask<bool> Filter(BaseOriginalEvent originalEvent);
}