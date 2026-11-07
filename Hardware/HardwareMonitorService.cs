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
    private readonly UpdateVisitor _visitor = new();
    private readonly object _updateLock = new();
    private Timer? _timer;

    // Rolling history per sensor id, for the dashboard sparklines.
    private readonly ConcurrentDictionary<string, RingBuffer> _history = new();

    private volatile Snapshot _latest = Snapshot.Empty;
    private string? _systemDrive;
    public Snapshot Latest => _latest;

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
                _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Opening the hardware monitor failed");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    private void Poll()
    {
        // Timer callbacks can overlap on slow hardware; skip if a poll is in flight.
        if (!Monitor.TryEnter(_updateLock)) return;
        try
        {
            _computer.Accept(_visitor);

            var groups = new List<HardwareGroup>();
            foreach (var hw in _computer.Hardware)
            {
                groups.Add(BuildGroup(hw));
                foreach (var sub in hw.SubHardware)
                    groups.Add(BuildGroup(sub, parent: hw.Name));
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

    private HardwareGroup BuildGroup(IHardware hw, string? parent = null)
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
                History: PushHistory(s.Identifier.ToString(), value));
            sensors.Add(reading);
        }

        var name = parent is null ? hw.Name : $"{parent} › {hw.Name}";
        return new HardwareGroup(name, hw.HardwareType.ToString(), sensors);
    }

    private double[] PushHistory(string id, double value)
    {
        var buffer = _history.GetOrAdd(id, _ => new RingBuffer(60));
        buffer.Add(value);
        return buffer.ToArray();
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

    /// <summary>Walks the hardware tree and refreshes each node's sensor values.</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
