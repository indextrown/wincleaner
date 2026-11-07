using System.Management;

namespace WinCleaner.Optimizer.Fans;

/// <summary>팬 속도를 바꾸는 부분. 테스트에서 실제 하드웨어를 건드리지 않도록 분리했다.</summary>
public interface IFanControl
{
    /// <summary>팬 속도를 직접 정한다(%). 그동안 노트북 펌웨어의 자동 제어는 멈춘다.</summary>
    void SetDuty(int cpuPercent, int gpuPercent);

    /// <summary>노트북 펌웨어의 자동 제어로 되돌린다.</summary>
    void SetAuto();
}

/// <summary>
/// Clevo 제조사 WMI의 SetFanDuty(id 104)·SetFanAutoDuty(id 105)로 팬 속도를 바꾼다 (사양서 3.2.1).
/// 값 형식은 리눅스 Clevo 드라이버(tuxedo-drivers)와 같다: 팬마다 1바이트(0~255)씩 팬 1·2·3 순서로 묶고,
/// 자동 복귀는 되돌릴 팬 비트(0x0F = 전부)를 보낸다.
/// </summary>
public sealed class ClevoFanControl : IFanControl
{
    public const uint AllFansAuto = 0x0F;

    public void SetDuty(int cpuPercent, int gpuPercent) => Run(instance =>
    {
        // 팬 3은 지금 값을 그대로 보낸다(없는 기기면 영향 없음). 읽지 못하면 안전한 쪽인 최대로 둔다.
        var fan3 = ClevoWmi.Read(instance, "Fan3Info") is { } info and not 0xFFFFFFFF ? (byte)(info & 0xFF) : (byte)0xFF;
        ClevoWmi.Write(instance, "SetFanDuty", Pack(ToRaw(cpuPercent), ToRaw(gpuPercent), fan3));
    });

    public void SetAuto() => Run(instance => ClevoWmi.Write(instance, "SetFanAutoDuty", AllFansAuto));

    /// <summary>백분율을 0~255 값으로 바꾼다. 팬이 멈추지 않도록 <see cref="FanMode.MinPercent"/> 아래로는 내리지 않는다.</summary>
    public static byte ToRaw(int percent) =>
        (byte)Math.Round(Math.Clamp(percent, FanMode.MinPercent, 100) * 255 / 100.0);

    public static uint Pack(byte fan1, byte fan2, byte fan3) => fan1 | (uint)fan2 << 8 | (uint)fan3 << 16;

    private static void Run(Action<ManagementObject> action)
    {
        try
        {
            ClevoWmi.Use(instance =>
            {
                action(instance);
                return true;
            });
        }
        catch (ManagementException ex)
        {
            throw new InvalidOperationException($"팬 인터페이스 호출에 실패했습니다: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("팬 속도는 관리자 권한으로 실행해야 바꿀 수 있습니다.", ex);
        }
    }
}
