using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Safety;

namespace WinCleaner.App.ViewModels;

/// <summary>기록 화면. WinCleaner가 한 일을 모두 보여 주고, 설정 변경은 여기서 되돌린다.</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly ChangeJournal _journal;
    private readonly TweakRunner _runner;
    private readonly TweakCatalog _catalog;
    private readonly IRestorePointService _restorePoints;
    private readonly IUserDialogs _dialogs;
    private readonly bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntries), nameof(IsEmpty))]
    private IReadOnlyList<HistoryEntryViewModel> _entries = [];

    [ObservableProperty]
    private string _restorePointText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "WinCleaner가 한 일을 모두 기록합니다. 설정 변경은 여기서 되돌릴 수 있습니다.";

    public HistoryViewModel(ChangeJournal journal, TweakRunner runner, TweakCatalog catalog,
        IRestorePointService restorePoints, IUserDialogs dialogs, bool isElevated)
    {
        _journal = journal;
        _runner = runner;
        _catalog = catalog;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _isElevated = isElevated;

        journal.Changed += (_, _) => Reload();
        Reload();
        RestorePointText = isElevated
            ? "복원 지점을 확인하는 중입니다."
            : "복원 지점은 관리자 권한으로 실행해야 확인하고 만들 수 있습니다.";
    }

    public bool HasEntries => Entries.Count > 0;
    public bool IsEmpty => !HasEntries;
    public bool IsIdle => !IsBusy;
    public bool CanManageRestorePoints => _isElevated;

    [RelayCommand]
    private async Task RefreshRestorePointsAsync()
    {
        if (!_isElevated)
            return;

        try
        {
            var points = await _restorePoints.ListAsync(CancellationToken.None) ?? [];
            RestorePointText = points.FirstOrDefault() is { } latest
                ? $"최근 복원 지점: {latest.CreatedAt:yyyy-MM-dd HH:mm} · {latest.Description} (모두 {points.Count}개)"
                : "만들어 둔 복원 지점이 없습니다.";
        }
        catch (Exception ex)
        {
            RestorePointText = $"복원 지점을 읽지 못했습니다: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateRestorePointAsync()
    {
        if (!_isElevated || IsBusy)
            return;
        if (!_dialogs.Confirm("시스템 복원 지점", "지금 시스템 복원 지점을 만들까요?\n\n1분 정도 걸릴 수 있고, C: 드라이브 공간을 조금 사용합니다."))
            return;

        IsBusy = true;
        try
        {
            StatusText = "시스템 복원 지점을 만드는 중입니다.";
            var result = await _restorePoints.CreateAsync("WinCleaner: 직접 만든 복원 지점", CancellationToken.None);
            if (result.Outcome == RestorePointOutcome.Created)
                _journal.Add(new ChangeEntry { Kind = ChangeKind.RestorePoint, Title = "시스템 복원 지점 만듦", Detail = "기록 화면에서 직접 만듦" });
            StatusText = result.Message;
            await RefreshRestorePointsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"복원 지점을 만들지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSystemRestore() => _dialogs.OpenFile(Path.Combine(Environment.SystemDirectory, "rstrui.exe"));

    [RelayCommand]
    private void OpenLog(HistoryEntryViewModel? item)
    {
        if (item is { HasLog: true })
            _dialogs.OpenFile(item.Entry.LogPath!);
    }

    [RelayCommand]
    private async Task RevertAsync(HistoryEntryViewModel? item)
    {
        if (item is not { CanRevert: true } || IsBusy || _catalog.Find(item.Entry.TweakId) is not { } tweak)
            return;
        if (!_dialogs.Confirm("되돌리기", $"{item.TimeText}에 적용한 '{item.Entry.Title}'을(를) 적용하기 전 상태로 되돌릴까요?"))
            return;

        IsBusy = true;
        try
        {
            await _runner.RevertAsync(tweak, item.Entry, CancellationToken.None);
            StatusText = $"되돌렸습니다: {item.Entry.Title}";
        }
        catch (Exception ex)
        {
            StatusText = $"되돌리지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Reload()
    {
        Entries = _journal.Entries
            .Select(e =>
            {
                var tweak = _catalog.Find(e.TweakId);
                var canRevert = e.CanRevert && tweak is not null && (!tweak.RequiresElevation || _isElevated);
                return new HistoryEntryViewModel(e, canRevert);
            })
            .ToList();
    }
}

public sealed class HistoryEntryViewModel(ChangeEntry entry, bool canRevert)
{
    public ChangeEntry Entry => entry;
    public bool CanRevert => canRevert;
    public string TimeText => entry.Time.ToString("yyyy-MM-dd HH:mm");
    public string Title => entry.Title;
    public string Detail => entry.Detail;
    public bool HasLog => entry.LogPath is { } path && File.Exists(path);

    public string KindText => entry.Kind switch
    {
        ChangeKind.Cleanup => "정리",
        ChangeKind.Tweak => "설정 변경",
        ChangeKind.Revert => "되돌림",
        ChangeKind.Maintenance => "유지 관리",
        ChangeKind.Recycled => "휴지통으로 보냄",
        ChangeKind.Uninstalled => "프로그램 제거",
        _ => "복원 지점",
    };

    public string Glyph => entry.Kind switch
    {
        ChangeKind.Cleanup => "",
        ChangeKind.Tweak => "",
        ChangeKind.Revert => "",
        ChangeKind.Maintenance => "",
        ChangeKind.Recycled => "",
        _ => "",
    };

    public string StatusText => entry.Kind switch
    {
        ChangeKind.Cleanup => "되돌릴 수 없음 (파일을 바로 삭제함)",
        ChangeKind.Tweak when entry.RevertedAt is { } at => $"{at:yyyy-MM-dd HH:mm}에 되돌림",
        ChangeKind.Tweak => "적용됨",
        ChangeKind.Recycled => "휴지통에서 되살릴 수 있음",
        ChangeKind.Uninstalled => "되돌리려면 다시 설치해야 함",
        _ => "",
    };
}
