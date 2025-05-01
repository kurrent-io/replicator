using Kurrent.Replicator.Shared.Pipeline;

namespace Kurrent.Replicator.Prepare; 

public record PreparePipelineOptions(
    FilterEvent?    Filter,
    TransformEvent? Transform,
    int             TransformConcurrencyLevel = 1,
    int             BufferSize                = 1000
);