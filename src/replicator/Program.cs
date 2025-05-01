using System.Diagnostics;
using System.Reflection;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Kurrent.Replicator;
using replicator;
using replicator.Settings;

var builder = WebApplication.CreateBuilder(args);

var isDebug   = Environment.GetEnvironmentVariable("REPLICATOR_DEBUG") != null;
var logConfig = new LoggerConfiguration();
logConfig = isDebug ? logConfig.MinimumLevel.Debug() : logConfig.MinimumLevel.Information();

logConfig = logConfig
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Grpc", LogEventLevel.Error)
    .Enrich.FromLogContext();

logConfig = builder.Environment.IsDevelopment()
    ? logConfig.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} <s:{SourceContext}>;{NewLine}{Exception}")
    : logConfig.WriteTo.Console(new RenderedCompactJsonFormatter());
Log.Logger = logConfig.CreateLogger();
var fileInfo = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
Log.Information("Starting replicator {Version}", fileInfo.ProductVersion);

builder.Host.UseSerilog();
builder.Configuration.AddYamlFile("./config/appsettings.yaml", false, true).AndEnvConfig();
Startup.ConfigureServices(builder);

var app = builder.Build();
Startup.Configure(app);

var restartOnFailure = app.Services.GetService<ReplicatorOptions>()?.RestartOnFailure == true;

try {
    app.Run();

    return 0;
} catch (Exception ex) {
    Log.Fatal(ex, "Host terminated unexpectedly");

    if (restartOnFailure) return -1;

    while (true) {
        await Task.Delay(5000);
    }
} finally {
    Log.CloseAndFlush();
}
