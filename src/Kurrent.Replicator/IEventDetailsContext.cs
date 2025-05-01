using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator; 

public interface IEventDetailsContext {
    public EventDetails EventDetails { get; }
}