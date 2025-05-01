using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.Partitioning; 

public static class KeyProvider {
    public static string ByStreamName(BaseProposedEvent evt) => evt.EventDetails.Stream;
}