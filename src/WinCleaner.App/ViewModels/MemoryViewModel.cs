using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Core;
using WinCleaner.Memory;
using WinCleaner.Safety;

namespace WinCleaner.App.ViewModels;

/// <summary>
/// 메모리 화면 (사양서 3.2). 원인 파악이 우선이고 정리는 사용자가 고른 것만 버튼으로 한다.
/// 화면이 보이는 동안에만 몇 초마다 다시 읽고, 자동으로 정리하지는 않는다.
/// </summary>
public partial class MemoryViewModel : ObservableObject
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly IMemoryService _memory;
    private readonly IUserDialogs _dialogs;
    private readonly ChangeJournal _journal;
    private readonly bool _isElevated;
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _autoRefresh;
    private bool _refreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsedText), nameof(UsedPercent), nameof(AvailableText), nameof(CacheText), nameof(CommitText))]
    private SystemMemory? _system;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(TrimSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = $"{RefreshInterval.TotalSeconds:0}초마다 새로 읽습니다. 프로그램을 골라 작업 집합을 비울 수 있습니다.";

    public MemoryViewModel(IMemoryService memory, IUserDialogs dialogs, ChangeJournal journal, bool isElevated)
    {
        _memory = memory;
        _dialogs = dialogs;
        _journal = journal;
        _isElevated = isElevated;
    }

    public ObservableCollection<ProcessGroupViewModel> Groups { get; } = [];

    public bool IsIdle => !IsBusy;
    public bool CanPurgeStandby => _isElevated;
    public bool ShowPurgeElevationWarning => !_isElevated;

    public string UsedText => System is { } s ? $"{ByteSize.Format(s.UsedBytes)} / {ByteSize.Format(s.TotalBytes)}" : "";
    public double UsedPercent => (System?.UsedRatio ?? 0) * 100;
    public string AvailableText => System is { } s ? ByteSize.Format(s.AvailableBytes) : "";
    public string CacheText => System is { } s ? ByteSize.Format(s.CacheBytes) : "";
    public string CommitText => System is { } s ? $"{ByteSize.Format(s.CommitBytes)} / {ByteSize.Format(s.CommitLimitBytes)}" : "";

    public int SelectedCount => Groups.Count(g => g.IsSelected);
    public string SelectedText => SelectedCount == 0 ? "정리할 프로그램을 고르세요" : $"선택한 프로그램 {SelectedCount}개";

    /// <summary>화면에 들어오면 주기적으로 새로 읽기 시작한다.</summary>
    public void Activate()
    {
        Deactivate();
        _autoRefresh = new CancellationTokenSource();
        _ = AutoRefreshAsync(_autoRefresh.Token);
    }

    /// <summary>다른 화면으로 가면 새로 읽기를 멈춘다.</summary>
    public void Deactivate()
    {
        _autoRefresh?.Cancel();
        _autoRefresh = null;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var (system, groups) = await Task.Run(() =>
            {
                var groups = ProcessGrouping.ByName(_memory.ReadProcesses());
                foreach (var group in groups.Where(g => !_descriptions.ContainsKey(g.Name)))
                    _descriptions[group.Name] = _memory.DescribeProcess(group.Processes[0].Id);
                return (_memory.ReadSystem(), groups);
            });

            System = system;
            Sync(groups, system.TotalBytes);
        }
        catch (Exception ex)
        {
            StatusText = $"메모리 정보를 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool CanTrim() => !IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanTrim))]
    private async Task TrimSelectedAsync()
    {
        var selected = Groups.Where(g => g.IsSelected).ToList();
        var names = string.Join(", ", selected.Take(5).Select(g => g.Title)) + (selected.Count > 5 ? $" 외 {selected.Count - 5}개" : "");
        if (!_dialogs.Confirm("작업 집합 비우기",
                $"{names}의 작업 집합을 비웁니다.\n\n" +
                "프로그램은 종료되지 않습니다. '사용 중' 숫자는 줄지만, 그 프로그램을 다시 쓸 때 필요한 부분을 디스크에서 다시 읽어 와 잠깐 느려질 수 있습니다. " +
                "메모리가 정말 부족할 때만 쓰고, 반복할 필요는 없습니다.\n\n계속할까요?"))
            return;

        IsBusy = true;
        try
        {
            var ids = selected.SelectMany(g => g.ProcessIds).ToList();
            var result = await Task.Run(() => _memory.TrimWorkingSets(ids));

            StatusText = result.Failed == 0
                ? $"프로세스 {result.Trimmed}개의 작업 집합을 비워 {ByteSize.Format(result.FreedBytes)}를 내보냈습니다."
                : $"프로세스 {result.Trimmed}개에서 {ByteSize.Format(result.FreedBytes)}를 내보냈습니다. " +
                  $"{result.Failed}개는 권한이 없어 건너뛰었습니다" + (_isElevated ? "." : " (관리자 권한이면 더 많이 정리할 수 있습니다).");

            TryRecord(new ChangeEntry
            {
                Kind = ChangeKind.Maintenance,
                Title = $"작업 집합 비우기: {names}",
                Detail = $"{ByteSize.Format(result.FreedBytes)} · 프로세스 {result.Trimmed}개 정리, {result.Failed}개 건너뜀",
            });

            foreach (var group in selected)
                group.IsSelected = false;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task PurgeStandbyAsync()
    {
        if (!_isElevated || IsBusy)
            return;
        if (!_dialogs.Confirm("대기 목록 비우기",
                "대기 목록(최근에 쓴 파일과 프로그램을 담아 둔 캐시)을 비웁니다.\n\n" +
                "게임이 주기적으로 끊기는 것처럼 특별한 경우에만 필요합니다. 비운 직후에는 프로그램과 파일을 여는 속도가 잠깐 느려질 수 있습니다.\n\n계속할까요?"))
            return;

        IsBusy = true;
        try
        {
            var result = await Task.Run(_memory.PurgeStandbyList);
            StatusText = result.Succeeded
                ? $"{result.Message} 캐시가 {ByteSize.Format(result.FreedCacheBytes)} 줄었습니다."
                : result.Message;
            if (result.Succeeded)
            {
                TryRecord(new ChangeEntry
                {
                    Kind = ChangeKind.Maintenance,
                    Title = "대기 목록 비우기",
                    Detail = $"캐시 {ByteSize.Format(result.FreedCacheBytes)} 줄어듦",
                });
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task AutoRefreshAsync(CancellationToken ct)
    {
        await RefreshAsync();
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!IsBusy)
                    await RefreshAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 목록을 통째로 바꾸지 않고 항목을 갱신·이동한다. 통째로 바꾸면 몇 초마다 스크롤과 선택이 풀린다.
    /// </summary>
    private void Sync(IReadOnlyList<ProcessGrouping.Group> groups, long totalBytes)
    {
        var index = 0;
        foreach (var group in groups)
        {
            var existing = Groups.FirstOrDefault(g => string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new ProcessGroupViewModel(group.Name, _descriptions.GetValueOrDefault(group.Name));
                existing.PropertyChanged += OnGroupChanged;
                Groups.Insert(index, existing);
            }
            else if (Groups.IndexOf(existing) is var current && current != index)
            {
                Groups.Move(current, index);
            }

            existing.Update(group, totalBytes);
            index++;
        }

        // 위에서 남은 것만 앞쪽에 모았으므로 뒤에 남은 항목은 끝난 프로그램이다.
        for (var i = Groups.Count - 1; i >= index; i--)
        {
            Groups[i].PropertyChanged -= OnGroupChanged;
            Groups.RemoveAt(i);
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedText));
        TrimSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProcessGroupViewModel.IsSelected))
            return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedText));
        TrimSelectedCommand.NotifyCanExecuteChanged();
    }

    private void TryRecord(ChangeEntry entry)
    {
        try
        {
            _journal.Add(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public partial class ProcessGroupViewModel(string name, string? description) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkingSetText), nameof(PrivateText), nameof(Subtitle), nameof(Percent))]
    private ProcessGrouping.Group? _group;

    private long _totalBytes;

    public string Name => name;
    public string Title => description ?? name;
    public IReadOnlyList<int> ProcessIds => Group?.Processes.Select(p => p.Id).ToList() ?? [];

    public string Subtitle => Group is { Count: > 1 } g ? $"{name} · 프로세스 {g.Count}개" : name;
    public string WorkingSetText => ByteSize.Format(Group?.WorkingSetBytes ?? 0);
    public string PrivateText => $"전용 {ByteSize.Format(Group?.PrivateBytes ?? 0)}";
    public double Percent => _totalBytes == 0 ? 0 : (Group?.WorkingSetBytes ?? 0) * 100.0 / _totalBytes;

    public void Update(ProcessGrouping.Group group, long totalBytes)
    {
        _totalBytes = totalBytes;
        Group = group;
    }
}
