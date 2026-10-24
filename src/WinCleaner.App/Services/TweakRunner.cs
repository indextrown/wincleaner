using WinCleaner.Core.Optimization;
using WinCleaner.Safety;

namespace WinCleaner.App.Services;

/// <summary>
/// 설정 변경의 안전 순서를 한곳에서 지킨다 (사양서 3.4): 복원 지점 → 적용 → 기록, 되돌리기 → 기록.
/// 최적화 화면과 기록 화면이 같이 쓴다.
/// </summary>
public sealed class TweakRunner(ChangeJournal journal, IRestorePointService restorePoints, IUserDialogs dialogs)
{
    public ChangeJournal Journal => journal;

    /// <summary>적용했으면 true, 사용자가 중간에 그만두면 false.</summary>
    public async Task<bool> ApplyAsync(ITweak tweak, bool createRestorePoint, Action<string> status, CancellationToken ct)
    {
        var restoreNote = "복원 지점 없이 적용했습니다.";
        if (createRestorePoint)
        {
            status("시스템 복원 지점을 만드는 중입니다. 1분 정도 걸릴 수 있습니다.");
            var result = await restorePoints.CreateAsync($"WinCleaner: {tweak.Name}", ct);
            if (result.Outcome == RestorePointOutcome.Created)
                journal.Add(new ChangeEntry { Kind = ChangeKind.RestorePoint, Title = "시스템 복원 지점 만듦", Detail = $"{tweak.Name} 적용 전" });

            if (!result.IsAvailable
                && !dialogs.Confirm("시스템 복원 지점",
                    $"{result.Message}\n\n복원 지점 없이 계속할까요? 바꾼 내용은 기록되어 WinCleaner에서 되돌릴 수 있습니다."))
                return false;

            restoreNote = result.Message;
        }

        status($"적용 중: {tweak.Name}");
        var backup = await tweak.ApplyAsync(ct);
        journal.Add(new ChangeEntry
        {
            Kind = ChangeKind.Tweak,
            Title = tweak.Name,
            Detail = restoreNote,
            TweakId = tweak.Id,
            Backup = backup.Values,
        });
        return true;
    }

    /// <summary><paramref name="entry"/>의 백업으로 되돌린다. 기록이 없으면 Windows 기본값으로 되돌린다.</summary>
    public async Task RevertAsync(ITweak tweak, ChangeEntry? entry, CancellationToken ct)
    {
        var backup = entry?.Backup is { } values ? new TweakBackup(tweak.Id, values) : null;
        await tweak.RevertAsync(backup, ct);

        if (entry is not null)
            journal.MarkReverted(entry.Id, DateTime.Now);
        journal.Add(new ChangeEntry
        {
            Kind = ChangeKind.Revert,
            Title = $"되돌림: {tweak.Name}",
            Detail = entry is null
                ? "기록이 없어 Windows 기본값으로 되돌렸습니다."
                : $"{entry.Time:yyyy-MM-dd HH:mm}에 적용한 변경을 되돌렸습니다.",
        });
    }
}
