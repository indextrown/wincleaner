using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Cleaning.Analysis;
using WinCleaner.Core;
using WinCleaner.Core.Platform;

namespace WinCleaner.App.ViewModels;

public sealed record DriveChoice(string Root, string DisplayText, bool IsSystemDrive);

public sealed record BreadcrumbItem(FolderNode Node, string Name);

/// <summary>용량 분석 화면. 무엇이 드라이브를 차지하는지 보여 주기만 하고 아무것도 바꾸지 않는다.</summary>
public partial class DiskUsageViewModel : ObservableObject
{
    // 하위 폴더가 수만 개인 폴더(예: WinSxS)도 있어 이만큼만 따로 보여 주고 나머지는 한 줄로 묶는다.
    public const int MaxFolderRows = 300;

    private const string IdleText = "분석을 누르면 드라이브의 모든 폴더 크기를 계산합니다. 아무것도 바꾸지 않습니다.";

    private readonly IUserDialogs _dialogs;
    private readonly bool _isElevated;
    private readonly DiskUsageScanner _scanner;
    private long _driveUsedBytes;

    [ObservableProperty]
    private DriveChoice? _selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = IdleText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport), nameof(ShowEmptyState), nameof(UsedText), nameof(AnalyzedText),
        nameof(InaccessibleText), nameof(LargeFiles))]
    private DiskUsageReport? _report;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderRows), nameof(Breadcrumbs), nameof(CanGoUp))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private FolderNode? _currentFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFolders))]
    private bool _showLargeFiles;

    public DiskUsageViewModel(IReadOnlyList<DriveChoice> drives, IUserDialogs dialogs, bool isElevated,
        DiskUsageScanner? scanner = null)
    {
        _dialogs = dialogs;
        _isElevated = isElevated;
        _scanner = scanner ?? new DiskUsageScanner();
        Drives = drives;
        SelectedDrive = drives.FirstOrDefault(d => d.IsSystemDrive) ?? drives.FirstOrDefault();
    }

    public static IReadOnlyList<DriveChoice> FixedDrives() =>
        SystemInfo.GetFixedDrives()
            .Select(d => new DriveChoice(
                d.Name,
                $"{d.Name.TrimEnd('\\')}{(d.IsSystemDrive ? " (시스템)" : "")} · {ByteSize.Format(d.UsedBytes)} 사용",
                d.IsSystemDrive))
            .ToList();

    public IReadOnlyList<DriveChoice> Drives { get; }

    public bool IsIdle => !IsBusy;

    public bool HasReport => Report is not null;

    public bool ShowEmptyState => !HasReport && !IsBusy;

    public bool ShowFolders
    {
        get => !ShowLargeFiles;
        set => ShowLargeFiles = !value;
    }

    public string UsedText => ByteSize.Format(_driveUsedBytes);

    public string AnalyzedText => Report is null
        ? ""
        : $"{ByteSize.Format(Report.Root.SizeBytes)} · 파일 {Report.FileCount:N0}개";

    public string InaccessibleText => Report switch
    {
        null => "",
        { InaccessibleFolders: 0 } => "없음",
        _ when _isElevated => $"{Report.InaccessibleFolders:N0}개",
        _ => $"{Report.InaccessibleFolders:N0}개 · 관리자 권한이면 줄어듭니다",
    };

    public bool CanGoUp => CurrentFolder?.Parent is not null;

    public IReadOnlyList<BreadcrumbItem> Breadcrumbs
    {
        get
        {
            var items = new List<BreadcrumbItem>();
            for (var folder = CurrentFolder; folder is not null; folder = folder.Parent)
                items.Add(new BreadcrumbItem(folder, folder.Parent is null ? folder.Name.TrimEnd('\\') : folder.Name));
            items.Reverse();
            return items;
        }
    }

    public IReadOnlyList<FolderRowViewModel> FolderRows
    {
        get
        {
            if (CurrentFolder is not { } folder)
                return [];

            var total = folder.SizeBytes;
            var rows = folder.Children.Take(MaxFolderRows).Select(c => FolderRowViewModel.ForFolder(c, total)).ToList();

            var rest = folder.Children.Skip(MaxFolderRows).ToList();
            if (rest.Count > 0)
                rows.Add(FolderRowViewModel.ForOthers(rest.Count, rest.Sum(c => c.SizeBytes), total));
            if (folder.OwnFileCount > 0)
                rows.Add(FolderRowViewModel.ForOwnFiles(folder, total));

            return rows.OrderByDescending(r => r.SizeBytes).ToList();
        }
    }

    public IReadOnlyList<LargeFileRowViewModel> LargeFiles =>
        Report?.LargestFiles.Select(f => new LargeFileRowViewModel(f)).ToList() ?? [];

    partial void OnSelectedDriveChanged(DriveChoice? value)
    {
        Report = null;
        CurrentFolder = null;
        StatusText = IdleText;
    }

    [RelayCommand(CanExecute = nameof(IsIdle), IncludeCancelCommand = true)]
    private async Task AnalyzeAsync(CancellationToken ct)
    {
        if (SelectedDrive is not { } drive)
            return;

        IsBusy = true;
        Report = null;
        CurrentFolder = null;
        ShowLargeFiles = false;

        var progress = new Progress<DiskUsageProgress>(p =>
        {
            // 늦게 도착한 진행 알림이 완료 문구를 덮지 않게 한다.
            if (IsBusy)
                StatusText = $"분석 중: 파일 {p.Files:N0}개 · {ByteSize.Format(p.Bytes)} · {p.CurrentFolder}";
        });

        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(drive.Root)!);
            _driveUsedBytes = driveInfo.TotalSize - driveInfo.TotalFreeSpace;

            var report = await _scanner.ScanAsync(drive.Root, progress, ct);
            Report = report;
            CurrentFolder = report.Root;
            StatusText = $"분석을 마쳤습니다 ({report.Duration.TotalSeconds:N0}초). 폴더 이름을 누르면 안으로 들어갑니다.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "분석을 취소했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => AnalyzeCancelCommand.Execute(null);

    [RelayCommand]
    private void Open(FolderRowViewModel? row)
    {
        if (row is { CanOpen: true, Node: { } node })
            CurrentFolder = node;
    }

    [RelayCommand]
    private void GoTo(FolderNode? folder)
    {
        if (folder is not null)
            CurrentFolder = folder;
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp() => CurrentFolder = CurrentFolder?.Parent;

    [RelayCommand]
    private void ShowInExplorer(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _dialogs.ShowInExplorer(path);
    }
}

public sealed class FolderRowViewModel
{
    private FolderRowViewModel(FolderNode? node, string name, string? path, long sizeBytes, long totalBytes,
        string detail, string? hint, string glyph, bool canOpen)
    {
        Node = node;
        Name = name;
        Path = path;
        SizeBytes = sizeBytes;
        Percent = totalBytes == 0 ? 0 : sizeBytes * 100.0 / totalBytes;
        Detail = detail;
        Hint = hint;
        Glyph = glyph;
        CanOpen = canOpen;
    }

    public FolderNode? Node { get; }
    public string Name { get; }
    public string? Path { get; }
    public long SizeBytes { get; }
    public double Percent { get; }
    public string Detail { get; }
    public string? Hint { get; }
    public string Glyph { get; }
    public bool CanOpen { get; }

    public string SizeText => ByteSize.Format(SizeBytes);
    public string PercentText => $"{Percent:0.#}%";
    public bool HasPath => Path is not null;
    public bool HasHint => Hint is not null;

    public static FolderRowViewModel ForFolder(FolderNode folder, long totalBytes) => new(
        folder, folder.Name, folder.FullPath, folder.SizeBytes, totalBytes,
        folder.IsInaccessible ? "열 수 없음 (크기에 들어가지 않음)" : $"파일 {folder.FileCount:N0}개",
        KnownLocations.Describe(folder.FullPath), "", canOpen: folder.Children.Count > 0);

    public static FolderRowViewModel ForOwnFiles(FolderNode folder, long totalBytes) => new(
        null, "이 폴더에 바로 있는 파일", folder.FullPath, folder.OwnFilesBytes, totalBytes,
        $"파일 {folder.OwnFileCount:N0}개", null, "", canOpen: false);

    public static FolderRowViewModel ForOthers(int count, long sizeBytes, long totalBytes) => new(
        null, $"나머지 작은 폴더 {count:N0}개", null, sizeBytes, totalBytes, "", null, "", canOpen: false);
}

public sealed class LargeFileRowViewModel(LargeFile file)
{
    public string Path => file.Path;
    public string Name => System.IO.Path.GetFileName(file.Path);
    public string Folder => System.IO.Path.GetDirectoryName(file.Path) ?? "";
    public string SizeText => ByteSize.Format(file.SizeBytes);
    public string ModifiedText => file.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public string? Hint { get; } = KnownLocations.DescribeNearest(file.Path);
    public bool HasHint => Hint is not null;
}
