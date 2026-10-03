using System.Text.Json;

namespace AutoDuck.Helper;

public sealed class Configuration
{
    public int Port { get; set; } = 8765;
    public bool UsePeakMeter { get; set; } = false;
    public float PeakThreshold { get; set; } = 0.001f;
    public int PeakHoldMs { get; set; } = 1500;
    public int SafetyRescanSeconds { get; set; } = 10;
    public List<string> IgnoredProcesses { get; set; } = new() { "spotify.exe", "autoduck.helper.exe" };

    public bool IsIgnored(string processName) =>
        IgnoredProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

    public static Configuration Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        try
        {
            if (File.Exists(path))
            {
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var cfg = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), opts) ?? new Configuration();
                if (cfg.Port is < 1024 or > 65535) cfg.Port = 8765;
                return cfg;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read config.json, using defaults: {ex.Message}");
        }
        return new Configuration();
    }
}
