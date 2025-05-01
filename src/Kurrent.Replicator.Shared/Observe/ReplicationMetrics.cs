using System.Diagnostics;
using Ubiquitous.Metrics;

namespace Kurrent.Replicator.Shared.Observe; 

public static class ReplicationMetrics {
    public const string PrepareChannelSizeName      = "prepare_size";
    public const string SinkChannelSizeName         = "sink_size";
    public const string ReadsHistogramName          = "event_reads";
    public const string WritesHistogramName         = "event_writes";
    public const string PrepareHistogramName        = "event_prepares";
    public const string ReadingPositionGaugeName    = "read_position";
    public const string ProcessedPositionGaugeName  = "processed_position";
    public const string LastSourcePositionGaugeName = "last_known_position";
    public const string SinkPositionGaugeName       = "sink_position";

    public static void Configure(Metrics metrics) {
        PrepareChannelSize = metrics.CreateGauge(PrepareChannelSizeName, "Prepare channel size");
        SinkChannelSize    = metrics.CreateGauge(SinkChannelSizeName, "Sink channel size");
        ReadingPosition    = metrics.CreateGauge(ReadingPositionGaugeName, "Read position");
        ProcessedPosition  = metrics.CreateGauge(ProcessedPositionGaugeName, "Processed position");
        ReadsHistogram     = metrics.CreateHistogram(ReadsHistogramName, "Event reads, seconds");
        MetaReadsHistogram = metrics.CreateHistogram("metadata_reads", "Stream meta reads, seconds");
        WritesHistogram    = metrics.CreateHistogram(WritesHistogramName, "Event writes, seconds");
        PrepareHistogram   = metrics.CreateHistogram(PrepareHistogramName, "Event prepares, seconds");
        ReadErrorsCount    = metrics.CreateCount("read_errors", "Reader errors count");
        WriteErrorsCount   = metrics.CreateCount("write_errors", "Sink errors count");
        LastSourcePosition = metrics.CreateGauge(LastSourcePositionGaugeName, "Last known source position");
        WriterPosition     = metrics.CreateGauge(SinkPositionGaugeName, "Sink writer position");
    }

    public static IGaugeMetric     PrepareChannelSize { get; private set; } = null!;
    public static IGaugeMetric     SinkChannelSize    { get; private set; } = null!;
    public static IGaugeMetric     ReadingPosition    { get; private set; } = null!;
    public static IGaugeMetric     ProcessedPosition  { get; private set; } = null!;
    public static IHistogramMetric MetaReadsHistogram { get; private set; } = null!;
    public static IHistogramMetric ReadsHistogram     { get; private set; } = null!;
    public static IHistogramMetric WritesHistogram    { get; private set; } = null!;
    public static ICountMetric     ReadErrorsCount    { get; private set; } = null!;
    public static ICountMetric     WriteErrorsCount   { get; private set; } = null!;
    public static IGaugeMetric     LastSourcePosition { get; private set; } = null!;
    public static IGaugeMetric     WriterPosition     { get; private set; } = null!;
    public static IHistogramMetric PrepareHistogram   { get; private set; } = null!;

    public static int PrepareChannelCapacity { get; private set; }
    public static int SinkChannelCapacity    { get; private set; }

    public static async Task<T?> Measure<T>(
        Func<Task<T>>    action,
        IHistogramMetric metric,
        Func<T, int>     getCount,
        ICountMetric?    errorCount = null,
        string[]?        labels     = null
    ) where T : class {
        var stopwatch = Stopwatch.StartNew();

        T? result = null;

        try {
            result = await action().ConfigureAwait(false);
        }
        catch (Exception) {
            errorCount?.Inc(labels);

            throw;
        }
        finally {
            stopwatch.Stop();
            var count = result != null ? getCount(result) : 0;
            metric.Observe(stopwatch, labels, count);
        }

        return result;
    }

    public static void SetCapacity(int prepareCapacity, int sinkCapacity) {
        PrepareChannelCapacity = prepareCapacity;
        SinkChannelCapacity    = sinkCapacity;
    }
}