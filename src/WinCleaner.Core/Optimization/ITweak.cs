namespace WinCleaner.Core.Optimization;

/// <summary>
/// 시스템 최적화 항목 하나 (사양서 3.3). 적용과 되돌리기를 반드시 한 쌍으로 구현한다.
/// 적용할 때 바꾸기 전 값을 <see cref="TweakBackup"/>으로 돌려주고, 변경 기록에 남겨 되돌릴 때 쓴다 (사양서 3.4).
/// </summary>
public interface ITweak
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    RiskLevel Risk { get; }
    bool RequiresElevation { get; }
    bool RequiresRestart { get; }

    Task<TweakState> GetStateAsync(CancellationToken ct);

    /// <summary>적용하고, 바꾸기 전 값을 돌려준다.</summary>
    Task<TweakBackup> ApplyAsync(CancellationToken ct);

    /// <summary>
    /// 백업한 값으로 되돌린다. 백업이 없으면 Windows 기본값으로 되돌린다.
    /// 백업은 사용자 폴더의 기록 파일에서 읽어 오므로, 구현은 값을 그대로 쓰지 말고 허용된 값인지 검사해야 한다.
    /// </summary>
    Task RevertAsync(TweakBackup? backup, CancellationToken ct);
}

/// <param name="IsApplied">이 항목이 지금 적용된 상태인지.</param>
/// <param name="StatusText">사용자에게 보여 줄 현재 상태 (예: "켜짐 · hiberfil.sys 6.3 GB").</param>
/// <param name="IsSupported">이 PC에서 쓸 수 있는 항목인지.</param>
public sealed record TweakState(bool IsApplied, string StatusText, bool IsSupported = true);

public sealed record TweakBackup(string TweakId, IReadOnlyDictionary<string, string?> Values);
