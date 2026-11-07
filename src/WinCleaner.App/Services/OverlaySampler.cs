using WinCleaner.Memory;
using WinCleaner.Optimizer.Fans;
using WinCleaner.Optimizer.Monitoring;

namespace WinCleaner.App.Services;

/// <summary>오버레이에 보여 줄 값 한 번 분량. 읽지 않았거나 못 읽은 값은 null.</summary>
/// <param name="TopMemory">메모리를 가장 많이 쓰는 프로그램(이름이 같은 프로세스는 합침)과 작업 집합 크기.</param>
public sealed record OverlaySnapshot(SystemSample? System, SystemMemory? Memory, FanReading? Fans, (string Name, long Bytes)? TopMemory = null);

public interface IOverlaySampler
{
    /// <summary>설정에서 켠 항목만 읽는다.</summary>
    OverlaySnapshot Sample(OverlaySettings settings);
}

/// <summary>CPU·GPU·메모리·온도 등을 한 번에 읽는다. 오버레이가 보이는 동안 1초마다 백그라운드에서 부른다.</summary>
/// <param name="monitor">성능 카운터를 열지 못한 PC면 null.</param>
/// <param name="readFans">관리자 권한이 아니면 팬 인터페이스가 늘 실패하므로 아예 읽지 않는다.</param>
public sealed class OverlaySampler(ISystemMonitor? monitor, IMemoryService memory, IFanMonitor fans, bool readFans) : IOverlaySampler
{
    public OverlaySnapshot Sample(OverlaySettings settings)
    {
        var parts = MonitorParts.None;
        if (settings.ShowCpu || settings.ShowThrottle)
            parts |= MonitorParts.Cpu;
        if (settings.ShowGpu)
            parts |= MonitorParts.Gpu;
        if (settings.ShowGpu && settings.ReadGpuWhenIdle)
            parts |= MonitorParts.GpuWhenIdle;
        if (settings.ShowDiskNetwork)
            parts |= MonitorParts.DiskNetwork;
        if (settings.ShowTopProcesses)
            parts |= MonitorParts.TopProcess;

        var system = monitor?.Sample(parts);
        var memoryNow = settings.ShowMemory ? Try(memory.ReadSystem) : null;
        var fanReading = readFans && (settings.ShowTemperature || settings.ShowFan || settings.ShowThrottle) ? fans.Read() : null;
        var topMemory = settings.ShowTopProcesses ? Try(() => TopMemory(memory.ReadProcesses())) : null;
        return new OverlaySnapshot(system, memoryNow, fanReading, topMemory);
    }

    public static (string Name, long Bytes)? TopMemory(IEnumerable<ProcessMemory> processes)
    {
        var top = processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Bytes: g.Sum(p => p.WorkingSetBytes)))
            .OrderByDescending(p => p.Bytes)
            .FirstOrDefault();
        return top.Name is null ? null : top;
    }

    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return default;
        }
    }
}
