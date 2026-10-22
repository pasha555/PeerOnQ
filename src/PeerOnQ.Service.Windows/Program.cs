using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PeerOnQ.Service.Windows;
using PeerOnQ.Shared.Contracts;

// Only SCM starts the installed broker. No interactive elevation or service self-installation.
if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService()) return 2;
using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
using var configuration = machine.OpenSubKey(WindowsSessionBrokerProtocol.RegistryKey);
if (configuration?.GetValue("HostExecutablePath") is not string hostPath ||
    !Path.IsPathFullyQualified(hostPath) || !string.Equals(Path.GetFileName(hostPath), "PeerOnQ.exe", StringComparison.OrdinalIgnoreCase)) return 3;
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService(options => options.ServiceName = WindowsSessionBrokerProtocol.ServiceName);
// LocalService cannot create EventLog sources. SCM records start/stop/failure; no user logs or
// credentials are written in session 0. The per-user host retains its existing diagnostics.
builder.Logging.ClearProviders();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton(new SessionLeaseRegistry(Path.GetFullPath(hostPath)));
builder.Services.AddHostedService<SessionBrokerWorker>();
await builder.Build().RunAsync();
return Environment.ExitCode;
