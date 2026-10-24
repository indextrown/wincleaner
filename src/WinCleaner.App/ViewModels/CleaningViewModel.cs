using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Cleaning;
using WinCleaner.Core;
using WinCleaner.Core.Cleaning;
using WinCleaner.Safety;

namespace WinCleaner.App.ViewModels;

/// <summary>디스크 청소 화면. 스캔 → 미리보기 → 선택 → 확인 → 정리 → 결과 순서로만 진행한다 (사양서 3.4).</summary>
public partial class CleaningViewModel : ObservableObject
{
    private readonly IUserDialogs _dialogs;
    private readonly bool _isElevated;
    private readonly string _logDirectory;
    private readonly ChangeJournal _journal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "스캔을 누르면 정리할 수 있는 파일을 찾습니다. 스캔은 아무것도 지우지 않습니다.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private CleanupSummaryViewModel? _summary;

    public CleaningViewModel(IEnumerable<ICleanupRule> rules, IUserDialogs dialogs, bool isElevated, string logDirectory,
        ChangeJournal journal)
    {
        _dialogs = dialogs;
        _isElevated = isElevated;
        _logDirectory = logDirectory;
        _journal = journal;

        Rules = rules.Select(r => new CleanupRuleItemViewModel(r, isElevated)).ToList();
        foreach (var rule in Rules)
            rule.PropertyChanged += OnRuleChanged;

        Groups = Rules.GroupBy(r => r.Category)
            .Select(g => new CleanupRuleGroup(g.Key, g.ToList()))
            .ToList();
    }

    public IReadOnlyList<CleanupRuleItemViewModel> Rules { get; }

    public IReadOnlyList<CleanupRuleGroup> Groups { get; }

    public bool IsIdle => !IsBusy;

    public bool HasSummary => Summary is not null;

    public long SelectedBytes => CleanTargets.Sum(r => r.FoundBytes);

    public string SelectedText => $"선택한 항목 {ByteSize.Format(SelectedBytes)}";

    private IEnumerable<CleanupRuleItemViewModel> CleanTargets =>
        Rules.Where(r => r.IsSelected && r.CanClean && r.FoundCount > 0);

    private bool CanScan() => !IsBusy;

    private bool CanClean() => !IsBusy && CleanTargets.Any();

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken ct)
    {
        IsBusy = true;
        Summary = null;
        try
        {
            foreach (var rule in Rules)
            {
                StatusText = $"스캔 중: {rule.Name}";
                await rule.ScanAsync(ct);
            }

            var found = Rules.Where(r => r.CanClean).Sum(r => r.FoundBytes);
            StatusText = $"스캔을 마쳤습니다. 정리할 수 있는 공간은 {ByteSize.Format(found)}입니다.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "스캔을 취소했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean), IncludeCancelCommand = true)]
    private async Task CleanAsync(CancellationToken ct)
    {
        var targets = CleanTargets.ToList();
        var cautious = targets.Where(t => t.IsCaution).Select(t => t.Name).ToList();
        var message =
            $"선택한 {targets.Count}개 항목에서 {ByteSize.Format(targets.Sum(t => t.FoundBytes))}를 정리합니다.\n\n" +
            "지운 파일은 휴지통을 거치지 않고 바로 삭제되며 되살릴 수 없습니다. 사용 중인 파일은 건너뜁니다.\n\n" +
            (cautious.Count > 0 ? $"주의 항목이 포함되어 있습니다: {string.Join(", ", cautious)}. 미리보기에서 내용을 다시 확인하세요.\n\n" : "") +
            "계속할까요?";
        if (!_dialogs.Confirm("디스크 청소", message))
            return;

        IsBusy = true;
        var startedAt = DateTime.Now;
        var results = new List<(ICleanupRule Rule, CleanResult Result)>();
        try
        {
            foreach (var target in targets)
            {
                if (ct.IsCancellationRequested)
                    break;
                StatusText = $"정리 중: {target.Name}";
                results.Add((target.Rule, await target.CleanAsync(ct)));
            }
        }
        finally
        {
            string? logPath = null;
            try
            {
                logPath = await Task.Run(() => CleanupLog.Write(_logDirectory, results, startedAt, _isElevated));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            var cleanResults = results.Select(r => r.Result).ToList();
            Summary = new CleanupSummaryViewModel(cleanResults, logPath, ct.IsCancellationRequested);
            if (cleanResults.Count > 0)
            {
                try
                {
                    _journal.Add(new ChangeEntry
                    {
                        Kind = ChangeKind.Cleanup,
                        Title = $"디스크 청소: {string.Join(", ", results.Select(r => r.Rule.Name))}",
                        Detail = $"{ByteSize.Format(cleanResults.Sum(r => r.FreedBytes))} 확보 · " +
                                 $"파일 {cleanResults.Sum(r => r.DeletedCount):N0}개 삭제 · 건너뜀 {cleanResults.Sum(r => r.Skipped.Count):N0}개",
                        LogPath = logPath,
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            StatusText = ct.IsCancellationRequested
                ? "정리를 취소했습니다. 취소하기 전까지 정리한 결과입니다."
                : "정리를 마쳤습니다. 남은 파일을 확인하려면 다시 스캔하세요.";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        ScanCancelCommand.Execute(null);
        CleanCancelCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (Summary?.LogPath is { } path)
            _dialogs.OpenFile(path);
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupRuleItemViewModel.IsSelected) or nameof(CleanupRuleItemViewModel.Result))
        {
            OnPropertyChanged(nameof(SelectedBytes));
            OnPropertyChanged(nameof(SelectedText));
            CleanCommand.NotifyCanExecuteChanged();
        }
    }
}

public sealed record CleanupRuleGroup(string Name, IReadOnlyList<CleanupRuleItemViewModel> Rules);

public sealed record PreviewItem(string Path, string SizeText);

public partial class CleanupRuleItemViewModel : ObservableObject
{
    private const int PreviewLimit = 30;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoundBytes), nameof(FoundCount), nameof(FoundText), nameof(WarningText),
        nameof(HasWarning), nameof(PreviewItems), nameof(PreviewHeader), nameof(HasPreview))]
    private ScanResult? _result;

    [ObservableProperty]
    private string? _cleanedText;

    public CleanupRuleItemViewModel(ICleanupRule rule, bool isElevated)
    {
        Rule = rule;
        NeedsElevation = rule.RequiresElevation && !isElevated;
        // 위험도 낮음만 기본으로 선택한다 (사양서 3장).
        IsSelected = rule.Risk == RiskLevel.Low && CanClean;
    }

    public ICleanupRule Rule { get; }
    public string Name => Rule.Name;
    public string Description => Rule.Description;
    public string Category => Rule.Category;
    public bool RequiresElevation => Rule.RequiresElevation;

    /// <summary>위험도 중간 이상. 기본으로 선택하지 않고 "주의" 표시를 붙인다.</summary>
    public bool IsCaution => Rule.Risk >= RiskLevel.Medium;

    /// <summary>관리자 권한이 필요한데 일반 권한으로 실행 중이면 스캔만 하고 정리는 막는다 (사양서 3.4).</summary>
    public bool NeedsElevation { get; }

    public bool CanClean => !NeedsElevation;

    public long FoundBytes => Result?.TotalBytes ?? 0;

    public int FoundCount => Result?.Items.Count ?? 0;

    public string FoundText => Result switch
    {
        null => "",
        { Items.Count: 0 } => "정리할 파일 없음",
        var r => $"{ByteSize.Format(r.TotalBytes)} · 파일 {r.Items.Count:N0}개",
    };

    public string? WarningText
    {
        get
        {
            var lines = new List<string>();
            if (NeedsElevation)
                lines.Add("관리자 권한으로 실행해야 정리할 수 있습니다.");
            if (Result is not null)
                lines.AddRange(Result.Warnings);
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
    }

    public bool HasWarning => WarningText is not null;

    public bool HasPreview => FoundCount > 0;

    public string PreviewHeader => FoundCount > PreviewLimit ? $"큰 파일 {PreviewLimit}개 미리보기" : "파일 미리보기";

    public IReadOnlyList<PreviewItem> PreviewItems => Result is null
        ? []
        : Result.Items.OrderByDescending(i => i.SizeBytes)
            .Take(PreviewLimit)
            .Select(i => new PreviewItem(i.ShownPath, ByteSize.Format(i.SizeBytes)))
            .ToList();

    public async Task ScanAsync(CancellationToken ct)
    {
        CleanedText = null;
        Result = null;
        try
        {
            Result = await Rule.ScanAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Result = new ScanResult(Rule.Id, [], [$"스캔하지 못했습니다: {ex.Message}"]);
        }
    }

    public async Task<CleanResult> CleanAsync(CancellationToken ct)
    {
        var scan = Result ?? throw new InvalidOperationException("스캔하지 않은 규칙은 정리할 수 없습니다.");
        CleanResult result;
        try
        {
            result = await Rule.CleanAsync(scan, ct);
        }
        catch (Exception ex)
        {
            result = new CleanResult(Rule.Id, [], scan.Items.Select(i => new SkippedItem(i.Path, $"오류: {ex.Message}")).ToList());
        }

        // 정리한 뒤의 크기는 다시 스캔해야 정확하다.
        Result = null;
        CleanedText = result.Skipped.Count == 0
            ? $"{ByteSize.Format(result.FreedBytes)} 정리함"
            : $"{ByteSize.Format(result.FreedBytes)} 정리함 · 건너뜀 {result.Skipped.Count:N0}개";
        return result;
    }
}

public sealed class CleanupSummaryViewModel
{
    public CleanupSummaryViewModel(IReadOnlyList<CleanResult> results, string? logPath, bool cancelled)
    {
        var freed = ByteSize.Format(results.Sum(r => r.FreedBytes));
        var deleted = results.Sum(r => r.DeletedCount);
        var skipped = results.Sum(r => r.Skipped.Count);

        Title = cancelled ? $"정리를 중간에 멈췄습니다. {freed}를 확보했습니다." : $"{freed}를 확보했습니다.";
        Detail = skipped == 0
            ? $"파일 {deleted:N0}개를 삭제했습니다."
            : $"파일 {deleted:N0}개를 삭제했습니다. 사용 중이거나 권한이 없는 파일 {skipped:N0}개는 건너뛰었습니다.";
        LogPath = logPath;
    }

    public string Title { get; }
    public string Detail { get; }
    public string? LogPath { get; }
    public bool HasLog => LogPath is not null;
}
