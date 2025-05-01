using Kurrent.Replicator.Shared.Observe;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.InMemory;
using Ubiquitous.Metrics.Labels;
using Ubiquitous.Metrics.Prometheus;

namespace replicator;

public static class Measurements {
    static readonly InMemoryMetricsProvider InMemory = new();

    public static void ConfigureMetrics(string environment) {
        var metrics = Metrics.CreateUsing(
            new PrometheusConfigurator(new Label("app", "replicator"), new Label("environment", environment)),
            InMemory
        );
        ReplicationMetrics.Configure(metrics);
    }

    public static InMemoryGauge GetGauge(string name) => InMemory.GetGauge(name)!;

    public static InMemoryHistogram GetHistogram(string name) => InMemory.GetHistogram(name)!;
}
