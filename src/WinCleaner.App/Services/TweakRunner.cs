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

    /// <summary>
    /// 아직 되돌리지 않은 이 항목의 변경을 모두 되돌려, WinCleaner가 처음 바꾸기 전 상태로 돌아간다.
    /// 여러 번 바꿨어도(예: 전원 옵션을 연달아 바꿈) 값마다 가장 오래된 기록의 백업을 쓴다.
    /// 나중 기록에만 있는 값(예: 터보 제한이 나중에 백업하기 시작한 최소 프로세서 상태)은 그 기록에서 가져온다.
    /// 되돌릴 기록이 없으면 false.
    /// </summary>
    public async Task<bool> RevertToOriginalAsync(ITweak tweak, CancellationToken ct)
    {
        var pending = journal.Entries
            .Where(e => e.CanRevert && e.TweakId == tweak.Id && e.Backup is not null)
            .OrderBy(e => e.Time)
            .ToList();
        if (pending.Count == 0)
            return false;

        var original = pending[0];
        var merged = new Dictionary<string, string?>();
        foreach (var entry in pending)
        {
            foreach (var (key, value) in entry.Backup!)
                merged.TryAdd(key, value);
        }
        await tweak.RevertAsync(new TweakBackup(tweak.Id, merged), ct);

        var now = DateTime.Now;
        foreach (var entry in pending)
            journal.MarkReverted(entry.Id, now);
        journal.Add(new ChangeEntry
        {
            Kind = ChangeKind.Revert,
            Title = $"되돌림: {tweak.Name}",
            Detail = $"{original.Time:yyyy-MM-dd HH:mm}에 처음 바꾸기 전 상태로 되돌렸습니다 (기록 {pending.Count}개).",
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
