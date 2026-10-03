using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AutoDuck.Helper;

public sealed record SessionInfo(string Process, uint Pid);

public sealed record AudioState(
    bool Active,
    string? Process,
    IReadOnlyList<string> Processes,
    IReadOnlyList<SessionInfo> Sessions)
{
    public static readonly AudioState Idle =
        new(false, null, Array.Empty<string>(), Array.Empty<SessionInfo>());
}

/// <summary>
/// Memantau semua sesi audio (per aplikasi) di semua render endpoint memakai WASAPI.
/// Event-driven:
///  - IAudioSessionEvents.OnStateChanged   -> sesi Active/Inactive/Expired
///  - IAudioSessionNotification            -> sesi baru dibuat (mis. tab Chrome mulai bunyi)
///  - IMMNotificationClient                -> device ditambah/dicabut/ganti
/// Satu-satunya timer berulang: scan pengaman (default 10 dtk) dan, opsional, peak meter.
/// </summary>
public sealed class AudioSessionMonitor : IMMNotificationClient, IDisposable
{
    private sealed class Tracked : IAudioSessionEventsHandler, IDisposable
    {
        private readonly AudioSessionMonitor _owner;
        public string Key { get; }
        public AudioSessionControl Control { get; }
        public string Process { get; }
        public uint Pid { get; }
        public bool Ignored { get; }
        public volatile AudioSessionState State;
        public long LastAudibleTicks = long.MinValue / 2;

        public Tracked(AudioSessionMonitor owner, string key, AudioSessionControl control,
                       string process, uint pid, bool ignored)
        {
            _owner = owner; Key = key; Control = control; Process = process; Pid = pid; Ignored = ignored;
        }

        public bool IsAudible(long now, int holdMs) => now - LastAudibleTicks <= holdMs;

        // Callback ini berjalan di thread COM: jangan panggil COM lain di sini, cukup jadwalkan Recompute.
        public void OnStateChanged(AudioSessionState state)
        {
            State = state;
            Logger.Debug($"[AudioSession] {Process} -> {Describe(state)}");
            _owner.ScheduleRecompute();
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason reason)
        {
            State = AudioSessionState.AudioSessionStateExpired;
            Logger.Debug($"[AudioSession] {Process} disconnected ({reason})");
            _owner.ScheduleRecompute();
        }

        public void OnVolumeChanged(float volume, bool isMuted) { }
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }

        public void Dispose()
        {
            try { Control.UnRegisterEventClient(this); } catch { }
            try { Control.Dispose(); } catch { }
        }
    }

    private readonly Configuration _cfg;
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly List<MMDevice> _devices = new();
    private readonly Dictionary<string, Tracked> _tracked = new();
    private readonly Timer _recomputeTimer;
    private readonly Timer _rebuildTimer;
    private Timer? _rescanTimer;
    private Timer? _peakTimer;
    private HashSet<string> _lastNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public AudioState Current { get; private set; } = AudioState.Idle;
    public event Action<AudioState>? StateChanged;

    public AudioSessionMonitor(Configuration cfg)
    {
        _cfg = cfg;
        _recomputeTimer = new Timer(_ => Recompute(), null, Timeout.Infinite, Timeout.Infinite);
        _rebuildTimer = new Timer(_ => Rebuild(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        Rebuild();

        if (_cfg.SafetyRescanSeconds > 0)
        {
            var ms = _cfg.SafetyRescanSeconds * 1000;
            _rescanTimer = new Timer(_ => Rescan(), null, ms, ms);
        }
        if (_cfg.UsePeakMeter)
            _peakTimer = new Timer(_ => PollPeaks(), null, 250, 250);
    }

    // ---------- Phase 1: dump sesi ----------
    public void Dump()
    {
        lock (_gate)
        {
            if (_tracked.Count == 0) Logger.Info("(tidak ada sesi audio)");
            foreach (var t in _tracked.Values.OrderBy(t => t.Process, StringComparer.OrdinalIgnoreCase))
                Logger.Info($"[AudioSession] {t.Process} -> {Describe(t.State)}{(t.Ignored ? "  (diabaikan)" : "")}");
        }
    }

    // ---------- Pembangunan ulang (start / device berubah) ----------
    private void Rebuild()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var t in _tracked.Values) t.Dispose();
            _tracked.Clear();
            foreach (var d in _devices) { try { d.Dispose(); } catch { } }
            _devices.Clear();

            try
            {
                foreach (var dev in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    _devices.Add(dev);
                    var devId = dev.ID;
                    var mgr = dev.AudioSessionManager;
                    var sessions = mgr.Sessions;
                    for (int i = 0; i < sessions.Count; i++) TrackLocked(devId, sessions[i]);
                    mgr.OnSessionCreated += (_, raw) => OnSessionCreated(devId, raw);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Gagal enumerasi audio device: {ex.Message}");
            }
        }
        Recompute();
    }

    private void OnSessionCreated(string devId, IAudioSessionControl raw)
    {
        // Jangan lakukan kerja COM di dalam callback notifikasi -> pindah ke thread pool.
        Task.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed) return;
                TrackLocked(devId, new AudioSessionControl(raw));
            }
            Recompute();
        });
    }

    private void TrackLocked(string devId, AudioSessionControl ctl)
    {
        string key;
        try { key = devId + "|" + ctl.GetSessionInstanceIdentifier; }
        catch { ctl.Dispose(); return; }

        if (_tracked.ContainsKey(key)) { ctl.Dispose(); return; }

        var name = ResolveName(ctl, out var pid);
        var ignored = ctl.IsSystemSoundsSession || pid == 0 || _cfg.IsIgnored(name);
        var tracked = new Tracked(this, key, ctl, name, pid, ignored);
        try
        {
            tracked.State = ctl.State;
            ctl.RegisterEventClient(tracked);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Gagal mendaftarkan event untuk {name}: {ex.Message}");
            ctl.Dispose();
            return;
        }
        _tracked[key] = tracked;
        Logger.Debug($"[AudioSession] + {name} (pid {pid}) -> {Describe(tracked.State)}");
    }

    private static string ResolveName(AudioSessionControl ctl, out uint pid)
    {
        pid = 0;
        try
        {
            if (ctl.IsSystemSoundsSession) return "System Sounds";
            pid = ctl.GetProcessID;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant() + ".exe";
        }
        catch
        {
            try
            {
                var dn = ctl.DisplayName;
                if (!string.IsNullOrWhiteSpace(dn)) return dn;
            }
            catch { }
            return $"pid:{pid}";
        }
    }

    // ---------- Jaring pengaman ----------
    // Manager baru dari enumerator baru selalu mengambil daftar sesi terbaru.
    // (AudioSessionManager.RefreshSessions() sengaja tidak dipakai karena melepas notifikasi.)
    private void Rescan()
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var key in _tracked.Where(p => p.Value.State == AudioSessionState.AudioSessionStateExpired)
                                        .Select(p => p.Key).ToList())
            {
                _tracked[key].Dispose();
                _tracked.Remove(key);
                changed = true;
            }
            try
            {
                using var tmp = new MMDeviceEnumerator();
                foreach (var dev in tmp.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var before = _tracked.Count;
                    var devId = dev.ID;
                    var sessions = dev.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++) TrackLocked(devId, sessions[i]);
                    if (_tracked.Count > before) { _devices.Add(dev); changed = true; }
                    else dev.Dispose();
                }
            }
            catch (Exception ex) { Logger.Warn($"Rescan gagal: {ex.Message}"); }
        }
        if (changed) Recompute();
    }

    // ---------- Opsional: peak meter ----------
    private void PollPeaks()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var t in _tracked.Values)
            {
                if (t.Ignored || t.State != AudioSessionState.AudioSessionStateActive) continue;
                try
                {
                    if (t.Control.AudioMeterInformation.MasterPeakValue > _cfg.PeakThreshold)
                        t.LastAudibleTicks = now;
                }
                catch { }
            }
        }
        Recompute();
    }

    // ---------- Hitung state agregat ----------
    private void ScheduleRecompute() => _recomputeTimer.Change(40, Timeout.Infinite);   // gabung burst event
    private void ScheduleRebuild() => _rebuildTimer.Change(500, Timeout.Infinite);

    private void Recompute()
    {
        AudioState next;
        lock (_gate)
        {
            if (_disposed) return;
            var now = Environment.TickCount64;
            var active = _tracked.Values
                .Where(t => !t.Ignored
                         && t.State == AudioSessionState.AudioSessionStateActive
                         && (!_cfg.UsePeakMeter || t.IsAudible(now, _cfg.PeakHoldMs)))
                .ToList();

            var names = active.Select(t => t.Process)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                              .ToList();

            next = new AudioState(names.Count > 0, names.FirstOrDefault(), names,
                                  active.Select(t => new SessionInfo(t.Process, t.Pid)).ToList());

            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (set.SetEquals(_lastNames)) { Current = next; return; }

            foreach (var n in set.Except(_lastNames)) Logger.Info($"{n} STARTED AUDIO");
            foreach (var n in _lastNames.Except(set)) Logger.Info($"{n} STOPPED AUDIO");
            _lastNames = set;
            Current = next;
        }
        StateChanged?.Invoke(next);
    }

    private static string Describe(AudioSessionState s) => s switch
    {
        AudioSessionState.AudioSessionStateActive => "ACTIVE",
        AudioSessionState.AudioSessionStateInactive => "INACTIVE",
        _ => "EXPIRED"
    };

    // ---------- IMMNotificationClient (device berubah) ----------
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => ScheduleRebuild();
    public void OnDeviceAdded(string pwstrDeviceId) => ScheduleRebuild();
    public void OnDeviceRemoved(string deviceId) => ScheduleRebuild();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render) ScheduleRebuild();
    }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _rescanTimer?.Dispose();
        _peakTimer?.Dispose();
        _recomputeTimer.Dispose();
        _rebuildTimer.Dispose();
        lock (_gate)
        {
            foreach (var t in _tracked.Values) t.Dispose();
            _tracked.Clear();
            foreach (var d in _devices) { try { d.Dispose(); } catch { } }
            _devices.Clear();
        }
        try { _enumerator.Dispose(); } catch { }
    }
}
