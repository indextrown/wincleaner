using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.Core;
using WinCleaner.Core.Platform;
using WinCleaner.Memory;

namespace WinCleaner.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly Action _openCleaning;

    [ObservableProperty]
    private IReadOnlyList<DriveCardViewModel> _drives = [];

    [ObservableProperty]
    private MemoryCardViewModel? _memory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    private string? _recommendation;

    /// <summary>화면은 메뉴에서 선택될 때마다 <see cref="RefreshCommand"/>로 다시 읽는다.</summary>
    public DashboardViewModel(Action openCleaning)
    {
        _openCleaning = openCleaning;
    }

    public string OsDescription { get; } = SystemInfo.GetOsDescription();

    public string PrivilegeText { get; } = Elevation.IsElevated ? "관리자 권한으로 실행 중" : "일반 권한으로 실행 중";

    public bool HasRecommendation => Recommendation is not null;

    [RelayCommand]
    private void Refresh()
    {
        Drives = SystemInfo.GetFixedDrives().Select(d => new DriveCardViewModel(d)).ToList();
        Memory = new MemoryCardViewModel(MemoryInfo.GetStatus());

        // 시스템 드라이브가 부족하면 그것을 먼저 알린다.
        var low = Drives.Where(d => d.IsLowOnSpace).OrderByDescending(d => d.IsSystemDrive).FirstOrDefault();
        Recommendation = low is null
            ? null
            : $"{low.Letter} 드라이브 여유 공간이 {low.FreePercentText}밖에 남지 않았습니다. 디스크 청소로 공간을 확보하세요.";
    }

    [RelayCommand]
    private void OpenCleaning() => _openCleaning();
}

public sealed class DriveCardViewModel
{
    public DriveCardViewModel(DriveStatus status)
    {
        Letter = status.Name.TrimEnd('\\');
        Title = status.IsSystemDrive ? $"{Letter} (시스템)" : Letter;
        Label = string.IsNullOrWhiteSpace(status.Label) ? "로컬 디스크" : status.Label;
        IsSystemDrive = status.IsSystemDrive;
        IsLowOnSpace = status.IsLowOnSpace;
        UsedPercent = status.UsedRatio * 100;
        FreePercentText = $"{status.FreeRatio * 100:0}%";
        UsageText = $"사용 {ByteSize.Format(status.UsedBytes)} / 전체 {ByteSize.Format(status.TotalBytes)}";
        FreeText = $"남은 공간 {ByteSize.Format(status.FreeBytes)} ({FreePercentText})";
    }

    public string Letter { get; }
    public string Title { get; }
    public string Label { get; }
    public bool IsSystemDrive { get; }
    public bool IsLowOnSpace { get; }
    public double UsedPercent { get; }
    public string FreePercentText { get; }
    public string UsageText { get; }
    public string FreeText { get; }
}

public sealed class MemoryCardViewModel
{
    public MemoryCardViewModel(MemoryStatus status)
    {
        UsedPercent = status.UsedRatio * 100;
        UsedPercentText = $"{UsedPercent:0}% 사용";
        UsageText = $"사용 {ByteSize.Format(status.UsedBytes)} / 전체 {ByteSize.Format(status.TotalBytes)}";
        AvailableText = $"사용 가능 {ByteSize.Format(status.AvailableBytes)}";
    }

    public double UsedPercent { get; }
    public string UsedPercentText { get; }
    public string UsageText { get; }
    public string AvailableText { get; }
}
