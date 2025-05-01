using Ubiquitous.Metrics.InMemory;
using static replicator.Measurements;
using static Kurrent.Replicator.Shared.Observe.ReplicationMetrics;

namespace replicator.HttpApi; 

public class CountersKeep {
    public InMemoryGauge     SinkChannelGauge       { get; } = GetGauge(SinkChannelSizeName);
    public InMemoryGauge     SourcePositionGauge    { get; } = GetGauge(LastSourcePositionGaugeName);
    public InMemoryGauge     ProcessedPositionGauge { get; } = GetGauge(ProcessedPositionGaugeName);
    public InMemoryGauge     SinkPositionGauge      { get; } = GetGauge(SinkPositionGaugeName);
    public InMemoryGauge     ReadPositionGauge      { get; } = GetGauge(ReadingPositionGaugeName);
    public InMemoryHistogram PrepareHistogram       { get; } = GetHistogram(PrepareHistogramName);
    public InMemoryHistogram SinkHistogram          { get; } = GetHistogram(WritesHistogramName);
    public InMemoryHistogram ReadsHistogram         { get; } = GetHistogram(ReadsHistogramName);
    public InMemoryGauge     PrepareChannelGauge    { get; } = GetGauge(PrepareChannelSizeName);
}