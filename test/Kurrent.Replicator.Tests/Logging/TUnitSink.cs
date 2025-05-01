using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace Kurrent.Replicator.Tests.Logging;

public class TestOutputSink(ITextFormatter textFormatter) : ILogEventSink {
    private readonly ITextFormatter _textFormatter = textFormatter ?? throw new ArgumentNullException(nameof(textFormatter));

    public void Emit(LogEvent logEvent) {
        ArgumentNullException.ThrowIfNull(logEvent);

        var renderSpace = new StringWriter();
        _textFormatter.Format(logEvent, renderSpace);
        var message = renderSpace.ToString().Trim();
        TestContext.Current?.OutputWriter.WriteLine(message);
    }
}
