using System.Net;
using System.Runtime.InteropServices;
using AutoDuck.Helper;

// Usage:
//   AutoDuck.Helper.exe              -> run the helper (console)
//   AutoDuck.Helper.exe --list       -> Phase 1: list audio sessions, then exit
//   AutoDuck.Helper.exe --verbose    -> log every session state change (including Spotify)
//   AutoDuck.Helper.exe --background -> detach the console (used at Windows startup)

Logger.Init(args.Contains("--verbose"));
var config = Configuration.Load();

if (args.Contains("--list"))
{
    using var probe = new AudioSessionMonitor(config);
    probe.Start();
    Logger.Info("Current audio sessions:");
    probe.Dump();
    return 0;
}

using var single = new Mutex(true, @"Local\AutoDuck.Helper", out var created);
if (!created)
{
    Logger.Warn("AutoDuck.Helper is already running. Exiting.");
    return 1;
}

if (args.Contains("--background")) FreeConsole();

using var monitor = new AudioSessionMonitor(config);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
// IPv4 loopback only -> unreachable from the network, no admin rights needed.
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, config.Port));
var app = builder.Build();

var server = new WebSocketServer(monitor);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Run(server.HandleAsync);

monitor.Start();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; app.Lifetime.StopApplication(); };

Logger.Info($"AutoDuck.Helper listening on ws://127.0.0.1:{config.Port}  (log: %LOCALAPPDATA%\\AutoDuck\\helper.log)");
try
{
    await app.RunAsync();
}
catch (IOException ex)
{
    Logger.Error($"Cannot listen on port {config.Port} (maybe another program is using it): {ex.Message}");
    return 2;
}
return 0;

[DllImport("kernel32.dll")]
static extern bool FreeConsole();
