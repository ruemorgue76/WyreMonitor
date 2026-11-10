using System.Collections.Concurrent;
using System.Management;
using System.Security.Principal;
using System.Globalization;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using WyreMonitor.Desktop;

namespace WyreMonitor.Hardware;

/// <summary>
/// Hosted service that keeps a <see cref="LibreHardwareMonitor"/> session open and
/// refreshes every sensor on a background timer. Consumers read <see cref="Latest"/>,
/// an immutable snapshot swapped in atomically after each poll — so the HTTP layer
/// never touches the (non-thread-safe) LibreHardwareMonitor object graph directly.
/// </summary>
public sealed class HardwareMonitorService : IHostedService, IDisposable
{
    private readonly ILogger<HardwareMonitorService> _log;
    private readonly Computer _computer;
    private readonly object _updateLock = new();
    private volatile bool _stopping;   // set when the service stops: no more polls or timer
    private Timer? _timer;

    // Rolling history per sensor id: one sample per second for the last 15 minutes (PushHistory).
    // Each snapshot carries only the last minute; the longer chart asks for one sensor (History).
    public const int HistorySeconds = 900;
    private const int SnapshotHistorySeconds = 60;
    private readonly ConcurrentDictionary<string, RingBuffer> _history = new();

    private volatile Snapshot _latest = Snapshot.Empty;

    // Fan headers that have had a signal (docs/design/fans.md): when they last had one, and how
    // long the service has watched them since without one. Facts only: the page applies the rules.
    // Kept in HKLM, which only administrators and this service can change.
    private sealed class FanSignal
    {
        public DateTimeOffset LastSignal;
        public double QuietSeconds;
    }
    private const string FansValue = "Fans";
    private const double MaxRealRpm = 30000;                 // above this a reading is not a fan
    private static readonly TimeSpan MaxWatchedGap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FanSaveEvery = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, FanSignal> _fans = LoadFans();
    private readonly Dictionary<string, int> _fanStreak = new();
    private DateTime _lastFanCheck = DateTime.MinValue;
    private DateTime _lastFanSave = DateTime.UtcNow;
    private string? _systemDrive;

    // Reading every sensor takes a few hundred milliseconds on a big machine, mostly in parts
    // whose values change slowly. Each part is read at its own pace (see IntervalFor). With no
    // window open the service slows down, and after IdleStop it stops reading altogether: it
    // runs all the time, and nobody is looking.
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<IHardware, DateTime> _lastUpdate = new();
    private readonly HashSet<IHardware> _neverUpdate = new();
    private DateTime _lastPoll = DateTime.MinValue;
    private DateTime _lastSample = DateTime.MinValue;
    private long _lastClientRequest;   // Environment.TickCount64 of the page's last request
    private long _lastFastRequest;     // ... of the last request asking for two readings a second
    private long _lastBackgroundRequest;   // ... of the icon next to the clock (temperature, alerts)
    private static readonly TimeSpan BackgroundTimeout = TimeSpan.FromSeconds(60);

    public Snapshot Latest => _latest;

    /// <summary>The last 15 minutes of one sensor, one value per second; null for an unknown id.</summary>
    public double[]? History(string id) => _history.TryGetValue(id, out var buffer) ? buffer.ToArray() : null;

    /// <summary>
    /// Called by the icon next to the clock, which checks every few seconds with no window open:
    /// the sensors keep being read at the idle pace instead of stopping, never every second.
    /// </summary>
    public void NoteBackgroundActivity() => Interlocked.Exchange(ref _lastBackgroundRequest, Environment.TickCount64);

    private bool BackgroundActive => Within(ref _lastBackgroundRequest, BackgroundTimeout);

    // The small window and the number on the tray icon show a few headline values: every second
    // only the parts they show are read (CPU, graphics, memory), the rest at the idle pace.
    public enum SummaryPart { Cpu, Gpu, Memory }
    private readonly long[] _lastSummaryRequest = new long[3];
    private static readonly TimeSpan SummaryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Called for /api/summary: <paramref name="part"/> null means all three parts.</summary>
    public void NoteSummaryActivity(SummaryPart? part)
    {
        var now = Environment.TickCount64;
        for (var i = 0; i < _lastSummaryRequest.Length; i++)
            if (part is null || (int)part == i) Interlocked.Exchange(ref _lastSummaryRequest[i], now);
    }

    private bool SummaryWanted(SummaryPart part) => Within(ref _lastSummaryRequest[(int)part], SummaryTimeout);
    private bool SummaryActive => SummaryWanted(SummaryPart.Cpu) || SummaryWanted(SummaryPart.Gpu) || SummaryWanted(SummaryPart.Memory);

    /// <summary>
    /// Called on every page request: while a page is open, sensors are read every second, or
    /// every half second when the page asks for it (<paramref name="fast"/>).
    /// </summary>
    public void NoteClientActivity(bool fast)
    {
        var now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastClientRequest, now);
        if (fast) Interlocked.Exchange(ref _lastFastRequest, now);
    }

    private static bool Within(ref long since, TimeSpan window) =>
        Environment.TickCount64 - Interlocked.Read(ref since) < window.TotalMilliseconds;

    private bool ClientActive => Within(ref _lastClientRequest, ClientTimeout);
    private bool FastActive => Within(ref _lastFastRequest, FastTimeout);

    public bool IsElevated { get; }

    public HardwareMonitorService(ILogger<HardwareMonitorService> log)
    {
        _log = log;
        IsElevated = CheckElevated();
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            // Network off by default: it spawns one noisy group per virtual/physical NIC.
            IsNetworkEnabled = false,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Opening hardware monitor (elevated={Elevated})", IsElevated);
        // Opening the session takes several seconds (drivers, SMART on every disk). Doing it
        // in the background lets the server, and so the window, start at once: until the
        // first poll the page receives Snapshot.Empty and says the reading is in progress.
        _ = Task.Run(() =>
        {
            try
            {
                _systemDrive = FindSystemDriveModel();
                // Under the lock, so that stopping meanwhile waits for it (see Dispose).
                lock (_updateLock)
                {
                    if (_stopping) return;
                    _computer.Open();
                }
                Poll();
                if (_stopping) return;
                // Ticks every half second; Tick decides whether this one reads anything.
                _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Opening the hardware monitor failed");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Every half second while a page asks for it, every second while a page is open, every
    /// IdleInterval for a minute after, then not at all.
    /// </summary>
    private void Tick()
    {
        var fast = FastActive;
        var sincePoll = DateTime.UtcNow - _lastPoll;
        if (fast) { Poll(fast: true); return; }
        if (ClientActive)
        {
            if (sincePoll >= TimeSpan.FromSeconds(1) - Tolerance) Poll(fast: false);
            return;
        }
        if (SummaryActive)
        {
            if (sincePoll >= TimeSpan.FromSeconds(1) - Tolerance) Poll(fast: false, summaryOnly: true);
            return;
        }
        var sinceClient = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastClientRequest));
        if (sinceClient > IdleStop && !BackgroundActive) return;
        if (sincePoll >= IdleInterval - Tolerance) Poll(fast: false);
    }

    /// <summary>How often a part is read. Temperatures of drives and memory modules move slowly.</summary>
    private TimeSpan IntervalFor(IHardware hw, bool fast, bool summaryOnly) => hw.HardwareType switch
    {
        // Only the small window or the tray number is showing: just the parts they show are
        // fresh; the rest keeps the idle pace, enough for the notifications.
        _ when summaryOnly && !IsSummaryPart(hw) => IdleInterval,
        HardwareType.Storage => TimeSpan.FromSeconds(10),
        HardwareType.Memory when hw.Name.EndsWith(')') && hw.Name.Contains("(#") => TimeSpan.FromSeconds(5),
        _ => fast ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(1),
    };

    private bool IsSummaryPart(IHardware hw) => hw.HardwareType switch
    {
        HardwareType.Cpu => SummaryWanted(SummaryPart.Cpu),
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => SummaryWanted(SummaryPart.Gpu),
        HardwareType.Memory => !hw.Name.Contains("(#") && SummaryWanted(SummaryPart.Memory),
        _ => false,
    };

    private void UpdateHardware(DateTime now, bool fast, bool summaryOnly)
    {
        foreach (var hw in _computer.Hardware)
        {
            if (_neverUpdate.Contains(hw)) continue;
            if (_lastUpdate.TryGetValue(hw, out var last) && now - last < IntervalFor(hw, fast, summaryOnly) - Tolerance) continue;

            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
            _lastUpdate[hw] = now;

            // Empty card readers report a total space of 0: reading them again is wasted time.
            if (hw.HardwareType == HardwareType.Storage &&
                hw.Sensors.Any(s => s.SensorType == SensorType.Data && s.Name == "Total Space" && (s.Value ?? 0) == 0))
            {
                _neverUpdate.Add(hw);
            }
        }
    }

    private void Poll(bool fast = false, bool summaryOnly = false)
    {
        // Timer callbacks can overlap on slow hardware; skip if a poll is in flight.
        if (!Monitor.TryEnter(_updateLock)) return;
        try
        {
            if (_stopping) return;   // the session is closing or closed
            var now = DateTime.UtcNow;
            // After a pause in reading, the old history no longer describes the last minute.
            if (_lastPoll != DateTime.MinValue && now - _lastPoll > IdleStop)
            {
                _history.Clear();
                _lastSample = DateTime.MinValue;
            }
            _lastPoll = now;

            // The history holds one sample per second whatever the reading pace: two readings
            // a second add one sample every other time, and after a slow (idle) reading the value
            // is repeated for the seconds that passed, so the chart's time axis stays true.
            var samples = _lastSample == DateTime.MinValue
                ? 1
                : (int)Math.Clamp(Math.Floor((now - _lastSample + Tolerance).TotalSeconds), 0, 10);
            if (samples > 0) _lastSample = now;

            UpdateHardware(now, fast, summaryOnly);

            var groups = new List<HardwareGroup>();
            foreach (var hw in _computer.Hardware)
            {
                groups.Add(BuildGroup(hw, samples));
                foreach (var sub in hw.SubHardware)
                    groups.Add(BuildGroup(sub, samples, parent: hw.Name));
            }

            UpdateFans(groups);

            _latest = new Snapshot(
                DateTimeOffset.UtcNow,
                IsElevated,
                groups,
                BuildSummary(groups),
                SystemDrive: _systemDrive,
                MachineName: Environment.MachineName,
                FanSignals: _fans.Select(f => new FanSignalInfo(f.Key, f.Value.LastSignal, Math.Round(f.Value.QuietSeconds / 3600, 2))).ToList());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sensor poll failed");
        }
        finally
        {
            Monitor.Exit(_updateLock);
        }
    }

    private HardwareGroup BuildGroup(IHardware hw, int samples, string? parent = null)
    {
        var sensors = new List<SensorReading>();
        foreach (var s in hw.Sensors)
        {
            // Infinity too (a ratio divided by zero): JSON can't carry it, and one such value
            // would make every answer of the service fail.
            if (s.Value is null || !float.IsFinite(s.Value.Value)) continue;

            var value = Math.Round(s.Value.Value, 2);
            var reading = new SensorReading(
                Id: s.Identifier.ToString(),
                Name: s.Name,
                Type: s.SensorType.ToString(),
                Unit: UnitFor(s.SensorType),
                Value: value,
                Min: s.Min is { } mn && float.IsFinite(mn) ? Math.Round(mn, 2) : null,
                Max: s.Max is { } mx && float.IsFinite(mx) ? Math.Round(mx, 2) : null,
                History: PushHistory(s.Identifier.ToString(), value, samples));
            sensors.Add(reading);
        }

        var name = parent is null ? hw.Name : $"{parent} › {hw.Name}";
        return new HardwareGroup(name, hw.HardwareType.ToString(), sensors);
    }

    /// <summary>
    /// Notes which fan headers have a signal now. Only time between close readings counts as
    /// watched: while the service isn't reading (no window, PC off) nothing is counted.
    /// </summary>
    private void UpdateFans(IReadOnlyList<HardwareGroup> groups)
    {
        var now = DateTime.UtcNow;
        var gap = _lastFanCheck == DateTime.MinValue ? TimeSpan.Zero : now - _lastFanCheck;
        _lastFanCheck = now;
        var watched = gap > TimeSpan.Zero && gap <= MaxWatchedGap ? gap.TotalSeconds : 0;
        var changed = false;

        foreach (var g in groups.Where(g => g.Type is "SuperIO" or "EmbeddedController"))
        {
            foreach (var s in g.Sensors.Where(s => s.Type == "Fan"))
            {
                var signal = s.Value is > 0 and < MaxRealRpm;
                // Two readings in a row: one odd reading is not a signal.
                var streak = signal ? _fanStreak.GetValueOrDefault(s.Id) + 1 : 0;
                _fanStreak[s.Id] = streak;
                if (_fans.TryGetValue(s.Id, out var fan))
                {
                    if (streak >= 2) { fan.LastSignal = DateTimeOffset.UtcNow; fan.QuietSeconds = 0; }
                    else if (!signal) fan.QuietSeconds += watched;
                }
                else if (streak >= 2)
                {
                    _fans[s.Id] = new FanSignal { LastSignal = DateTimeOffset.UtcNow };
                    changed = true;
                }
            }
        }

        if (changed || now - _lastFanSave >= FanSaveEvery)
        {
            _lastFanSave = now;
            SaveFans();
        }
    }

    // One line per header: id, last signal (Unix seconds), watched seconds without a signal.
    private static Dictionary<string, FanSignal> LoadFans()
    {
        var fans = new Dictionary<string, FanSignal>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AppInfo.RegistryKey);
            if (key?.GetValue(FansValue) is not string[] lines) return fans;
            foreach (var line in lines.Take(200))
            {
                var parts = line.Split('	');
                if (parts.Length == 3 && parts[0].Length is > 0 and <= 256 &&
                    long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var quiet) &&
                    quiet >= 0 && double.IsFinite(quiet))
                    fans[parts[0]] = new FanSignal { LastSignal = DateTimeOffset.FromUnixTimeSeconds(last), QuietSeconds = quiet };
            }
        }
        catch { /* nothing remembered: every header starts empty */ }
        return fans;
    }

    private void SaveFans()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AppInfo.RegistryKey);
            key.SetValue(FansValue, _fans.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => string.Join('	', f.Key,
                    f.Value.LastSignal.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    Math.Round(f.Value.QuietSeconds).ToString(CultureInfo.InvariantCulture)))
                .ToArray(), RegistryValueKind.MultiString);
        }
        catch
        {
            // A development run without administrator rights: remembered until it stops.
        }
    }

    private double[] PushHistory(string id, double value, int samples)
    {
        var buffer = _history.GetOrAdd(id, _ => new RingBuffer(HistorySeconds));
        for (var i = 0; i < samples; i++) buffer.Add(value);
        return buffer.Last(SnapshotHistorySeconds);
    }

    private static Summary BuildSummary(IReadOnlyList<HardwareGroup> groups)
    {
        double? cpuTempPreferred = null;   // Package / Tdie / Tctl — the canonical CPU temp
        double? cpuTempAny = null;         // hottest temp on the CPU node
        double? cpuTempMobo = null;        // "CPU" temp reported by the motherboard Super-I/O
        double? cpuLoad = null, cpuPower = null, cpuClock = null;
        double? gpuTempPreferred = null, gpuTempAny = null, gpuLoad = null, gpuPower = null;
        double? ramLoadPhys = null, ramUsedPhys = null;   // "Total Memory" = physical DIMMs
        double? ramLoadAny = null, ramUsedAny = null;      // fallback (e.g. "Virtual Memory")

        double maxFan = 0;
        bool anyFan = false;   // distinguishes "0 RPM" from "no fan sensor at all"

        // Graphics cards with their own memory first: on a laptop the summary is the discrete one's.
        var ordered = groups.OrderBy(g => g.Type.StartsWith("Gpu") && !g.Sensors.Any(s => s.Name == "GPU Memory Total") ? 1 : 0);
        foreach (var g in ordered)
        {
            var isCpu = g.Type == "Cpu";
            var isGpu = g.Type.StartsWith("Gpu");
            var isRam = g.Type == "Memory";
            var isPhysRam = isRam && !g.Name.Contains("Virtual");   // exclude commit/pagefile
            var isMobo = g.Type is "Motherboard" or "SuperIO" or "EmbeddedController";

            foreach (var s in g.Sensors)
            {
                switch (s.Type)
                {
                    case "Temperature" when isCpu:
                        // First canonical control sensor wins: "Core (Tctl/Tdie)" (AMD) or
                        // "CPU Package" (Intel) — not the per-CCD average that trails it.
                        if (s.Name.Contains("Package") || s.Name.Contains("Tdie") || s.Name.Contains("Tctl"))
                            cpuTempPreferred ??= s.Value;
                        cpuTempAny = Math.Max(cpuTempAny ?? double.MinValue, s.Value);
                        break;
                    // Many desktops surface the CPU temperature only through the board's Super-I/O.
                    case "Temperature" when isMobo && s.Name.Contains("CPU"):
                        cpuTempMobo = Math.Max(cpuTempMobo ?? double.MinValue, s.Value);
                        break;
                    case "Temperature" when isGpu:
                        if (s.Name.Contains("Core") || s.Name.Contains("Hot") || s.Name.Contains("GPU"))
                            gpuTempPreferred ??= s.Value;
                        gpuTempAny = Math.Max(gpuTempAny ?? double.MinValue, s.Value);
                        break;
                    case "Load" when isCpu && s.Name.Contains("Total"):
                        cpuLoad = s.Value;
                        break;
                    case "Load" when isGpu && s.Name.Contains("Core"):
                        gpuLoad = s.Value;
                        break;
                    case "Load" when isRam && s.Name.Contains("Memory"):
                        if (isPhysRam) ramLoadPhys ??= s.Value;
                        ramLoadAny ??= s.Value;
                        break;
                    case "Data" when isRam && s.Name.Contains("Used"):
                        if (isPhysRam) ramUsedPhys ??= s.Value;
                        ramUsedAny ??= s.Value;
                        break;
                    case "Power" when isCpu && (s.Name.Contains("Package") || s.Name.Contains("CPU")):
                        cpuPower ??= s.Value;
                        break;
                    case "Power" when isGpu:
                        gpuPower ??= s.Value;
                        break;
                    case "Clock" when isCpu && s.Name.Contains("Core"):
                        cpuClock = Math.Max(cpuClock ?? 0, s.Value);
                        break;
                    case "Fan":
                        anyFan = true;
                        maxFan = Math.Max(maxFan, s.Value);
                        break;
                }
            }
        }

        // The AMD SMU sometimes returns 0 for temp/clock/power (e.g. when another
        // monitoring tool holds the hardware mutex). Treat 0 as "unavailable" so the
        // card shows n/d instead of a misleading zero.
        static double? Positive(double? v) => v is > 0 ? v : null;

        var cpuTemp = Positive(cpuTempPreferred) ?? Positive(cpuTempAny) ?? Positive(cpuTempMobo);
        var gpuTemp = Positive(gpuTempPreferred) ?? Positive(gpuTempAny);

        return new Summary(cpuTemp, cpuLoad, Positive(cpuClock), Positive(cpuPower),
            gpuTemp, gpuLoad, gpuPower,
            ramLoadPhys ?? ramLoadAny, ramUsedPhys ?? ramUsedAny,
            anyFan ? maxFan : null);
    }

    private static string UnitFor(SensorType t) => t switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "x",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        _ => "",
    };

    /// <summary>
    /// Model of the physical disk holding the Windows partition (C: → partition → disk).
    /// LibreHardwareMonitor names storage groups by model, so the page matches them by name.
    /// </summary>
    private static string? FindSystemDriveModel()
    {
        try
        {
            var letter = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (var partition in partitions.Get().Cast<ManagementObject>())
            {
                using var disks = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (var disk in disks.Get().Cast<ManagementObject>())
                {
                    if (disk["Model"] is string model) return model.Trim();
                }
            }
        }
        catch { /* WMI unavailable: the page simply raises no free-space alerts */ }
        return null;
    }

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        // The hours counted since the last save are kept across an update or a restart.
        lock (_updateLock) SaveFans();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stopping = true;
        _timer?.Dispose();
        // The library isn't thread-safe: wait for an Open or a Poll still running before closing.
        lock (_updateLock)
        {
            try { _computer.Close(); } catch { /* driver may already be unloaded */ }
        }
    }
}
