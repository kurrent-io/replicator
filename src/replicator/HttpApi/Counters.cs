using Kurrent.Replicator.Shared.Observe;
using Microsoft.AspNetCore.Mvc;
using Ubiquitous.Metrics.InMemory;
using static Kurrent.Replicator.Shared.Observe.ReplicationMetrics;

namespace replicator.HttpApi; 

[Route("api/counters")]
public class Counters(CountersKeep keep) : ControllerBase {
    CountersKeep Keep { get; } = keep;

    [HttpGet]
    public MetricsResponse GetCounters() {
        var prepareCount = (int) Keep.PrepareChannelGauge.Value;
        return new(
            Keep.ReadsHistogram.Count,
            Keep.SinkHistogram.Count,
            (long) Keep.ReadPositionGauge.Value,
            (long) Keep.ProcessedPositionGauge.Value,
            prepareCount,
            ReadRate(),
            GetRate(Keep.SinkHistogram),
            (long) Keep.SourcePositionGauge.Value,
            (long) Keep.SinkPositionGauge.Value,
            ReplicationStatus.ReaderRunning,
            new(PrepareChannelCapacity, prepareCount),
            new(SinkChannelCapacity, (int) Keep.SinkChannelGauge.Value),
            GetRate(Keep.PrepareHistogram)
        );

        static Rate GetRate(InMemoryHistogram metric) => new(metric.Sum, metric.Count);

        Rate ReadRate() => ReplicationStatus.ReaderRunning
            ? new(ReplicationStatus.ElapsedSeconds, Keep.ReadsHistogram.Count)
            : new(0, 0);
    }

    public record MetricsResponse(
        long    ReadEvents,
        long    ProcessedEvents,
        long    ReadPosition,
        long    WritePosition,
        long    InFlightSink,
        Rate    ReadRate,
        Rate    WriteRate,
        long    LastSourcePosition,
        long    SinkPosition,
        bool    ReaderRunning,
        Channel PrepareChannel,
        Channel SinkChannel,
        Rate    PrepareRate
    );

    public record Rate(double Sum, long Count);

    public record Channel(int Capacity, int Size);
}