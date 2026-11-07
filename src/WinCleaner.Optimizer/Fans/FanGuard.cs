using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCleaner.Optimizer.Fans;

/// <summary>사용자 지정 팬 모드. 속도는 백분율이다.</summary>
public sealed record FanMode(string Name, int CpuPercent, int GpuPercent)
{
    /// <summary>이보다 낮게는 돌리지 않는다. 리눅스 Clevo 드라이버도 25%를 "팬이 도는 최저 속도"로 쓴다.</summary>
    public const int MinPercent = 25;

    public static FanMode Max { get; } = new("최대", 100, 100);

    [JsonIgnore]
    public string Summary => $"CPU 팬 {CpuPercent}% · GPU 팬 {GpuPercent}%";

    public FanMode Clamped() => this with
    {
        CpuPercent = Math.Clamp(CpuPercent, MinPercent, 100),
        GpuPercent = Math.Clamp(GpuPercent, MinPercent, 100),
    };
}

/// <summary>사용자가 저장한 팬 모드 목록 (%LOCALAPPDATA%\WinCleaner\fan-modes.json).</summary>
public sealed class FanModeStore(string path)
{
    public const int MaxModes = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner", "fan-modes.json");

    public IReadOnlyList<FanMode> Load()
    {
        if (!File.Exists(path))
            return [];
        try
        {
            return Clean(JsonSerializer.Deserialize<List<FanMode>>(File.ReadAllText(path), JsonOptions) ?? []);
        }
        catch (JsonException)
        {
            // 망가진 파일은 무시한다. 다음에 저장할 때 새로 쓴다.
            return [];
        }
    }

    public void Save(IEnumerable<FanMode> modes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Clean(modes), JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    // 파일을 손으로 고쳐도 이상한 값이 팬에 그대로 가지 않게 한다.
    private static List<FanMode> Clean(IEnumerable<FanMode> modes) => modes
        .Where(m => !string.IsNullOrWhiteSpace(m.Name))
        .Select(m => m.Clamped() with { Name = m.Name.Trim() })
        .DistinctBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
        .Take(MaxModes)
        .ToList();
}

public enum FanControlState
{
    /// <summary>노트북 펌웨어가 팬을 정한다 (기본).</summary>
    Auto,

    /// <summary>사용자가 고른 속도로 돌린다.</summary>
    Manual,

    /// <summary>과열이라 잠시 펌웨어 자동 제어로 돌려 두었다. 식으면 사용자 속도로 돌아간다.</summary>
    Protecting,
}

/// <summary>
/// 사용자 지정 팬 속도를 걸고, 걸려 있는 동안 지켜본다 (사양서 3.2.1 안전장치).
/// <list type="bullet">
/// <item>CPU가 <see cref="ProtectAtCelsius"/> 이상이면 펌웨어 자동 제어로 돌리고, <see cref="ResumeBelowCelsius"/> 아래로 식으면 다시 건다.</item>
/// <item>온도를 읽지 못하거나 CPU 팬이 <see cref="StallTimeout"/> 동안 돌지 않으면 자동 제어로 되돌리고 그만둔다.</item>
/// <item>사용자 지정 중에는 표시 파일을 남겨, 앱이 비정상 종료돼도 다음 실행 때 자동 제어로 되돌린다.</item>
/// </list>
/// 스레드에 안전하지 않다. 부르는 쪽이 한 번에 하나씩 부른다.
/// </summary>
public sealed class FanGuard(IFanControl control, string markerPath)
{
    public const int ProtectAtCelsius = 90;
    public const int ResumeBelowCelsius = 80;
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);

    public const string AutoText = "자동 제어: 노트북이 온도에 맞춰 팬 속도를 정합니다.";

    private DateTime? _stallSince;

    public static string DefaultMarkerPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner", "fan-manual.flag");

    /// <summary>걸려 있는 사용자 지정 모드. 자동 제어면 null.</summary>
    public FanMode? Mode { get; private set; }

    public FanControlState State { get; private set; }

    public string StatusText { get; private set; } = AutoText;

    /// <param name="mode">null이면 자동 제어로 되돌린다.</param>
    public void Apply(FanMode? mode)
    {
        if (mode is null)
        {
            control.SetAuto();
            BackToAuto(AutoText);
            return;
        }

        var target = mode.Clamped();
        // 표시를 먼저 남긴다. 적용 도중 앱이 꺼져도 다음 실행 때 자동 제어로 돌아간다.
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, $"{DateTime.Now:O} {target.Name}");
        control.SetDuty(target.CpuPercent, target.GpuPercent);
        Mode = target;
        State = FanControlState.Manual;
        _stallSince = null;
        StatusText = ManualText(target);
    }

    /// <summary>사용자 지정 중일 때 2초마다 부른다.</summary>
    public void Tick(FanReading? reading, DateTime now)
    {
        if (State == FanControlState.Auto || Mode is not { } mode)
            return;

        if (reading?.CpuTemperature is not { } celsius)
        {
            FallBack("CPU 온도를 읽지 못해 자동 제어로 되돌렸습니다.");
            return;
        }

        if (State == FanControlState.Protecting)
        {
            if (celsius < ResumeBelowCelsius)
            {
                control.SetDuty(mode.CpuPercent, mode.GpuPercent);
                State = FanControlState.Manual;
                _stallSince = null;
                StatusText = ManualText(mode);
            }
            return;
        }

        if (celsius >= ProtectAtCelsius && (mode.CpuPercent < 100 || mode.GpuPercent < 100))
        {
            control.SetAuto();
            State = FanControlState.Protecting;
            StatusText = $"과열 보호 중: CPU가 {celsius}°C라 노트북 자동 제어로 돌렸습니다. " +
                         $"{ResumeBelowCelsius}°C 아래로 식으면 '{mode.Name}'을(를) 다시 적용합니다.";
            return;
        }

        // 팬이 실제로 도는지 본다. 값 형식이 다른 기기에서 팬을 멈춘 채로 두지 않기 위해서다.
        if (reading.FanRpm is null)
        {
            _stallSince ??= now;
            if (now - _stallSince >= StallTimeout)
                FallBack("CPU 팬이 도는 것이 확인되지 않아 자동 제어로 되돌렸습니다.");
            return;
        }
        _stallSince = null;

        // 펌웨어가 값을 덮어썼으면 다시 건다.
        if (reading.FanPercent is { } actual && Math.Abs(actual - mode.CpuPercent) > 3)
            control.SetDuty(mode.CpuPercent, mode.GpuPercent);
    }

    /// <summary>앱을 끌 때 부른다. 사용자 지정 중이었으면 자동 제어로 되돌린다.</summary>
    public void Release()
    {
        if (State != FanControlState.Auto)
            control.SetAuto();
        BackToAuto(AutoText);
    }

    /// <summary>지난번에 사용자 지정 중 앱이 비정상 종료됐으면 자동 제어로 되돌린다.</summary>
    public bool RecoverAfterCrash()
    {
        if (!File.Exists(markerPath))
            return false;
        control.SetAuto();
        BackToAuto(AutoText);
        return true;
    }

    private void FallBack(string reason)
    {
        control.SetAuto();
        BackToAuto(reason);
    }

    private void BackToAuto(string status)
    {
        if (File.Exists(markerPath))
            File.Delete(markerPath);
        Mode = null;
        State = FanControlState.Auto;
        _stallSince = null;
        StatusText = status;
    }

    private static string ManualText(FanMode mode) => $"사용자 지정: {mode.Name} · {mode.Summary}";
}
