using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinCleaner.Optimizer.Monitoring;

/// <summary>무엇을 읽을지. 꺼 둔 부분은 성능 카운터를 추가하지도 않는다.</summary>
[Flags]
public enum MonitorParts
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    DiskNetwork = 4,
    TopProcess = 8,
}

/// <param name="UsagePercent">가장 바쁜 엔진(3D·복사·영상 등) 기준 사용률. 작업 관리자와 같은 방식.</param>
/// <param name="TemperatureCelsius">NVIDIA이고 일하고 있을 때만.</param>
public sealed record GpuSample(string Name, double UsagePercent, int? TemperatureCelsius, double? CoreGhz);

/// <summary>한 번 읽은 값. 읽지 않았거나 못 읽은 값은 null. 비율 값은 지난번 읽은 뒤의 평균이다.</summary>
public sealed record SystemSample
{
    /// <summary>CPU 사용률(%). 작업 관리자와 같은 기준(% Processor Utility, 100에서 자름).</summary>
    public double? CpuUsage { get; init; }

    /// <summary>CPU 실제 평균 속도(GHz). 기본 속도 × % Processor Performance.</summary>
    public double? CpuGhz { get; init; }

    /// <summary>Windows·펌웨어가 정한 속도 상한(%). 100이면 제한 없음. 열 때문에 느려지는 것은 여기 나오지 않는다(2026-11-07 확인).</summary>
    public double? PerformanceLimit { get; init; }

    public GpuSample? Gpu { get; init; }

    public double? DiskReadBytesPerSecond { get; init; }
    public double? DiskWriteBytesPerSecond { get; init; }
    public double? NetworkReceiveBytesPerSecond { get; init; }
    public double? NetworkSendBytesPerSecond { get; init; }

    /// <summary>CPU를 가장 많이 쓰는 프로그램 이름과 사용률(전체 CPU 기준 %).</summary>
    public string? TopCpuProcess { get; init; }

    public double? TopCpuPercent { get; init; }
}

/// <summary>CPU·GPU·디스크·네트워크 사용량. 테스트에서 실제 성능 카운터를 쓰지 않도록 분리했다.</summary>
public interface ISystemMonitor
{
    SystemSample Sample(MonitorParts parts);
}

/// <summary>성능 카운터(PDH), DXGI, NVML로 사용량을 읽는다. 한 번에 한 스레드에서만 부른다.</summary>
public sealed partial class PdhSystemMonitor : ISystemMonitor, IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly NvidiaGpu _nvidia = new();
    private readonly Lazy<IReadOnlyList<GpuAdapter>> _adapters = new(() => SafeList());
    private readonly double _baseGhz = ReadBaseMhz() / 1000.0;
    private readonly int _cpuCount = Environment.ProcessorCount;
    private MonitorParts _added;
    private nint _utility, _performance, _limit;
    private nint _gpuEngines;
    private nint _diskRead, _diskWrite, _netReceive, _netSend;
    private nint _processes;

    public SystemSample Sample(MonitorParts parts)
    {
        Prepare(parts);
        if (!_query.Collect())
            return new SystemSample();

        var sample = new SystemSample();
        if (parts.HasFlag(MonitorParts.Cpu))
        {
            var performance = PdhQuery.Read(_performance);
            sample = sample with
            {
                CpuUsage = PdhQuery.Read(_utility) is { } usage ? Math.Clamp(usage, 0, 100) : null,
                CpuGhz = performance is { } p && _baseGhz > 0 ? _baseGhz * p / 100 : null,
                PerformanceLimit = PdhQuery.Read(_limit),
            };
        }
        if (parts.HasFlag(MonitorParts.Gpu))
            sample = sample with { Gpu = ReadGpu() };
        if (parts.HasFlag(MonitorParts.DiskNetwork))
        {
            sample = sample with
            {
                DiskReadBytesPerSecond = PdhQuery.Read(_diskRead),
                DiskWriteBytesPerSecond = PdhQuery.Read(_diskWrite),
                NetworkReceiveBytesPerSecond = Sum(_netReceive),
                NetworkSendBytesPerSecond = Sum(_netSend),
            };
        }
        if (parts.HasFlag(MonitorParts.TopProcess) && TopProcess(PdhQuery.ReadArray(_processes), _cpuCount) is var (name, percent))
            sample = sample with { TopCpuProcess = name, TopCpuPercent = percent };
        return sample;
    }

    public void Dispose()
    {
        _nvidia.Dispose();
        _query.Dispose();
    }

    /// <summary>그래픽 엔진 인스턴스별 사용률을 어댑터별 사용률로 묶는다. 어댑터마다 가장 바쁜 엔진 종류의 합.</summary>
    public static IReadOnlyDictionary<string, double> GpuUsageByAdapter(IEnumerable<(string Instance, double Value)> engines)
    {
        var byEngine = new Dictionary<(string Luid, string Engine), double>();
        foreach (var (instance, value) in engines)
        {
            var match = EngineName().Match(instance);
            if (!match.Success)
                continue;
            var key = (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value.ToLowerInvariant());
            byEngine[key] = byEngine.GetValueOrDefault(key) + value;
        }
        return byEngine
            .GroupBy(e => e.Key.Luid)
            .ToDictionary(g => g.Key, g => Math.Min(100, g.Max(e => e.Value)));
    }

    /// <summary>CPU를 가장 많이 쓰는 프로그램. 이름이 같은 프로세스(chrome#1, chrome#2…)는 합친다. % Processor Time은 CPU 하나 기준이라 CPU 수로 나눈다.</summary>
    public static (string Name, double Percent)? TopProcess(IEnumerable<(string Instance, double Value)> processes, int cpuCount)
    {
        var top = processes
            .Select(p => (Name: InstanceSuffix().Replace(p.Instance, ""), p.Value))
            .Where(p => !p.Name.Equals("_Total", StringComparison.OrdinalIgnoreCase) && !p.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Percent: g.Sum(p => p.Value) / Math.Max(1, cpuCount)))
            .OrderByDescending(p => p.Percent)
            .FirstOrDefault();
        return top.Name is null ? null : top;
    }

    private GpuSample? ReadGpu()
    {
        var usage = GpuUsageByAdapter(PdhQuery.ReadArray(_gpuEngines));
        var adapters = _adapters.Value;

        // NVIDIA 그래픽카드가 있으면 그것을 보여 준다. 게임은 보통 그쪽에서 돈다.
        if (adapters.FirstOrDefault(a => a.IsNvidia) is { } nvidia)
        {
            var percent = usage.GetValueOrDefault(nvidia.Luid);
            var (celsius, ghz) = _nvidia.Read(active: percent >= 0.5);
            return new GpuSample(nvidia.Name, percent, celsius, ghz);
        }

        if (usage.Count == 0)
            return null;
        var busiest = usage.MaxBy(u => u.Value);
        var name = adapters.FirstOrDefault(a => a.Luid == busiest.Key)?.Name ?? "GPU";
        return new GpuSample(name, busiest.Value, null, null);
    }

    private void Prepare(MonitorParts parts)
    {
        Toggle(parts, MonitorParts.Cpu, () =>
        {
            _utility = _query.Add(@"\Processor Information(_Total)\% Processor Utility");
            _performance = _query.Add(@"\Processor Information(_Total)\% Processor Performance");
            _limit = _query.Add(@"\Processor Information(_Total)\% Performance Limit");
        }, () => Remove(ref _utility, ref _performance, ref _limit));
        Toggle(parts, MonitorParts.Gpu,
            () => _gpuEngines = _query.Add(@"\GPU Engine(*)\Utilization Percentage"),
            () => Remove(ref _gpuEngines));
        Toggle(parts, MonitorParts.DiskNetwork, () =>
        {
            _diskRead = _query.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _diskWrite = _query.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            _netReceive = _query.Add(@"\Network Interface(*)\Bytes Received/sec");
            _netSend = _query.Add(@"\Network Interface(*)\Bytes Sent/sec");
        }, () => Remove(ref _diskRead, ref _diskWrite, ref _netReceive, ref _netSend));
        Toggle(parts, MonitorParts.TopProcess,
            () => _processes = _query.Add(@"\Process(*)\% Processor Time"),
            () => Remove(ref _processes));
    }

    private void Toggle(MonitorParts wanted, MonitorParts part, Action add, Action remove)
    {
        var want = wanted.HasFlag(part);
        var have = _added.HasFlag(part);
        if (want && !have)
        {
            add();
            _added |= part;
        }
        else if (!want && have)
        {
            remove();
            _added &= ~part;
        }
    }

    private void Remove(ref nint a) => Remove(ref a, ref a, ref a, ref a);

    private void Remove(ref nint a, ref nint b, ref nint c) => Remove(ref a, ref b, ref c, ref c);

    private void Remove(ref nint a, ref nint b, ref nint c, ref nint d)
    {
        foreach (var counter in new[] { a, b, c, d }.Distinct())
            _query.Remove(counter);
        a = b = c = d = 0;
    }

    private static double? Sum(nint counter)
    {
        var values = PdhQuery.ReadArray(counter);
        return values.Count == 0 ? null : values.Sum(v => v.Value);
    }

    private static IReadOnlyList<GpuAdapter> SafeList()
    {
        try
        {
            return GpuAdapters.List();
        }
        catch (Exception)
        {
            // DXGI가 없는 환경(일부 원격 세션 등). 이름 없이 사용률만 보여 준다.
            return [];
        }
    }

    /// <summary>CPU 기본 속도(MHz). i7-8750H는 약 2,200.</summary>
    private static double ReadBaseMhz()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("~MHz") is int mhz ? mhz : 0;
    }

    // 예: pid_1012_luid_0x00000000_0x00015aec_phys_0_eng_0_engtype_3D
    [GeneratedRegex(@"luid_(0x[0-9a-fA-F]+_0x[0-9a-fA-F]+)_phys_\d+_eng_\d+_engtype_(.+)$")]
    private static partial Regex EngineName();

    [GeneratedRegex(@"#\d+$")]
    private static partial Regex InstanceSuffix();
}
