using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;

namespace WinCleaner.App.ViewModels;

/// <summary>
/// 게임 중 화면 구석에 CPU·GPU·메모리·온도 등을 보여 주는 오버레이와 그 설정.
/// 게임에 끼어들지 않는(주입하지 않는) 별도 창이라 안티치트와 부딪히지 않는 대신, 게임이 "전체 창 모드(테두리 없음)"일 때만 위에 보인다.
/// 보이는 동안에만 1초마다 값을 읽고, 설정에서 켠 항목만 읽는다.
/// </summary>
public partial class OverlayViewModel : ObservableObject
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    public const string HotkeyText = "Ctrl + Shift + F10";

    /// <summary>이 온도부터 "열 한계"로 표시한다. 이 노트북은 열 스로틀링 중 95~97°C에 머물렀다 (사양서 5.12).</summary>
    public const int HeatLimitCelsius = 95;

    private static readonly string[] SettingNames =
    [
        nameof(ShowClock), nameof(ShowCpu), nameof(ShowTemperature), nameof(ShowThrottle), nameof(ShowGpu), nameof(ShowMemory),
        nameof(ShowFan), nameof(ShowStatus), nameof(ShowTopProcesses), nameof(ShowDiskNetwork), nameof(ReadGpuWhenIdle),
        nameof(Position), nameof(MarginX), nameof(MarginY), nameof(Scale), nameof(BackgroundOpacity),
    ];

    private static readonly string[] VisibilityNames =
    [
        nameof(IsClockRowVisible), nameof(IsCpuRowVisible), nameof(IsTemperatureVisible), nameof(IsThrottleVisible),
        nameof(IsGpuRowVisible), nameof(IsMemoryRowVisible), nameof(IsFanRowVisible), nameof(IsDiskNetworkRowVisible),
        nameof(IsTopRowVisible), nameof(IsStatusRowVisible),
    ];

    private readonly IOverlaySampler _sampler;
    private readonly Action<bool> _showWindow;
    private readonly OverlaySettingsStore? _store;
    private Task? _loop;
    private CancellationTokenSource? _pendingSave;
    private bool _loading;

    // ── 설정 (게임 모드 화면에서 바꾼다) ──

    [ObservableProperty]
    private bool _showClock;

    [ObservableProperty]
    private bool _showCpu;

    [ObservableProperty]
    private bool _showTemperature;

    [ObservableProperty]
    private bool _showThrottle;

    [ObservableProperty]
    private bool _showGpu;

    [ObservableProperty]
    private bool _showMemory;

    [ObservableProperty]
    private bool _showFan;

    [ObservableProperty]
    private bool _showStatus;

    [ObservableProperty]
    private bool _showTopProcesses;

    [ObservableProperty]
    private bool _showDiskNetwork;

    [ObservableProperty]
    private bool _readGpuWhenIdle;

    [ObservableProperty]
    private OverlayPosition _position;

    [ObservableProperty]
    private int _marginX;

    [ObservableProperty]
    private int _marginY;

    [ObservableProperty]
    private double _scale;

    [ObservableProperty]
    private double _backgroundOpacity;

    // ── 보여 주는 값 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isShown;

    [ObservableProperty]
    private string _timeText = "";

    [ObservableProperty]
    private string _cpuText = "–";

    [ObservableProperty]
    private string _cpuSpeedText = "";

    [ObservableProperty]
    private string _temperatureText = "";

    [ObservableProperty]
    private bool _isWarm;

    [ObservableProperty]
    private bool _isHot;

    [ObservableProperty]
    private string _throttleText = "";

    [ObservableProperty]
    private string _gpuText = "";

    [ObservableProperty]
    private string _gpuDetailText = "";

    [ObservableProperty]
    private string _memoryText = "–";

    [ObservableProperty]
    private string _memoryPercentText = "";

    [ObservableProperty]
    private string _fanText = "";

    [ObservableProperty]
    private string _diskText = "";

    [ObservableProperty]
    private string _networkText = "";

    [ObservableProperty]
    private string _topCpuText = "";

    [ObservableProperty]
    private string _topMemoryText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _hotkeyStatusText = $"{HotkeyText}로 켜고 끕니다.";

    /// <param name="showWindow">true면 오버레이 창을 보이고, false면 숨긴다.</param>
    /// <param name="store">설정을 저장할 곳. null이면 저장하지 않는다(테스트).</param>
    public OverlayViewModel(IOverlaySampler sampler, Action<bool> showWindow, OverlaySettingsStore? store = null)
    {
        _sampler = sampler;
        _showWindow = showWindow;
        _store = store;
        Load(store?.Load() ?? new OverlaySettings());
    }

    /// <summary>오버레이 "상태" 줄에 보일 지금 걸린 설정(팬 모드·전압 등). 게임 모드 화면이 채운다.</summary>
    public Func<string?>? StatusProvider { get; set; }

    public string ToggleText => IsShown ? "오버레이 숨기기" : "오버레이 보이기";

    public IReadOnlyList<KeyValuePair<OverlayPosition, string>> PositionOptions { get; } =
    [
        new(OverlayPosition.TopLeft, "왼쪽 위"),
        new(OverlayPosition.TopCenter, "위 가운데"),
        new(OverlayPosition.TopRight, "오른쪽 위"),
        new(OverlayPosition.BottomLeft, "왼쪽 아래"),
        new(OverlayPosition.BottomRight, "오른쪽 아래"),
    ];

    public IReadOnlyList<KeyValuePair<double, string>> ScaleOptions { get; } =
    [
        new(0.85, "작게"),
        new(1.0, "보통"),
        new(1.25, "크게"),
        new(1.5, "아주 크게"),
    ];

    public bool HasGpu => GpuText.Length > 0;
    public bool HasFan => FanText.Length > 0;
    public bool IsThrottled => ThrottleText.Length > 0;

    public bool IsClockRowVisible => ShowClock;
    public bool IsCpuRowVisible => ShowCpu || IsTemperatureVisible || IsThrottleVisible;
    public bool IsTemperatureVisible => ShowTemperature && TemperatureText.Length > 0;
    public bool IsThrottleVisible => ShowThrottle && IsThrottled;
    public bool IsGpuRowVisible => ShowGpu && HasGpu;
    public bool IsMemoryRowVisible => ShowMemory;
    public bool IsFanRowVisible => ShowFan && HasFan;
    public bool IsDiskNetworkRowVisible => ShowDiskNetwork && (DiskText.Length > 0 || NetworkText.Length > 0);
    public bool IsTopRowVisible => ShowTopProcesses && (TopCpuText.Length > 0 || TopMemoryText.Length > 0);
    public bool IsStatusRowVisible => ShowStatus && StatusText.Length > 0;

    public OverlaySettings CurrentSettings => new()
    {
        ShowClock = ShowClock, ShowCpu = ShowCpu, ShowTemperature = ShowTemperature, ShowThrottle = ShowThrottle,
        ShowGpu = ShowGpu, ShowMemory = ShowMemory, ShowFan = ShowFan, ShowStatus = ShowStatus,
        ShowTopProcesses = ShowTopProcesses, ShowDiskNetwork = ShowDiskNetwork, ReadGpuWhenIdle = ReadGpuWhenIdle, Position = Position,
        MarginX = MarginX, MarginY = MarginY, Scale = Scale, BackgroundOpacity = BackgroundOpacity,
    };

    /// <summary>단축키를 등록하지 못했을 때 화면에 알린다.</summary>
    public void SetHotkeyAvailable(bool registered) => HotkeyStatusText = registered
        ? $"{HotkeyText}로 켜고 끕니다. 게임 중에도 됩니다."
        : $"{HotkeyText}를 다른 프로그램이 쓰고 있어 단축키를 등록하지 못했습니다. 이 버튼으로 켜고 끄세요.";

    [RelayCommand]
    private void Toggle() => IsShown = !IsShown;

    [RelayCommand]
    private void ResetSettings() => Load(new OverlaySettings(), save: true);

    partial void OnIsShownChanged(bool value)
    {
        _showWindow(value);
        if (value && _loop is not { IsCompleted: false })
            _loop = RefreshAsync();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && SettingNames.Contains(name))
        {
            NotifyVisibility();
            if (!_loading)
                ScheduleSave();
        }
    }

    /// <summary>읽은 값을 화면 문구로 바꾼다.</summary>
    public void Apply(OverlaySnapshot snapshot, DateTime now)
    {
        TimeText = now.ToString("HH:mm", CultureInfo.InvariantCulture);

        var system = snapshot.System;
        CpuText = system?.CpuUsage is { } usage ? $"{usage:0}%" : "–";
        CpuSpeedText = system?.CpuGhz is { } ghz ? $"{ghz.ToString("0.0", CultureInfo.InvariantCulture)}GHz" : "";

        var celsius = snapshot.Fans?.CpuTemperature;
        TemperatureText = celsius is { } t ? $"{t}°C" : "";
        IsHot = celsius >= GameModeViewModel.HotTemperature;
        IsWarm = celsius is >= GameModeViewModel.WarmTemperature and < GameModeViewModel.HotTemperature;
        // Windows의 속도 상한 카운터는 열 스로틀링을 보여 주지 않아 온도로 판단한다.
        ThrottleText = celsius >= HeatLimitCelsius ? "열 한계"
            : system?.PerformanceLimit is { } limit && limit < 99 ? $"속도 제한 {limit:0}%"
            : "";

        if (system?.Gpu is { } gpu)
        {
            GpuText = $"{gpu.UsagePercent:0}%";
            var details = new List<string>();
            if (gpu.TemperatureCelsius is { } gpuCelsius)
                details.Add($"{gpuCelsius}°C");
            // 쉬고 있으면 NVML이 0MHz를 돌려준다(2026-11-07 확인).
            if (gpu.CoreGhz is { } gpuGhz and >= 0.05)
                details.Add($"{gpuGhz.ToString("0.0", CultureInfo.InvariantCulture)}GHz");
            if (details.Count == 0 && gpu.UsagePercent < 0.5)
                details.Add("쉬는 중");
            // 롤 클라이언트처럼 내장 그래픽으로 도는 프로그램이 있으면 함께 보인다.
            if (gpu.OtherUsagePercent >= 0.5)
                details.Add($"내장 {gpu.OtherUsagePercent:0}%");
            GpuDetailText = string.Join("  ", details);
        }
        else
        {
            GpuText = "";
            GpuDetailText = "";
        }

        if (snapshot.Memory is { TotalBytes: > 0 } memory)
        {
            MemoryText = $"{Gigabytes(memory.UsedBytes)} / {Gigabytes(memory.TotalBytes)}GB";
            MemoryPercentText = $"{memory.UsedRatio * 100:0}%";
        }
        else
        {
            MemoryText = "–";
            MemoryPercentText = "";
        }

        FanText = snapshot.Fans?.FanRpm is { } rpm ? $"{rpm:N0} RPM" : "";

        DiskText = system?.DiskReadBytesPerSecond is { } read && system.DiskWriteBytesPerSecond is { } write
            ? $"읽기 {Rate(read)} · 쓰기 {Rate(write)}"
            : "";
        NetworkText = system?.NetworkReceiveBytesPerSecond is { } down && system.NetworkSendBytesPerSecond is { } up
            ? $"↓ {Rate(down)} · ↑ {Rate(up)}"
            : "";

        TopCpuText = system?.TopCpuProcess is { } topCpu && system.TopCpuPercent is { } topPercent
            ? $"CPU {topCpu} {topPercent:0}%"
            : "";
        TopMemoryText = snapshot.TopMemory is { } topMemory ? $"RAM {topMemory.Name} {Gigabytes(topMemory.Bytes)}GB" : "";

        StatusText = ShowStatus ? StatusProvider?.Invoke() ?? "" : "";

        OnPropertyChanged(nameof(HasGpu));
        OnPropertyChanged(nameof(HasFan));
        OnPropertyChanged(nameof(IsThrottled));
        NotifyVisibility();
    }

    /// <summary>초당 바이트를 읽기 쉬운 단위로.</summary>
    public static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1 << 30 => $"{(bytesPerSecond / (1 << 30)).ToString("0.0", CultureInfo.InvariantCulture)}GB/s",
        >= 1 << 20 => $"{(bytesPerSecond / (1 << 20)).ToString("0.0", CultureInfo.InvariantCulture)}MB/s",
        _ => $"{bytesPerSecond / 1024:0}KB/s",
    };

    private void Load(OverlaySettings settings, bool save = false)
    {
        _loading = !save;
        var s = settings.Clamped();
        ShowClock = s.ShowClock;
        ShowCpu = s.ShowCpu;
        ShowTemperature = s.ShowTemperature;
        ShowThrottle = s.ShowThrottle;
        ShowGpu = s.ShowGpu;
        ShowMemory = s.ShowMemory;
        ShowFan = s.ShowFan;
        ShowStatus = s.ShowStatus;
        ShowTopProcesses = s.ShowTopProcesses;
        ShowDiskNetwork = s.ShowDiskNetwork;
        ReadGpuWhenIdle = s.ReadGpuWhenIdle;
        Position = s.Position;
        MarginX = s.MarginX;
        MarginY = s.MarginY;
        Scale = s.Scale;
        BackgroundOpacity = s.BackgroundOpacity;
        _loading = false;
    }

    private void NotifyVisibility()
    {
        foreach (var name in VisibilityNames)
            OnPropertyChanged(name);
    }

    // 슬라이더를 끄는 동안 파일을 계속 쓰지 않도록 0.5초 동안 바뀌지 않으면 저장한다.
    private void ScheduleSave()
    {
        if (_store is null)
            return;
        _pendingSave?.Cancel();
        var cts = _pendingSave = new CancellationTokenSource();
        var settings = CurrentSettings;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                _store.Save(settings);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 저장하지 못해도 이번 실행 동안은 화면에 반영돼 있다.
            }
        });
    }

    private async Task RefreshAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            try
            {
                var settings = CurrentSettings;
                Apply(await Task.Run(() => _sampler.Sample(settings)), DateTime.Now);
            }
            catch (Exception)
            {
                // 한 번 못 읽어도 다음 차례에 다시 읽는다.
            }
        }
        while (IsShown && await timer.WaitForNextTickAsync());
    }

    private static string Gigabytes(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture);
}
