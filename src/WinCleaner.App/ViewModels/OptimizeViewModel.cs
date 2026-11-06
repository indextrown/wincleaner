using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Core;
using WinCleaner.Core.Optimization;
using WinCleaner.Optimizer;
using WinCleaner.Optimizer.Drives;
using WinCleaner.Optimizer.Startup;
using WinCleaner.Safety;

namespace WinCleaner.App.ViewModels;

public enum OptimizeTab
{
    Startup,
    Settings,
    Drives,
}

/// <summary>최적화 화면. 시작 프로그램, 설정(전원·시각 효과·터보 부스트·최대 절전), 드라이브 세 탭으로 나눈다.</summary>
public partial class OptimizeViewModel : ObservableObject
{
    private readonly TweakCatalog _catalog;
    private readonly StartupManager _startup;
    private readonly IDriveService _drives;
    private readonly TweakRunner _runner;
    private readonly IUserDialogs _dialogs;
    private readonly bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupTab), nameof(IsSettingsTab), nameof(IsDrivesTab))]
    private OptimizeTab _tab;

    [ObservableProperty]
    private bool _createRestorePoint = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "바꾼 내용은 모두 기록되며, 이 화면이나 기록 메뉴에서 되돌릴 수 있습니다.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartupSummary))]
    private IReadOnlyList<StartupItemViewModel> _startupItems = [];

    [ObservableProperty]
    private IReadOnlyList<PowerScheme> _powerSchemes = [];

    [ObservableProperty]
    private PowerScheme? _selectedPowerScheme;

    [ObservableProperty]
    private string _powerStatusText = "";

    [ObservableProperty]
    private bool _canRevertPowerPlan;

    [ObservableProperty]
    private IReadOnlyList<DriveItemViewModel> _driveItems = [];

    public OptimizeViewModel(TweakCatalog catalog, IDriveService drives, TweakRunner runner, IUserDialogs dialogs, bool isElevated)
    {
        _catalog = catalog;
        _startup = new StartupManager(catalog.StartupStore);
        _drives = drives;
        _runner = runner;
        _dialogs = dialogs;
        _isElevated = isElevated;
        Tweaks = catalog.Fixed.Select(t => new TweakItemViewModel(t, isElevated)).ToList();

        // 기록 화면에서 되돌려도 이 화면 상태가 맞게 보이도록 한다.
        runner.Journal.Changed += (_, _) => _ = RefreshSettingsAsync();
    }

    public IReadOnlyList<TweakItemViewModel> Tweaks { get; }

    public bool IsIdle => !IsBusy;

    public bool IsStartupTab
    {
        get => Tab == OptimizeTab.Startup;
        set { if (value) Tab = OptimizeTab.Startup; }
    }

    public bool IsSettingsTab
    {
        get => Tab == OptimizeTab.Settings;
        set { if (value) Tab = OptimizeTab.Settings; }
    }

    public bool IsDrivesTab
    {
        get => Tab == OptimizeTab.Drives;
        set { if (value) Tab = OptimizeTab.Drives; }
    }

    public string StartupSummary =>
        $"켜진 항목 {StartupItems.Count(i => i.IsOn)}개 · 꺼진 항목 {StartupItems.Count(i => !i.IsOn)}개. " +
        "끈 항목은 다음에 Windows를 켤 때부터 자동으로 실행되지 않으며, 프로그램은 지우지 않습니다.";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadStartupAsync();
        await RefreshSettingsAsync();
        try
        {
            var drives = await Task.Run(_drives.ReadDrives);
            DriveItems = drives.Select(d => new DriveItemViewModel(d, _isElevated)).ToList();
        }
        catch (Exception ex)
        {
            DriveItems = [];
            StatusText = $"드라이브 정보를 읽지 못했습니다: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleStartupAsync(StartupItemViewModel? item)
    {
        if (item is not { CanChange: true } || IsBusy)
            return;

        IsBusy = true;
        try
        {
            var tweak = new StartupEntryTweak(_catalog.StartupStore, item.Entry.Location, item.Entry.Name);
            if (item.IsOn)
            {
                await _runner.ApplyAsync(tweak, createRestorePoint: false, _ => { }, CancellationToken.None);
                StatusText = $"자동 실행을 껐습니다: {item.DisplayName}";
            }
            else
            {
                await _runner.RevertAsync(tweak, _runner.Journal.LatestRevertible(tweak.Id), CancellationToken.None);
                StatusText = $"자동 실행을 켰습니다: {item.DisplayName}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"바꾸지 못했습니다: {ex.Message}";
        }
        finally
        {
            await LoadStartupAsync();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleTweakAsync(TweakItemViewModel? item)
    {
        if (item is not { CanChange: true } || IsBusy)
            return;

        IsBusy = true;
        try
        {
            if (!item.IsApplied)
            {
                if (!_dialogs.Confirm(item.Name, $"{item.Description}\n\n적용할까요? 바꾼 내용은 기록되며 이 화면이나 기록 메뉴에서 되돌릴 수 있습니다."))
                    return;

                // 복원 지점은 위험도 "주의" 이상 항목에만 만든다. 가벼운 설정마다 만들면 C: 공간만 쓴다.
                var restorePoint = CreateRestorePoint && item.Tweak.Risk >= RiskLevel.Medium;
                var applied = await _runner.ApplyAsync(item.Tweak, restorePoint, s => StatusText = s, CancellationToken.None);
                StatusText = applied ? $"적용했습니다: {item.Name}" : "적용을 그만두었습니다.";
            }
            else
            {
                var entry = _runner.Journal.LatestRevertible(item.Tweak.Id);
                var source = entry is null
                    ? "WinCleaner 기록이 없어 Windows 기본값으로 되돌립니다."
                    : $"{entry.Time:yyyy-MM-dd HH:mm}에 적용하기 전 상태로 되돌립니다.";
                if (!_dialogs.Confirm(item.Name, $"{source}\n\n되돌릴까요?"))
                    return;

                await _runner.RevertAsync(item.Tweak, entry, CancellationToken.None);
                StatusText = $"되돌렸습니다: {item.Name}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"실패했습니다: {ex.Message}";
        }
        finally
        {
            await item.RefreshAsync();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChangePowerPlanAsync()
    {
        if (SelectedPowerScheme is not { } target || IsBusy)
            return;

        // 이미 그 옵션이면 바꾸지도 기록하지도 않는다. 기록하면 "되돌리기"가 같은 옵션으로 돌아가는 기록이 생긴다.
        if (_catalog.PowerSchemes.GetActive() == target.Id)
        {
            StatusText = $"이미 사용 중인 전원 관리 옵션입니다: {target.Name}";
            return;
        }

        IsBusy = true;
        try
        {
            await _runner.ApplyAsync(new PowerPlanTweak(_catalog.PowerSchemes, target), createRestorePoint: false, _ => { }, CancellationToken.None);
            StatusText = $"전원 관리 옵션을 바꿨습니다: {target.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"바꾸지 못했습니다: {ex.Message}";
        }
        finally
        {
            await RefreshSettingsAsync();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevertPowerPlanAsync()
    {
        if (!CanRevertPowerPlan || IsBusy)
            return;

        IsBusy = true;
        try
        {
            await _runner.RevertToOriginalAsync(new PowerPlanTweak(_catalog.PowerSchemes, null), CancellationToken.None);
            StatusText = "전원 관리 옵션을 이전으로 되돌렸습니다.";
        }
        catch (Exception ex)
        {
            StatusText = $"되돌리지 못했습니다: {ex.Message}";
        }
        finally
        {
            await RefreshSettingsAsync();
            IsBusy = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task OptimizeDriveAsync(DriveItemViewModel? drive, CancellationToken ct)
    {
        if (drive is not { CanOptimize: true } || IsBusy)
            return;
        if (!_dialogs.Confirm($"{drive.Details.Letter}: {drive.ActionText}", $"{drive.Explanation}\n\n지금 실행할까요?"))
            return;

        IsBusy = true;
        drive.IsRunning = true;
        drive.ProgressText = "시작하는 중입니다.";
        try
        {
            var result = await _drives.OptimizeAsync(drive.Details.Letter, line => drive.ProgressText = line, ct);
            drive.ProgressText = $"마쳤습니다. {result}";
            _runner.Journal.Add(new ChangeEntry
            {
                Kind = ChangeKind.Maintenance,
                Title = $"드라이브 최적화: {drive.Title}",
                Detail = $"{drive.ActionText} · {result}",
            });
        }
        catch (OperationCanceledException)
        {
            drive.ProgressText = "취소했습니다.";
        }
        catch (Exception ex)
        {
            drive.ProgressText = $"실패했습니다: {ex.Message}";
        }
        finally
        {
            drive.IsRunning = false;
            IsBusy = false;
        }
    }

    private async Task LoadStartupAsync()
    {
        try
        {
            var entries = await Task.Run(() => _startup.List()
                .Select(e => new StartupItemViewModel(e, _isElevated))
                .OrderByDescending(i => i.IsOn)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
            StartupItems = entries;
        }
        catch (Exception ex)
        {
            StatusText = $"시작 프로그램을 읽지 못했습니다: {ex.Message}";
        }
    }

    private async Task RefreshSettingsAsync()
    {
        foreach (var tweak in Tweaks)
            await tweak.RefreshAsync();

        try
        {
            var power = _catalog.PowerSchemes;
            PowerSchemes = power.List();
            var active = power.GetActive();
            SelectedPowerScheme = PowerSchemes.FirstOrDefault(s => s.Id == active);
            PowerStatusText = $"현재: {SelectedPowerScheme?.Name ?? active.ToString()}";
            CanRevertPowerPlan = _runner.Journal.LatestRevertible(PowerPlanTweak.TweakId) is not null;
        }
        catch (Exception ex)
        {
            PowerStatusText = $"전원 관리 옵션을 읽지 못했습니다: {ex.Message}";
        }
    }
}

public partial class TweakItemViewModel : ObservableObject
{
    private readonly bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplied), nameof(StatusText), nameof(ActionText), nameof(CanChange),
        nameof(WarningText), nameof(HasWarning))]
    private TweakState? _state;

    public TweakItemViewModel(ITweak tweak, bool isElevated)
    {
        Tweak = tweak;
        _isElevated = isElevated;
    }

    public ITweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public bool RequiresElevation => Tweak.RequiresElevation;
    public bool IsCaution => Tweak.Risk >= RiskLevel.Medium;
    public bool NeedsElevation => Tweak.RequiresElevation && !_isElevated;

    public bool IsApplied => State?.IsApplied == true;
    public string StatusText => State?.StatusText ?? "상태 확인 중";
    public string ActionText => IsApplied ? "되돌리기" : "적용";
    public bool CanChange => State is { IsSupported: true } && !NeedsElevation;

    public string? WarningText => NeedsElevation
        ? "관리자 권한으로 실행해야 바꿀 수 있습니다."
        : State is { IsSupported: false } ? "이 PC에서는 쓸 수 없는 항목입니다." : null;

    public bool HasWarning => WarningText is not null;

    public async Task RefreshAsync()
    {
        try
        {
            State = await Tweak.GetStateAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            State = new TweakState(false, $"상태를 읽지 못했습니다: {ex.Message}", IsSupported: false);
        }
    }
}

public sealed class StartupItemViewModel(StartupEntry entry, bool isElevated)
{
    public StartupEntry Entry => entry;
    public string DisplayName => entry.DisplayName;
    public string Command => entry.Command;
    public bool IsOn => entry.IsEnabled;
    public string StateText => IsOn ? "켜짐" : "꺼짐";
    public bool CanChange => !entry.IsMachineWide || isElevated;

    public string Details { get; } = entry.Publisher is { } publisher ? $"{publisher} · {entry.LocationText}" : entry.LocationText;

    public string? WarningText => entry.IsMissingFile
        ? "실행 파일이 없습니다. 지운 프로그램이 남긴 항목일 수 있습니다."
        : !CanChange ? "모든 사용자 항목이라 관리자 권한으로 실행해야 바꿀 수 있습니다." : null;

    public bool HasWarning => WarningText is not null;
}

public partial class DriveItemViewModel(DriveDetails details, bool isElevated) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _progressText;

    public DriveDetails Details => details;
    public string Title => $"{details.Letter}: {details.Model}";
    public bool CanOptimize => isElevated;
    public bool IsIdle => !IsRunning;
    public bool IsHealthProblem => details.Health is DiskHealth.Warning or DiskHealth.Unhealthy;

    public string MediaText
    {
        get
        {
            var media = details.Media switch
            {
                MediaKind.Ssd => "SSD",
                MediaKind.Hdd => "HDD",
                _ => "종류 알 수 없음",
            };
            return details.Bus.Length > 0 ? $"{media} · {details.Bus}" : media;
        }
    }

    public string HealthText => details.Health switch
    {
        DiskHealth.Healthy => "상태 정상",
        DiskHealth.Warning => "상태 주의: 중요한 파일을 백업해 두세요",
        DiskHealth.Unhealthy => "상태 이상: 지금 바로 백업하세요",
        _ => "상태 알 수 없음",
    };

    public string ActionText => details.Media switch
    {
        MediaKind.Ssd => "TRIM 실행",
        MediaKind.Hdd => "조각 모음",
        _ => "최적화",
    };

    public string Explanation => details.Media switch
    {
        MediaKind.Ssd => "SSD는 조각 모음이 필요 없습니다. 지운 공간을 SSD에 알려 주는 TRIM만 실행하며 보통 1분 안에 끝납니다.",
        MediaKind.Hdd => "흩어진 파일 조각을 모아 읽기 속도를 높입니다. 디스크 크기와 상태에 따라 몇 분에서 몇 시간이 걸릴 수 있고, 그동안 PC를 써도 됩니다.",
        _ => "Windows가 드라이브 종류에 맞는 최적화를 고릅니다.",
    };
}
