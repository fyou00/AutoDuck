using System.Net;
using System.Runtime.InteropServices;
using AutoDuck.Helper;

// Penggunaan:
//   AutoDuck.Helper.exe              -> jalankan helper (console)
//   AutoDuck.Helper.exe --list       -> Phase 1: tampilkan sesi audio lalu keluar
//   AutoDuck.Helper.exe --verbose    -> log semua perubahan state sesi (termasuk Spotify)
//   AutoDuck.Helper.exe --background -> lepas console (dipakai saat startup Windows)

Logger.Init(args.Contains("--verbose"));
var config = Configuration.Load();

if (args.Contains("--list"))
{
    using var probe = new AudioSessionMonitor(config);
    probe.Start();
    Logger.Info("Daftar sesi audio saat ini:");
    probe.Dump();
    return 0;
}

using var single = new Mutex(true, @"Local\AutoDuck.Helper", out var created);
if (!created)
{
    Logger.Warn("AutoDuck.Helper sudah berjalan. Keluar.");
    return 1;
}

if (args.Contains("--background")) FreeConsole();

using var monitor = new AudioSessionMonitor(config);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
// Hanya loopback IPv4 -> tidak terjangkau dari jaringan, tidak butuh admin.
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, config.Port));
var app = builder.Build();

var server = new WebSocketServer(monitor);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Run(server.HandleAsync);

monitor.Start();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; app.Lifetime.StopApplication(); };

Logger.Info($"AutoDuck.Helper aktif di ws://127.0.0.1:{config.Port}  (log: %LOCALAPPDATA%\\AutoDuck\\helper.log)");
try
{
    await app.RunAsync();
}
catch (IOException ex)
{
    Logger.Error($"Tidak bisa listen di port {config.Port} (mungkin dipakai program lain): {ex.Message}");
    return 2;
}
return 0;

[DllImport("kernel32.dll")]
static extern bool FreeConsole();
