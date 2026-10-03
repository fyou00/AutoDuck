using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoDuck.Helper;

/// <summary>
/// Protocol (JSON, satu message per frame teks):
///   helper -> client : {"type":"hello","version":1}
///                      {"type":"audio_state","active":true,"process":"chrome.exe",
///                       "processes":["chrome.exe"],"sessions":[{"process":"chrome.exe","pid":1234}],"ts":...}
///                      {"type":"audio_state","active":false,"processes":[],"sessions":[],"ts":...}
///                      {"type":"pong"}
///   client -> helper : {"type":"get_state"} | {"type":"ping"}
/// </summary>
public sealed class WebSocketServer
{
    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        public SemaphoreSlim Lock { get; } = new(1, 1);   // WebSocket tidak boleh SendAsync paralel
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly AudioSessionMonitor _monitor;

    public WebSocketServer(AudioSessionMonitor monitor)
    {
        _monitor = monitor;
        _monitor.StateChanged += state => _ = BroadcastAsync(Serialize(state));
    }

    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 426;
            await ctx.Response.WriteAsync("AutoDuck.Helper: hubungkan memakai WebSocket.");
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var client = new Client { Socket = socket };
        var id = Guid.NewGuid();
        _clients[id] = client;
        Logger.Info($"Client terhubung ({_clients.Count} aktif)");

        try
        {
            await SendAsync(client, JsonSerializer.Serialize(new { type = "hello", version = 1 }, Json));
            await SendAsync(client, Serialize(_monitor.Current));

            var buffer = new byte[2048];
            while (socket.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) continue;

                string? type = null;
                try
                {
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                    if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString();
                }
                catch (JsonException) { continue; }

                switch (type)
                {
                    case "ping": await SendAsync(client, "{\"type\":\"pong\"}"); break;
                    case "get_state": await SendAsync(client, Serialize(_monitor.Current)); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            Logger.Info($"Client terputus ({_clients.Count} aktif)");
        }
    }

    private static string Serialize(AudioState s) => JsonSerializer.Serialize(new
    {
        type = "audio_state",
        active = s.Active,
        process = s.Process,
        processes = s.Processes,
        sessions = s.Sessions.Select(x => new { process = x.Process, pid = x.Pid }),
        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    }, Json);

    private async Task BroadcastAsync(string message)
    {
        foreach (var client in _clients.Values) await SendAsync(client, message);
    }

    private static async Task SendAsync(Client c, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await c.Lock.WaitAsync();
        try
        {
            if (c.Socket.State == WebSocketState.Open)
                await c.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* client putus; dibersihkan oleh loop receive */ }
        finally { c.Lock.Release(); }
    }
}
