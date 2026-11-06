using System.Globalization;
using System.Runtime.InteropServices;
using WinCleaner.Core;
using WinCleaner.Core.Optimization;

namespace WinCleaner.Optimizer;

/// <summary>전원 관리 옵션마다 저장된 "최대 프로세서 상태"(AC=전원 연결, DC=배터리). 테스트에서 실제 설정을 건드리지 않도록 분리했다.</summary>
public interface IProcessorStateSettings
{
    IReadOnlyList<Guid> Schemes();

    (uint Ac, uint Dc) Read(Guid scheme);

    void Write(Guid scheme, uint ac, uint dc);

    /// <summary>바꾼 값이 지금 쓰는 전원 관리 옵션에 바로 반영되게 한다.</summary>
    void ApplyActive();
}

/// <summary>
/// 터보 부스트 제한 (사양서 3.3 <c>turbo.limit</c>). 모든 전원 관리 옵션의 최대 프로세서 상태를 99%로 낮춘다.
/// Intel CPU는 100%일 때만 터보 부스트를 쓰므로, 99%면 기본 속도(i7-8750H는 2.2GHz)를 넘지 않아 온도가 크게 내려간다.
/// 게임 모드가 전원 관리 옵션을 바꿔도 제한이 유지되도록 옵션 하나가 아니라 전부에 적용한다.
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
        "최대 프로세서 상태를 99%로 낮춰 터보 부스트를 끕니다. 최고 성능은 조금 줄지만 CPU 온도가 크게 내려가고, 너무 뜨거워 속도가 오르내리는 버벅임이 줄어듭니다. 모든 전원 관리 옵션에 적용됩니다.";
    public RiskLevel Risk => RiskLevel.Low;
    public bool RequiresElevation => false;
    public bool RequiresRestart => false;

    public Task<TweakState> GetStateAsync(CancellationToken ct)
    {
        var values = settings.Schemes().Select(settings.Read).ToList();
        var limited = values.Count > 0 && values.All(v => v.Ac <= LimitPercent && v.Dc <= LimitPercent);
        var highest = values.Count == 0 ? 100 : values.Max(v => Math.Max(v.Ac, v.Dc));
        return Task.FromResult(new TweakState(limited,
            limited ? $"터보 제한됨 · 최대 프로세서 상태 {highest}%" : $"터보 켜짐 · 최대 프로세서 상태 {highest}%"));
    }

    public Task<TweakBackup> ApplyAsync(CancellationToken ct)
    {
        var backup = new Dictionary<string, string?>();
        foreach (var scheme in settings.Schemes())
        {
            var (ac, dc) = settings.Read(scheme);
            backup[$"{scheme}:ac"] = ac.ToString(CultureInfo.InvariantCulture);
            backup[$"{scheme}:dc"] = dc.ToString(CultureInfo.InvariantCulture);
            settings.Write(scheme, Math.Min(ac, LimitPercent), Math.Min(dc, LimitPercent));
        }
        settings.ApplyActive();
        return Task.FromResult(new TweakBackup(Id, backup));
    }

    public Task RevertAsync(TweakBackup? backup, CancellationToken ct)
    {
        if (backup is not null && backup.TweakId != Id)
            throw new ArgumentException($"다른 항목({backup.TweakId})의 백업입니다.", nameof(backup));

        // 기록 파일의 값은 0~100일 때만 쓰고, 없거나 이상하면 Windows 기본값(100%)으로 둔다.
        foreach (var scheme in settings.Schemes())
            settings.Write(scheme, Restore(backup, $"{scheme}:ac"), Restore(backup, $"{scheme}:dc"));
        settings.ApplyActive();
        return Task.CompletedTask;
    }

    private static uint Restore(TweakBackup? backup, string key) =>
        uint.TryParse(backup?.Values.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value <= 100
            ? value
            : 100;
}

/// <summary>powrprof.dll로 최대 프로세서 상태(PROCTHROTTLEMAX)를 읽고 쓴다.</summary>
public sealed partial class PowrProfProcessorState : IProcessorStateSettings
{
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    private readonly PowrProfSchemes _schemes = new();

    public IReadOnlyList<Guid> Schemes() => _schemes.List().Select(s => s.Id).ToList();

    public (uint Ac, uint Dc) Read(Guid scheme)
    {
        Check(PowerReadACValueIndex(0, in scheme, in ProcessorSubgroup, in MaxProcessorState, out var ac), "PowerReadACValueIndex");
        Check(PowerReadDCValueIndex(0, in scheme, in ProcessorSubgroup, in MaxProcessorState, out var dc), "PowerReadDCValueIndex");
        return (ac, dc);
    }

    public void Write(Guid scheme, uint ac, uint dc)
    {
        Check(PowerWriteACValueIndex(0, in scheme, in ProcessorSubgroup, in MaxProcessorState, ac), "PowerWriteACValueIndex");
        Check(PowerWriteDCValueIndex(0, in scheme, in ProcessorSubgroup, in MaxProcessorState, dc), "PowerWriteDCValueIndex");
    }

    // 지금 쓰는 옵션은 다시 활성화해야 바꾼 값이 반영된다.
    public void ApplyActive() => _schemes.SetActive(_schemes.GetActive());

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
