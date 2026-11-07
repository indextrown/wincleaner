using System.Globalization;
using System.Runtime.InteropServices;
using WinCleaner.Core;
using WinCleaner.Core.Optimization;

namespace WinCleaner.Optimizer;

/// <summary>
/// 전원 관리 옵션마다 저장된 최대·최소 프로세서 상태(AC=전원 연결, DC=배터리). 테스트에서 실제 설정을 건드리지 않도록 분리했다.
/// </summary>
public interface IProcessorStateSettings
{
    IReadOnlyList<Guid> Schemes();

    /// <summary>최대 프로세서 상태(%).</summary>
    (uint Ac, uint Dc) Read(Guid scheme);

    void Write(Guid scheme, uint ac, uint dc);

    /// <summary>최소 프로세서 상태(%).</summary>
    (uint Ac, uint Dc) ReadMinimum(Guid scheme);

    void WriteMinimum(Guid scheme, uint ac, uint dc);

    /// <summary>바꾼 값이 지금 쓰는 전원 관리 옵션에 바로 반영되게 한다.</summary>
    void ApplyActive();
}

/// <summary>
/// 터보 부스트 제한 (사양서 3.3 <c>turbo.limit</c>). 모든 전원 관리 옵션의 최대 프로세서 상태를 99%로 낮춘다.
/// Intel CPU는 100%일 때만 터보 부스트를 쓰므로, 99%면 기본 속도(i7-8750H는 2.2GHz)를 넘지 않아 온도가 크게 내려간다.
/// 게임 모드가 전원 관리 옵션을 바꿔도 제한이 유지되도록 옵션 하나가 아니라 전부에 적용한다.
/// "최고의 성능"·"고성능"은 최소 프로세서 상태가 100%라 최대만 낮추면 Windows가 최소값을 따라 터보를 그대로 쓴다
/// (2026-11-07 실제로 3.1GHz로 돌았다). 그래서 99%를 넘는 최소값도 99%로 낮춘다.
/// </summary>
public sealed class TurboLimitTweak(IProcessorStateSettings settings) : ITweak
{
    public const uint LimitPercent = 99;

    public TurboLimitTweak() : this(new PowrProfProcessorState())
    {
    }

    public string Id => "turbo.limit";
    public string Name => "터보 부스트 제한";
    public string Description =>
        "최대 프로세서 상태를 99%로 낮춰 터보 부스트를 끕니다. 최고 성능은 조금 줄지만 CPU 온도가 크게 내려가고, 너무 뜨거워 속도가 오르내리는 버벅임이 줄어듭니다. 모든 전원 관리 옵션에 적용되고, 최소 프로세서 상태가 100%인 옵션은 최소값도 99%로 맞춥니다.";
    public RiskLevel Risk => RiskLevel.Low;
    public bool RequiresElevation => false;
    public bool RequiresRestart => false;

    public Task<TweakState> GetStateAsync(CancellationToken ct)
    {
        var schemes = settings.Schemes();
        var max = schemes.Select(settings.Read).ToList();
        var min = schemes.Select(settings.ReadMinimum).ToList();
        var highestMax = max.Count == 0 ? 100 : max.Max(v => Math.Max(v.Ac, v.Dc));
        var highestMin = min.Count == 0 ? 0 : min.Max(v => Math.Max(v.Ac, v.Dc));
        var limited = schemes.Count > 0 && highestMax <= LimitPercent && highestMin <= LimitPercent;

        var text = limited
            ? $"터보 제한됨 · 최대 프로세서 상태 {highestMax}%"
            : highestMax <= LimitPercent
                ? $"터보 켜짐 · 최소 프로세서 상태가 {highestMin}%라 최대 {highestMax}% 제한이 걸리지 않습니다"
                : $"터보 켜짐 · 최대 프로세서 상태 {highestMax}%";
        return Task.FromResult(new TweakState(limited, text));
    }

    public Task<TweakBackup> ApplyAsync(CancellationToken ct)
    {
        var backup = new Dictionary<string, string?>();
        foreach (var scheme in settings.Schemes())
        {
            var (ac, dc) = settings.Read(scheme);
            var (minAc, minDc) = settings.ReadMinimum(scheme);
            backup[$"{scheme}:ac"] = Text(ac);
            backup[$"{scheme}:dc"] = Text(dc);
            backup[$"{scheme}:minac"] = Text(minAc);
            backup[$"{scheme}:mindc"] = Text(minDc);
            settings.Write(scheme, Math.Min(ac, LimitPercent), Math.Min(dc, LimitPercent));
            settings.WriteMinimum(scheme, Math.Min(minAc, LimitPercent), Math.Min(minDc, LimitPercent));
        }
        settings.ApplyActive();
        return Task.FromResult(new TweakBackup(Id, backup));
    }

    public Task RevertAsync(TweakBackup? backup, CancellationToken ct)
    {
        if (backup is not null && backup.TweakId != Id)
            throw new ArgumentException($"다른 항목({backup.TweakId})의 백업입니다.", nameof(backup));

        // 기록 파일의 값은 0~100일 때만 쓴다. 최대값이 없거나 이상하면 Windows 기본값(100%)으로 두고,
        // 최소값은 기록이 있을 때만 되돌린다(최소값을 기록하기 전에 만든 백업도 있다).
        foreach (var scheme in settings.Schemes())
        {
            settings.Write(scheme, Restore(backup, $"{scheme}:ac") ?? 100, Restore(backup, $"{scheme}:dc") ?? 100);
            if (Restore(backup, $"{scheme}:minac") is { } minAc && Restore(backup, $"{scheme}:mindc") is { } minDc)
                settings.WriteMinimum(scheme, minAc, minDc);
        }
        settings.ApplyActive();
        return Task.CompletedTask;
    }

    private static string Text(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static uint? Restore(TweakBackup? backup, string key) =>
        uint.TryParse(backup?.Values.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value <= 100
            ? value
            : null;
}

/// <summary>powrprof.dll로 최대·최소 프로세서 상태(PROCTHROTTLEMAX·PROCTHROTTLEMIN)를 읽고 쓴다.</summary>
public sealed partial class PowrProfProcessorState : IProcessorStateSettings
{
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid MinProcessorState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");

    private readonly PowrProfSchemes _schemes = new();

    public IReadOnlyList<Guid> Schemes() => _schemes.List().Select(s => s.Id).ToList();

    public (uint Ac, uint Dc) Read(Guid scheme) => ReadSetting(scheme, MaxProcessorState);

    public void Write(Guid scheme, uint ac, uint dc) => WriteSetting(scheme, MaxProcessorState, ac, dc);

    public (uint Ac, uint Dc) ReadMinimum(Guid scheme) => ReadSetting(scheme, MinProcessorState);

    public void WriteMinimum(Guid scheme, uint ac, uint dc) => WriteSetting(scheme, MinProcessorState, ac, dc);

    // 지금 쓰는 옵션은 다시 활성화해야 바꾼 값이 반영된다.
    public void ApplyActive() => _schemes.SetActive(_schemes.GetActive());

    private static (uint Ac, uint Dc) ReadSetting(Guid scheme, Guid setting)
    {
        Check(PowerReadACValueIndex(0, in scheme, in ProcessorSubgroup, in setting, out var ac), "PowerReadACValueIndex");
        Check(PowerReadDCValueIndex(0, in scheme, in ProcessorSubgroup, in setting, out var dc), "PowerReadDCValueIndex");
        return (ac, dc);
    }

    private static void WriteSetting(Guid scheme, Guid setting, uint ac, uint dc)
    {
        Check(PowerWriteACValueIndex(0, in scheme, in ProcessorSubgroup, in setting, ac), "PowerWriteACValueIndex");
        Check(PowerWriteDCValueIndex(0, in scheme, in ProcessorSubgroup, in setting, dc), "PowerWriteDCValueIndex");
    }

    private static void Check(uint result, string function)
    {
        if (result != 0)
            throw new InvalidOperationException($"{function} 실패 (오류 코드 {result})");
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadACValueIndex(nint root, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadDCValueIndex(nint root, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteACValueIndex(nint root, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteDCValueIndex(nint root, in Guid scheme, in Guid subgroup, in Guid setting, uint value);
}
