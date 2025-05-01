// Copyright (C) Eventuous HQ OÜ.All rights reserved
// Licensed under the Apache License, Version 2.0.

using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Kurrent.Replicator.Tests.Logging;

public static class SerilogExtensions {
    const string DefaultConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static LoggerConfiguration TestOutput(
            this LoggerSinkConfiguration sinkConfiguration,
            LogEventLevel                restrictedToMinimumLevel = LevelAlias.Minimum,
            string                       outputTemplate           = DefaultConsoleOutputTemplate,
            IFormatProvider              formatProvider           = null,
            LoggingLevelSwitch           levelSwitch              = null
        ) {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);

        var formatter = new MessageTemplateTextFormatter(outputTemplate, formatProvider);

        return sinkConfiguration.Sink(new TestOutputSink(formatter), restrictedToMinimumLevel, levelSwitch);
    }
}
