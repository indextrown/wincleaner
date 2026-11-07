using WinCleaner.Memory;
using WinCleaner.Optimizer.Cpu;
using WinCleaner.Optimizer.Fans;

namespace WinCleaner.App.Services;

/// <summary>오버레이에 보여 줄 값 한 번 분량. 읽지 못한 값은 null.</summary>
public sealed record OverlaySnapshot(CpuSample? Cpu, SystemMemory? Memory, FanReading? Fans);

public interface IOverlaySampler
{
    OverlaySnapshot Sample();
}

/// <summary>CPU·메모리·온도를 한 번에 읽는다. 오버레이가 보이는 동안 1초마다 백그라운드에서 부른다.</summary>
/// <param name="cpu">성능 카운터를 열지 못한 PC면 null.</param>
/// <param name="readFans">관리자 권한이 아니면 팬 인터페이스가 늘 실패하므로 아예 읽지 않는다.</param>
public sealed class OverlaySampler(ICpuMonitor? cpu, IMemoryService memory, IFanMonitor fans, bool readFans) : IOverlaySampler
{
    public OverlaySnapshot Sample()
    {
        SystemMemory? system;
        try
        {
            system = memory.ReadSystem();
        }
        catch (InvalidOperationException)
        {
            system = null;
        }
        return new OverlaySnapshot(cpu?.Sample(), system, readFans ? fans.Read() : null);
    }
}
