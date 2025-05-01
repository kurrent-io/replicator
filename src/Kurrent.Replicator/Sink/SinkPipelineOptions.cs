namespace Kurrent.Replicator.Sink; 

public record SinkPipeOptions(
    int          PartitionCount = 1,
    int          BufferSize     = 1000,
    string?      Partitioner    = null
);