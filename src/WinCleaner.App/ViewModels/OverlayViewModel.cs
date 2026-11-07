using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;

namespace WinCleaner.App.ViewModels;

/// <summary>
/// 게임 중 화면 오른쪽 위에 CPU·메모리·온도를 보여 주는 오버레이.
/// 게임에 끼어들지 않는(주입하지 않는) 별도 창이라 안티치트와 부딪히지 않는 대신, 게임이 "전체 창 모드(테두리 없음)"일 때만 위에 보인다.
/// 보이는 동안에만 1초마다 값을 읽는다.
/// </summary>
public partial class OverlayViewModel : ObservableObject
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    public const string HotkeyText = "Ctrl + Shift + F10";

    private readonly IOverlaySampler _sampler;
    private readonly Action<bool> _showWindow;
    private Task? _loop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isShown;

    [ObservableProperty]
    private string _cpuText = "–";

    [ObservableProperty]
    private string _clockText = "";

    [ObservableProperty]
    private string _temperatureText = "";

    [ObservableProperty]
    private bool _isWarm;

    [ObservableProperty]
    private bool _isHot;

    [ObservableProperty]
    private string _memoryText = "–";

    [ObservableProperty]
    private string _memoryPercentText = "";

    [ObservableProperty]
    private string _fanText = "";

    [ObservableProperty]
    private bool _hasFan;

    [ObservableProperty]
    private string _hotkeyStatusText = $"{HotkeyText}로 켜고 끕니다.";

    /// <param name="showWindow">true면 오버레이 창을 보이고, false면 숨긴다.</param>
    public OverlayViewModel(IOverlaySampler sampler, Action<bool> showWindow)
    {
        _sampler = sampler;
        _showWindow = showWindow;
    }

    public string ToggleText => IsShown ? "오버레이 숨기기" : "오버레이 보이기";

    /// <summary>단축키를 등록하지 못했을 때 화면에 알린다.</summary>
    public void SetHotkeyAvailable(bool registered) => HotkeyStatusText = registered
        ? $"{HotkeyText}로 켜고 끕니다. 게임 중에도 됩니다."
        : $"{HotkeyText}를 다른 프로그램이 쓰고 있어 단축키를 등록하지 못했습니다. 이 버튼으로 켜고 끄세요.";

    [RelayCommand]
    private void Toggle() => IsShown = !IsShown;

    partial void OnIsShownChanged(bool value)
    {
        _showWindow(value);
        if (value && _loop is not { IsCompleted: false })
            _loop = RefreshAsync();
    }

    /// <summary>읽은 값을 화면 문구로 바꾼다.</summary>
    public void Apply(OverlaySnapshot snapshot)
    {
        CpuText = snapshot.Cpu is { } cpu ? $"{cpu.UsagePercent:0}%" : "–";
        ClockText = snapshot.Cpu?.Ghz is { } ghz ? $"{ghz.ToString("0.0", CultureInfo.InvariantCulture)}GHz" : "";

        var celsius = snapshot.Fans?.CpuTemperature;
        TemperatureText = celsius is { } t ? $"{t}°C" : "";
        IsHot = celsius >= GameModeViewModel.HotTemperature;
        IsWarm = celsius is >= GameModeViewModel.WarmTemperature and < GameModeViewModel.HotTemperature;

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

        var rpm = snapshot.Fans?.FanRpm;
        HasFan = rpm is not null;
        FanText = rpm is { } r ? $"{r:N0} RPM" : "";
    }

    private async Task RefreshAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            try
            {
                Apply(await Task.Run(_sampler.Sample));
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
