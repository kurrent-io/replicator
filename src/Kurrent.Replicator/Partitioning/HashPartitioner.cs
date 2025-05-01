using GreenPipes;
using GreenPipes.Agents;
using GreenPipes.Partitioning;

namespace Kurrent.Replicator.Partitioning;

public class HashPartitioner : Supervisor, IPartitioner {
    readonly IHashGenerator     _hashGenerator;
    readonly string             _id= Guid.NewGuid().ToString("N");
    readonly int                _partitionCount;
    readonly PartitionChannel[] _partitions;

    public HashPartitioner(int partitionCount, IHashGenerator hashGenerator) {
        _partitionCount = partitionCount;
        _hashGenerator  = hashGenerator;

        _partitions = Enumerable.Range(0, partitionCount)
            .Select(index => new PartitionChannel(index))
            .ToArray();
    }

    IPartitioner<T> IPartitioner.GetPartitioner<T>(PartitionKeyProvider<T> keyProvider)
        => new ContextPartitioner<T>(this, keyProvider);

    public void Probe(ProbeContext context) {
        var scope = context.CreateScope("partitioner");
        scope.Add("id", _id);
        scope.Add("partitionCount", _partitionCount);

        foreach (var t in _partitions)
            t.Probe(scope);
    }

    Task Send<T>(byte[] key, T context, IPipe<T> next) where T : class, PipeContext {
        var hash = key.Length > 0 ? _hashGenerator.Hash(key) : 0;

        var partitionId = hash % _partitionCount;

        return _partitions[partitionId].Send(context, next);
    }

    class ContextPartitioner<TContext>(HashPartitioner partitioner, PartitionKeyProvider<TContext> keyProvider)
        : IPartitioner<TContext> where TContext : class, PipeContext {
        public Task Send(TContext context, IPipe<TContext> next) {
            var key = keyProvider(context);

            if (key == null)
                throw new InvalidOperationException("The key cannot be null");

            return partitioner.Send(key, context, next);
        }

        public void Probe(ProbeContext context) => partitioner.Probe(context);

        Task IAgent.Ready     => partitioner.Ready;
        Task IAgent.Completed => partitioner.Completed;

        CancellationToken IAgent.Stopping => partitioner.Stopping;
        CancellationToken IAgent.Stopped  => partitioner.Stopped;

        Task IAgent.Stop(StopContext context) => partitioner.Stop(context);

        int ISupervisor. PeakActiveCount => partitioner.PeakActiveCount;
        long ISupervisor.TotalCount      => partitioner.TotalCount;

        void ISupervisor.Add(IAgent agent) => partitioner.Add(agent);
    }
}
