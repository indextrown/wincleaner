using System.Management;

namespace WinCleaner.Optimizer.Fans;

/// <param name="Values">읽기 메서드 이름과 돌려받은 원래 값.</param>
public sealed record FanReading(IReadOnlyList<(string Name, uint Value)> Values)
{
    public uint? this[string name]
    {
        get
        {
            foreach (var (method, value) in Values)
            {
                if (method == name)
                    return value;
            }
            return null;
        }
    }

    public int? FanCount => this["GetFANCount"] is { } count and > 0 and < 8 ? (int)count : null;

    // 아래 해석은 2026-11-07 한성 X58K(Clevo N857)에서 2분 동안 기록해 확인했다 (사양서 5.10).
    // Fan1Info 바이트 2가 Windows 열 센서(97.1°C)와 같은 96~97이었고, 바이트 0은 내내 255(최대)였다.

    /// <summary>CPU 팬 속도(%). Fan1Info의 바이트 0(0~255).</summary>
    public int? FanPercent => DutyPercent(this["Fan1Info"]);

    /// <summary>CPU 온도(°C). Fan1Info의 바이트 2. 말이 안 되는 값이면 null.</summary>
    public int? CpuTemperature
    {
        get
        {
            if (this["Fan1Info"] is not { } info)
                return null;
            var celsius = (int)((info >> 16) & 0xFF);
            return celsius is >= 10 and <= 120 ? celsius : null;
        }
    }

    /// <summary>CPU 팬 회전 수(RPM). GetFan12RPM 아래 16비트가 회전 주기 값이고, Clevo 기기의 환산식은 2,156,220 ÷ 값이다.
    /// 멈췄거나 알 수 없으면 null.</summary>
    public int? FanRpm => Rpm(this["GetFan12RPM"] is { } raw ? raw & 0xFFFF : null);

    /// <summary>GPU 팬 속도(%). Fan2Info의 바이트 0. 팬 2가 GPU 팬인 기기가 많지만, 팬이 1개인 기기도 있다.</summary>
    public int? GpuFanPercent => DutyPercent(this["Fan2Info"]);

    /// <summary>GPU 팬 회전 수(RPM). GetFan12RPM 위 16비트.</summary>
    public int? GpuFanRpm => Rpm(this["GetFan12RPM"] is { } raw ? raw >> 16 : null);

    /// <summary>GPU 팬이 있어 보이는지. 이 PC(한성 X58K)는 팬 수 1, 팬 2 속도 0이라 없음으로 나온다.</summary>
    public bool HasGpuFan => FanCount >= 2 || GpuFanRpm is not null || GpuFanPercent > 0;

    private static int? DutyPercent(uint? info) =>
        info is { } value and not 0xFFFFFFFF ? (int)Math.Round((value & 0xFF) * 100 / 255.0) : null;

    private static int? Rpm(uint? period)
    {
        if (period is not { } value || value == 0)
            return null;
        var rpm = (int)(2_156_220 / value);
        return rpm is >= 300 and <= 10_000 ? rpm : null;
    }

    /// <summary>진단용 원래 값. 제어 기능을 만들기 전에 Control Center 화면과 맞춰 보는 데 쓴다.</summary>
    public string RawText => string.Join(Environment.NewLine,
        Values.Select(v => $"{v.Name,-18} {v.Value,10}  0x{v.Value:X8}  [{string.Join(",", BitConverter.GetBytes(v.Value))}]"));
}

/// <summary>팬 상태를 읽는 부분. 테스트에서 실제 하드웨어를 건드리지 않도록 분리했다.</summary>
public interface IFanMonitor
{
    /// <summary>팬 정보를 읽는다. 이 PC에 지원하는 인터페이스가 없거나 권한이 없으면 null.</summary>
    FanReading? Read();
}

/// <summary>
/// Clevo 계열 노트북(한성 등)의 제조사 WMI 인터페이스로 팬 정보를 읽는다. 읽기(Get·Info) 메서드만 부른다.
/// 팬 속도를 바꾸는 것은 <see cref="ClevoFanControl"/>이 맡는다.
/// </summary>
public sealed class ClevoFanMonitor : IFanMonitor
{
    private static readonly string[] ReadMethods =
    [
        "GetFANCount", "Fan1Info", "Fan2Info", "GetFan12RPM", "GetCPUFANControl",
    ];

    public FanReading? Read()
    {
        try
        {
            return ClevoWmi.Use(instance =>
            {
                var values = new List<(string, uint)>();
                foreach (var method in ReadMethods)
                {
                    if (ClevoWmi.Read(instance, method) is { } data)
                        values.Add((method, data));
                }
                return new FanReading(values);
            });
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 클래스가 없는 PC(다른 제조사)이거나 관리자 권한이 아니다.
            return null;
        }
    }
}
