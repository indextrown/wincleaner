using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Core.Optimization;
using WinCleaner.Optimizer;
using WinCleaner.Optimizer.Fans;

namespace WinCleaner.App.ViewModels;

/// <summary>
/// 게임 모드 화면. 게임할 때 쓰는 것을 한곳에 모았다: 전원 관리 옵션 전환, 원클릭 메모리 정리, CPU 온도·팬, 팬 속도 조절, 터보 부스트 제한.
/// 무거운 작업은 백그라운드에서 한다. 팬 상태는 화면이 보이는 동안 2초마다 읽고,
/// 사용자 지정 팬 속도가 걸려 있으면 안전장치가 돌도록 다른 화면에 있어도 계속 읽는다.
/// </summary>
public partial class GameModeViewModel : ObservableObject
{
    public static readonly TimeSpan FanRefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>이 온도부터 과열 경고를 띄운다. 인텔 노트북 CPU는 100°C 언저리에서 스스로 속도를 낮춘다.</summary>
    public const int HotTemperature = 90;

    /// <summary>이 온도부터 "뜨거운 편"으로 표시한다.</summary>
    public const int WarmTemperature = 80;

    private static readonly string[] PreferredPlanNames = ["최고의 성능", "Ultimate Performance", "고성능", "High performance"];

    private readonly TweakRunner _runner;
    private readonly IPowerSchemes _power;
    private readonly QuickOptimizeRunner _quick;
    private readonly IFanMonitor _fans;
    private readonly bool _isElevated;
    private Task? _fanLoop;
    private bool _isVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "게임 모드를 켜면 전원 관리 옵션을 성능 위주로 바꾸고 메모리를 정리합니다. 끄면 전원 관리 옵션을 원래대로 되돌립니다.";

    [ObservableProperty]
    private IReadOnlyList<PowerScheme> _powerSchemes = [];

    [ObservableProperty]
    private PowerScheme? _gamePlan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameModeOn), nameof(GameModeText))]
    private string _activePlanName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameModeOn), nameof(GameModeText))]
    private bool _canTurnOff;

    [ObservableProperty]
    private string _memoryResultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FanStatusText), nameof(HasFanReading), nameof(HasTemperature), nameof(CpuTemperatureText),
        nameof(FanSpeedText), nameof(FanRpmText), nameof(GpuFanSpeedText), nameof(GpuFanRpmText), nameof(HasGpuFan),
        nameof(IsWarm), nameof(IsOverheating), nameof(TemperatureAdvice))]
    private FanReading? _fanReading;

    public GameModeViewModel(TweakRunner runner, IPowerSchemes power, QuickOptimizeRunner quick, IFanMonitor fans,
        FanGuard fanGuard, FanModeStore fanModes, ITweak turbo, UndervoltViewModel undervolt, bool isElevated)
    {
        _runner = runner;
        _power = power;
        _quick = quick;
        _fans = fans;
        _isElevated = isElevated;
        Fans = new FanControlViewModel(fanGuard, fanModes, isElevated);
        Undervolt = undervolt;
        Turbo = new TweakItemViewModel(turbo, isElevated);
        Turbo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TweakItemViewModel.IsApplied))
                OnPropertyChanged(nameof(TemperatureAdvice));
        };
    }

    public bool IsIdle => !IsBusy;

    public bool IsGameModeOn => CanTurnOff && GamePlan is not null && ActivePlanName == GamePlan.Name;

    public string GameModeText => IsGameModeOn ? $"게임 모드 켜짐 · {ActivePlanName}" : $"게임 모드 꺼짐 · 현재 전원 관리 옵션: {ActivePlanName}";

    /// <summary>터보 부스트 제한. 최적화 화면의 같은 항목과 기록을 함께 쓴다.</summary>
    public TweakItemViewModel Turbo { get; }

    /// <summary>팬 속도 조절과 사용자 지정 모드.</summary>
    public FanControlViewModel Fans { get; }

    /// <summary>CPU 전압 낮추기(언더볼팅).</summary>
    public UndervoltViewModel Undervolt { get; }

    public bool HasFanReading => FanReading is not null;

    public string FanStatusText => FanReading switch
    {
        { CpuTemperature: not null } => "노트북 제조사 인터페이스(Clevo)에서 2초마다 읽습니다.",
        not null => "Clevo 팬 인터페이스에 연결됐지만 온도 값을 알아보지 못했습니다. 아래 진단용 원래 값을 확인하세요.",
        _ when !_isElevated => "온도·팬 정보는 관리자 권한으로 실행해야 읽을 수 있습니다.",
        _ => "이 PC에서는 지원하는 팬 인터페이스를 찾지 못했습니다. (Clevo 계열 노트북만 지원)",
    };

    public bool HasTemperature => FanReading?.CpuTemperature is not null;

    public string CpuTemperatureText => FanReading?.CpuTemperature is { } celsius ? $"{celsius}°C" : "–";

    public string FanSpeedText => FanReading?.FanPercent is { } percent ? $"{percent}%" : "–";

    public string FanRpmText => FanReading?.FanRpm is { } rpm ? $"약 {rpm:N0} RPM" : "";

    public bool HasGpuFan => FanReading?.HasGpuFan == true;

    public string GpuFanSpeedText => FanReading?.GpuFanPercent is { } percent ? $"{percent}%" : "–";

    public string GpuFanRpmText => FanReading switch
    {
        { GpuFanRpm: { } rpm } => $"약 {rpm:N0} RPM",
        { HasGpuFan: false } => "감지 안 됨 (팬 1개 기기일 수 있음)",
        _ => "",
    };

    public bool IsOverheating => FanReading?.CpuTemperature >= HotTemperature;

    public bool IsWarm => FanReading?.CpuTemperature is >= WarmTemperature and < HotTemperature;

    public string TemperatureAdvice
    {
        get
        {
            if (FanReading?.CpuTemperature is not { } celsius)
                return "";
            if (celsius < WarmTemperature)
                return "정상 온도입니다.";
            if (celsius < HotTemperature)
                return "뜨거운 편입니다. 게임 중이면 흔한 온도지만, 아무것도 안 할 때도 이 정도면 먼지 청소를 고려하세요.";

            var advice = "과열입니다. 이 온도에서는 CPU가 스스로 속도를 낮춰 게임이 끊길 수 있습니다.";
            if (FanReading.FanPercent >= 95)
                advice += " 팬은 이미 최대로 돌고 있어 더 빠르게 할 수 없습니다.";
            if (!Turbo.IsApplied)
                advice += " 아래 '터보 부스트 제한'을 켜면 온도가 크게 내려갑니다.";
            return advice + " 통풍구가 막히지 않게 단단한 곳에 두고, 오래 쓴 노트북이면 내부 먼지 청소와 서멀 재도포를 받아 보세요.";
        }
    }

    /// <summary>화면에 들어오면 상태를 읽고 팬 정보를 주기적으로 읽기 시작한다.</summary>
    public void Activate()
    {
        _isVisible = true;
        RefreshPower();
        _ = Turbo.RefreshAsync();
        Undervolt.RefreshCommand.Execute(null);
        if (_fanLoop is not { IsCompleted: false })
            _fanLoop = RefreshFansAsync();
    }

    /// <summary>화면에서 나간다. 사용자 지정 팬 속도가 걸려 있지 않으면 팬 정보 읽기를 멈춘다.</summary>
    public void Deactivate() => _isVisible = false;

    [RelayCommand]
    private async Task TurnOnAsync()
    {
        if (IsBusy || GamePlan is not { } plan)
            return;

        IsBusy = true;
        try
        {
            // 이미 그 옵션이면 바꾸지 않는다. 바꾼 기록이 쌓여도 끌 때는 가장 처음 상태로 돌아간다.
            if (_power.GetActive() != plan.Id)
            {
                StatusText = $"전원 관리 옵션을 바꾸는 중: {plan.Name}";
                await _runner.ApplyAsync(new PowerPlanTweak(_power, plan), createRestorePoint: false, _ => { }, CancellationToken.None);
            }

            StatusText = "메모리를 정리하는 중입니다.";
            MemoryResultText = await _quick.RunAsync();
            StatusText = $"게임 모드를 켰습니다. 전원 관리 옵션: {plan.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"게임 모드를 켜지 못했습니다: {ex.Message}";
        }
        finally
        {
            RefreshPower();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TurnOffAsync()
    {
        if (IsBusy || !CanTurnOff)
            return;

        IsBusy = true;
        try
        {
            await _runner.RevertToOriginalAsync(new PowerPlanTweak(_power, null), CancellationToken.None);
            StatusText = "게임 모드를 껐습니다. 전원 관리 옵션을 원래대로 되돌렸습니다.";
        }
        catch (Exception ex)
        {
            StatusText = $"되돌리지 못했습니다: {ex.Message}";
        }
        finally
        {
            RefreshPower();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OptimizeMemoryAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            StatusText = "메모리를 정리하는 중입니다.";
            MemoryResultText = await _quick.RunAsync();
            StatusText = "메모리를 정리했습니다.";
        }
        catch (Exception ex)
        {
            StatusText = $"메모리를 정리하지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleTurboAsync()
    {
        if (IsBusy || !Turbo.CanChange)
            return;

        IsBusy = true;
        try
        {
            if (Turbo.IsApplied)
            {
                // WinCleaner 기록이 없으면(다른 프로그램으로 낮췄으면) Windows 기본값 100%로 되돌린다.
                if (!await _runner.RevertToOriginalAsync(Turbo.Tweak, CancellationToken.None))
                    await _runner.RevertAsync(Turbo.Tweak, null, CancellationToken.None);
                StatusText = "터보 부스트 제한을 껐습니다. 최대 프로세서 상태를 원래대로 되돌렸습니다.";
            }
            else
            {
                await _runner.ApplyAsync(Turbo.Tweak, createRestorePoint: false, _ => { }, CancellationToken.None);
                StatusText = "터보 부스트 제한을 켰습니다. 1~2분 뒤 CPU 온도가 내려가는지 확인하세요.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"터보 부스트 설정을 바꾸지 못했습니다: {ex.Message}";
        }
        finally
        {
            await Turbo.RefreshAsync();
            IsBusy = false;
        }
    }

    private void RefreshPower()
    {
        try
        {
            PowerSchemes = _power.List();
            var active = _power.GetActive();
            ActivePlanName = PowerSchemes.FirstOrDefault(s => s.Id == active)?.Name ?? active.ToString();
            GamePlan ??= PreferredPlanNames
                .Select(name => PowerSchemes.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(s => s is not null) ?? PowerSchemes.FirstOrDefault(s => s.Id == active);
            CanTurnOff = _runner.Journal.LatestRevertible(PowerPlanTweak.TweakId) is not null;
        }
        catch (Exception ex)
        {
            ActivePlanName = $"읽지 못함 ({ex.Message})";
        }
        OnPropertyChanged(nameof(IsGameModeOn));
        OnPropertyChanged(nameof(GameModeText));
    }

    partial void OnGamePlanChanged(PowerScheme? value)
    {
        OnPropertyChanged(nameof(IsGameModeOn));
        OnPropertyChanged(nameof(GameModeText));
    }

    private async Task RefreshFansAsync()
    {
        using var timer = new PeriodicTimer(FanRefreshInterval);
        do
        {
            FanReading = await Task.Run(_fans.Read);
            await Fans.OnReadingAsync(FanReading);

            // 사용자 지정 팬 속도가 걸려 있으면 다른 화면에 있어도 안전장치가 돌아야 한다.
            // 그렇지 않으면 화면을 떠났거나, 지원하지 않거나 권한이 없을 때 그만 읽는다.
            if (!Fans.IsManual && (!_isVisible || FanReading is null))
                return;
        }
        while (await timer.WaitForNextTickAsync());
    }
}
