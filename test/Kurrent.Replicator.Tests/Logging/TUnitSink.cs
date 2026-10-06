using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace Kurrent.Replicator.Tests.Logging;

/// <summary>
/// Writes log events to the current test's output. The pipeline under test logs from many threads at once, and
/// TUnit's per-test output is a plain StringBuilder: its lazily created writer can race on first use (two writers,
/// two locks, one builder), and TUnit reads the builder without a lock when the test ends. Writes are therefore
/// serialised here, and a sink failure never reaches the code that logged.
/// </summary>
public class TestOutputSink(ITextFormatter textFormatter) : ILogEventSink {
    static readonly Lock WriteLock = new();

    readonly ITextFormatter _textFormatter = textFormatter ?? throw new ArgumentNullException(nameof(textFormatter));

    public void Emit(LogEvent logEvent) {
        ArgumentNullException.ThrowIfNull(logEvent);

        var context = TestContext.Current;

        if (context == null) return;

        try {
            var renderSpace = new StringWriter();
            _textFormatter.Format(logEvent, renderSpace);
            var message = renderSpace.ToString().Trim();

            lock (WriteLock) {
                context.OutputWriter.WriteLine(message);
            }
        } catch {
            // test output is best effort
        }
    }
}
