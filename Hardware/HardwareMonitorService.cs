using System.Collections.Concurrent;
using System.Management;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

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
    private Timer? _timer;

    // Rolling history per sensor id: one sample per second for the last 15 minutes (PushHistory).
    // Each snapshot carries only the last minute; the longer chart asks for one sensor (History).
    public const int HistorySeconds = 900;
    private const int SnapshotHistorySeconds = 60;
    private readonly ConcurrentDictionary<string, RingBuffer> _history = new();

    private volatile Snapshot _latest = Snapshot.Empty;
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

    // The small always-on-top window shows a few numbers: a reading every 2 seconds is enough.
    private long _lastSummaryRequest;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(2);

    /// <summary>Called by the small window: sensors are read every 2 seconds while it is open.</summary>
    public void NoteSummaryActivity() => Interlocked.Exchange(ref _lastSummaryRequest, Environment.TickCount64);

    private bool SummaryActive => Within(ref _lastSummaryRequest, ClientTimeout);

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
                _computer.Open();
                Poll();
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
            if (sincePoll >= SummaryInterval - Tolerance) Poll(fast: false, summaryOnly: true);
            return;
        }
        var sinceClient = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastClientRequest));
        if (sinceClient > IdleStop && !BackgroundActive) return;
        if (sincePoll >= IdleInterval - Tolerance) Poll(fast: false);
    }

    /// <summary>How often a part is read. Temperatures of drives and memory modules move slowly.</summary>
    private static TimeSpan IntervalFor(IHardware hw, bool fast, bool summaryOnly) => hw.HardwareType switch
    {
        // Only the small window is open: just what it shows (CPU, graphics, memory) is fresh;
        // the rest keeps the idle pace, enough for the notifications.
        _ when summaryOnly && !IsSummaryPart(hw) => IdleInterval,
        HardwareType.Storage => TimeSpan.FromSeconds(10),
        HardwareType.Memory when hw.Name.EndsWith(')') && hw.Name.Contains("(#") => TimeSpan.FromSeconds(5),
        _ => fast ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(1),
    };

    private static bool IsSummaryPart(IHardware hw) =>
        hw.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
        || (hw.HardwareType == HardwareType.Memory && !hw.Name.Contains("(#"));

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

            _latest = new Snapshot(
                DateTimeOffset.UtcNow,
                IsElevated,
                groups,
                BuildSummary(groups),
                SystemDrive: _systemDrive,
                MachineName: Environment.MachineName);
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
            if (s.Value is null || float.IsNaN(s.Value.Value)) continue;

            var value = Math.Round(s.Value.Value, 2);
            var reading = new SensorReading(
                Id: s.Identifier.ToString(),
                Name: s.Name,
                Type: s.SensorType.ToString(),
                Unit: UnitFor(s.SensorType),
                Value: value,
                Min: s.Min is { } mn ? Math.Round(mn, 2) : null,
                Max: s.Max is { } mx ? Math.Round(mx, 2) : null,
                History: PushHistory(s.Identifier.ToString(), value, samples));
            sensors.Add(reading);
        }

        var name = parent is null ? hw.Name : $"{parent} › {hw.Name}";
        return new HardwareGroup(name, hw.HardwareType.ToString(), sensors);
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

        foreach (var g in groups)
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
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        try { _computer.Close(); } catch { /* driver may already be unloaded */ }
    }
}
