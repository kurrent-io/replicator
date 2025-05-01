using System.Text;
using Kurrent.Replicator.Shared.Logging;
using GreenPipes;
using GreenPipes.Agents;
using GreenPipes.Partitioning;

namespace Kurrent.Replicator.Partitioning; 

public class ValuePartitioner : Supervisor, IPartitioner {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();
        
    readonly string _id = Guid.NewGuid().ToString("N");

    readonly Dictionary<string, PartitionChannel> _partitions = new();

    int _partitionsCount;

    IPartitioner<T> IPartitioner.GetPartitioner<T>(PartitionKeyProvider<T> keyProvider)
        => new ContextPartitioner<T>(this, keyProvider);

    public void Probe(ProbeContext context) {
        var scope = context.CreateScope("partitioner");
        scope.Add("id", _id);

        foreach (var t in _partitions.Values)
            t.Probe(scope);
    }

    async Task Send<T>(string key, T context, IPipe<T> next) where T : class, PipeContext {
        var partitionKey = key.StartsWith("$") ? "$system" : key;
        if (!_partitions.ContainsKey(partitionKey)) {
            Log.Info("Adding new partition {Partition}", partitionKey);
            _partitions[partitionKey] = new PartitionChannel(_partitionsCount++);
        }
        await _partitions[partitionKey].Send(context, next);
    }

    class ContextPartitioner<TContext>(ValuePartitioner partitioner, PartitionKeyProvider<TContext> keyProvider) 
        : IPartitioner<TContext> where TContext : class, PipeContext {
        public Task Send(TContext context, IPipe<TContext> next) {
            var key = Encoding.UTF8.GetString(keyProvider(context));

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