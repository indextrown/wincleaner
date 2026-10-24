using WinCleaner.Core;
using WinCleaner.Core.Optimization;

namespace WinCleaner.Optimizer.Startup;

/// <summary>
/// 시작 프로그램 하나 끄기 (사양서 3.3 <c>startup.manage</c>). 항목마다 하나씩 만들고, 변경 기록에 남겨 되돌릴 수 있게 한다.
/// </summary>
public sealed class StartupEntryTweak(IStartupStore store, StartupLocation location, string name) : ITweak
{
    private const string IdPrefix = "startup:";
    private const string ApprovalKey = "approval";

    public string Id => $"{IdPrefix}{location}:{name}";
    public string Name => $"시작 프로그램 끄기: {name}";
    public string Description => "Windows를 켤 때 이 프로그램이 자동으로 실행되지 않게 합니다. 프로그램은 지우지 않습니다.";
    public RiskLevel Risk => RiskLevel.Low;
    public bool RequiresElevation => location is not (StartupLocation.UserRun or StartupLocation.UserFolder);

    /// <summary>다음에 로그인할 때부터 적용된다.</summary>
    public bool RequiresRestart => false;

    /// <summary>변경 기록의 ID에서 항목을 되살린다. 기록 화면의 되돌리기에 쓴다.</summary>
    public static bool TryParseId(string id, out StartupLocation location, out string name)
    {
        location = default;
        name = "";
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return false;

        var rest = id[IdPrefix.Length..];
        var separator = rest.IndexOf(':');
        if (separator <= 0 || separator == rest.Length - 1
            || !Enum.TryParse(rest[..separator], out location) || !Enum.IsDefined(location))
            return false;

        name = rest[(separator + 1)..];
        return true;
    }

    public Task<TweakState> GetStateAsync(CancellationToken ct)
    {
        var enabled = StartupApproval.IsEnabled(store.ReadApproval(location, name));
        return Task.FromResult(new TweakState(IsApplied: !enabled, enabled ? "켜짐" : "꺼짐"));
    }

    public Task<TweakBackup> ApplyAsync(CancellationToken ct)
    {
        var before = store.ReadApproval(location, name);
        store.WriteApproval(location, name, StartupApproval.Disabled(DateTime.UtcNow));
        return Task.FromResult(new TweakBackup(Id, new Dictionary<string, string?>
        {
            [ApprovalKey] = before is null ? null : Convert.ToHexString(before),
        }));
    }

    public Task RevertAsync(TweakBackup? backup, CancellationToken ct)
    {
        if (backup is not null && backup.TweakId != Id)
            throw new ArgumentException($"다른 항목({backup.TweakId})의 백업입니다.", nameof(backup));

        // 원래 값이 없었으면(작업 관리자에서 한 번도 바꾸지 않은 항목) 값을 지워 원래대로 둔다.
        if (backup is not null && backup.Values.TryGetValue(ApprovalKey, out var hex) && hex is null)
        {
            store.DeleteApproval(location, name);
            return Task.CompletedTask;
        }

        var restored = TryParseHex(backup?.Values.GetValueOrDefault(ApprovalKey));
        store.WriteApproval(location, name, restored is not null && StartupApproval.IsValid(restored)
            ? restored
            : StartupApproval.Enabled());
        return Task.CompletedTask;
    }

    private static byte[]? TryParseHex(string? hex)
    {
        if (hex is null)
            return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
