using System.Collections.Concurrent;
using Kurrent.Replicator.Tests.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Kurrent.Replicator.Tests.Auth.Support;

/// <summary>
/// Captures everything logged through Serilog's global logger while alive. Tests using it must be
/// [NotInParallel("global-logger")] because Log.Logger is process-global.
/// </summary>
public sealed class LogCapture : ILogEventSink, IDisposable {
    readonly ConcurrentQueue<LogEvent> _events = new();
    readonly ILogger                   _previous = Log.Logger;

    public LogCapture() {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(this)
            .WriteTo.TestOutput()
            .CreateLogger();
    }

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public IReadOnlyList<(LogEventLevel Level, string Text)> Events
        => _events.Select(e => (e.Level, e.RenderMessage() + " " + e.Exception)).ToList();

    public IEnumerable<string> TextAtOrAbove(LogEventLevel level) => Events.Where(e => e.Level >= level).Select(e => e.Text);

    public string AllText => string.Join("\n", Events.Select(e => e.Text));

    public void Dispose() => Log.Logger = _previous;
}
