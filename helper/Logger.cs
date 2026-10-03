namespace AutoDuck.Helper;

public static class Logger
{
    private static readonly object Gate = new();
    private static string? _file;
    private static bool _verbose;

    public static void Init(bool verbose)
    {
        _verbose = verbose;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoDuck");
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir, "helper.log");
            if (File.Exists(_file) && new FileInfo(_file).Length > 1_000_000) File.Delete(_file);
        }
        catch { _file = null; }
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) { if (_verbose) Write("DEBUG", msg); }

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { /* console bisa sudah dilepas (--background) */ }
            if (_file != null) { try { File.AppendAllText(_file, line + Environment.NewLine); } catch { } }
        }
    }
}
