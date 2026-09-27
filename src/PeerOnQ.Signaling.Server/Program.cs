using PeerOnQ.Signaling.Server;
using Serilog;

// Structured audit logging for the control plane. Device IDs are logged masked by the
// handler; no secret, token or media ever reaches these logs.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.WithProperty("Application", "PeerOnQ.Signaling")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/signaling-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var app = SignalingApp.Create(args);
    app.Logger.LogInformation("PeerOnQ signaling server starting");
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Signaling server terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
