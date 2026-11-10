namespace WyreMonitor.Hardware;

/// <summary>An immutable point-in-time view of every hardware sensor.</summary>
public sealed record Snapshot(
    DateTimeOffset Timestamp,
    bool Elevated,
    IReadOnlyList<HardwareGroup> Hardware,
    Summary Summary,
    bool Ready = true,
    string? SystemDrive = null,     // model of the disk Windows runs from, matched by name on the page
    string? MachineName = null,
    IReadOnlyList<FanSignalInfo>? FanSignals = null)
{
    /// <summary>Served while the hardware session is still opening (several seconds at startup).</summary>
    public static readonly Snapshot Empty =
        new(DateTimeOffset.UtcNow, false, Array.Empty<HardwareGroup>(),
            new Summary(null, null, null, null, null, null, null, null, null, null), Ready: false);
}

/// <summary>A fan header that has had a signal: when it last had one, and the hours the service
/// has watched it since without one (docs/design/fans.md).</summary>
public sealed record FanSignalInfo(string Id, DateTimeOffset LastSignal, double QuietHours);

public sealed record HardwareGroup(
    string Name,
    string Type,
    IReadOnlyList<SensorReading> Sensors);

public sealed record SensorReading(
    string Id,
    string Name,
    string Type,
    string Unit,
    double Value,
    double? Min,
    double? Max,
    double[] History);

/// <summary>Headline metrics surfaced as the dashboard's top cards.</summary>
public sealed record Summary(
    double? CpuTemp,
    double? CpuLoad,
    double? CpuClock,
    double? CpuPower,
    double? GpuTemp,
    double? GpuLoad,
    double? GpuPower,
    double? RamLoad,
    double? RamUsed,
    double? MaxFanRpm);

/// <summary>Fixed-capacity circular buffer of recent sensor values (for sparklines).</summary>
public sealed class RingBuffer
{
    private readonly double[] _buffer;
    private int _count;
    private int _head;

    public RingBuffer(int capacity) => _buffer = new double[capacity];

    public void Add(double value)
    {
        _buffer[_head] = value;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    public double[] ToArray() => Last(_count);

    /// <summary>The most recent <paramref name="n"/> values, oldest first.</summary>
    public double[] Last(int n)
    {
        n = Math.Min(n, _count);
        var result = new double[n];
        var start = (_head - n + _buffer.Length) % _buffer.Length;
        for (var i = 0; i < n; i++)
            result[i] = _buffer[(start + i) % _buffer.Length];
        return result;
    }
}
