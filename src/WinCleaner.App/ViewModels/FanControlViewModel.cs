using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.Optimizer.Fans;

namespace WinCleaner.App.ViewModels;

public enum FanModeKind
{
    /// <summary>노트북 펌웨어 자동 제어.</summary>
    Auto,

    /// <summary>CPU·GPU 팬 모두 100%.</summary>
    Max,

    /// <summary>저장하지 않고 슬라이더 값으로 돌린다.</summary>
    Free,

    /// <summary>사용자가 저장한 모드.</summary>
    Saved,
}

public sealed record FanModeOption(string Name, FanModeKind Kind, int CpuPercent = 100, int GpuPercent = 100)
{
    public bool CanAdjust => Kind is FanModeKind.Free or FanModeKind.Saved;

    public string DisplayName => Kind == FanModeKind.Saved ? $"{Name} ({CpuPercent}% · {GpuPercent}%)" : Name;
}

/// <summary>
/// 게임 모드 화면의 팬 속도 조절. 모드를 고르고 "적용"을 눌러야 팬이 바뀐다.
/// 실제 제어와 안전장치는 <see cref="FanGuard"/>가 맡고, 이 클래스는 화면 상태와 모드 저장을 맡는다.
/// </summary>
public partial class FanControlViewModel : ObservableObject
{
    public const string AutoName = "자동 (노트북 기본)";
    public const string FreeName = "직접 설정";

    private readonly FanGuard _guard;
    private readonly FanModeStore _store;
    private readonly bool _isElevated;
    private readonly SemaphoreSlim _gate = new(1, 1);

    [ObservableProperty]
    private IReadOnlyList<FanModeOption> _modes = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdjust), nameof(CanDelete))]
    private FanModeOption? _selectedMode;

    [ObservableProperty]
    private int _cpuPercent = 60;

    [ObservableProperty]
    private int _gpuPercent = 60;

    [ObservableProperty]
    private string _newModeName = "";

    [ObservableProperty]
    private string _controlText = FanGuard.AutoText;

    [ObservableProperty]
    private string _messageText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManual))]
    private FanControlState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanControl), nameof(IsUnavailable))]
    private bool _isAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanControl))]
    private bool _isBusy;

    public FanControlViewModel(FanGuard guard, FanModeStore store, bool isElevated)
    {
        _guard = guard;
        _store = store;
        _isElevated = isElevated;
        LoadModes(null);
        SyncFromGuard();
    }

    public int MinPercent => FanMode.MinPercent;

    public bool CanControl => _isElevated && IsAvailable && !IsBusy;

    public bool CanAdjust => SelectedMode?.CanAdjust == true;

    public bool CanDelete => SelectedMode?.Kind == FanModeKind.Saved;

    /// <summary>사용자 지정 속도가 걸려 있다(과열 보호 중 포함). 이 동안에는 다른 화면에 있어도 팬을 계속 지켜본다.</summary>
    public bool IsManual => State != FanControlState.Auto;

    public bool IsUnavailable => !IsAvailable;

    public string UnavailableText => _isElevated
        ? "이 PC에서는 팬 인터페이스를 찾지 못해 팬 속도를 바꿀 수 없습니다."
        : "팬 속도는 관리자 권한으로 실행해야 바꿀 수 있습니다.";

    partial void OnSelectedModeChanged(FanModeOption? value)
    {
        if (value is { Kind: FanModeKind.Max or FanModeKind.Saved })
        {
            CpuPercent = value.CpuPercent;
            GpuPercent = value.GpuPercent;
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanControl || SelectedMode is not { } option)
            return;

        FanMode? target = option.Kind switch
        {
            FanModeKind.Max => FanMode.Max,
            FanModeKind.Free => new FanMode(FreeName, CpuPercent, GpuPercent),
            FanModeKind.Saved => new FanMode(option.Name, CpuPercent, GpuPercent),
            _ => null,
        };
        MessageText = "";
        await RunAsync(() => _guard.Apply(target), showBusy: true);
    }

    [RelayCommand]
    private async Task RestoreAutoAsync()
    {
        SelectedMode = Modes.FirstOrDefault(m => m.Kind == FanModeKind.Auto);
        MessageText = "";
        await RunAsync(() => _guard.Apply(null), showBusy: true);
    }

    [RelayCommand]
    private void SaveMode()
    {
        var name = NewModeName.Trim();
        if (name.Length == 0 && SelectedMode is { Kind: FanModeKind.Saved } selected)
            name = selected.Name;
        if (name.Length == 0)
        {
            MessageText = "저장할 모드 이름을 입력하세요.";
            return;
        }
        if (name is AutoName or FreeName || name == FanMode.Max.Name)
        {
            MessageText = $"'{name}'은(는) 기본 모드 이름이라 쓸 수 없습니다.";
            return;
        }

        var modes = _store.Load()
            .Where(m => !m.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
            .Append(new FanMode(name, CpuPercent, GpuPercent).Clamped())
            .ToList();
        if (modes.Count > FanModeStore.MaxModes)
        {
            MessageText = $"모드는 {FanModeStore.MaxModes}개까지 저장할 수 있습니다. 쓰지 않는 모드를 지우세요.";
            return;
        }

        try
        {
            _store.Save(modes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText = $"저장하지 못했습니다: {ex.Message}";
            return;
        }

        LoadModes(name);
        NewModeName = "";
        MessageText = $"'{name}' 모드를 저장했습니다. 팬에 반영하려면 적용을 누르세요.";
    }

    [RelayCommand]
    private void DeleteMode()
    {
        if (SelectedMode is not { Kind: FanModeKind.Saved } selected)
            return;

        try
        {
            _store.Save(_store.Load().Where(m => m.Name != selected.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText = $"지우지 못했습니다: {ex.Message}";
            return;
        }

        LoadModes(null);
        // 지금 걸려 있는 속도는 그대로 둔다. 바꾸려면 다른 모드를 적용한다.
        MessageText = $"'{selected.Name}' 모드를 지웠습니다.";
    }

    /// <summary>팬 정보를 새로 읽을 때마다(2초) 부른다. 사용자 지정 중이면 안전장치를 돌린다.</summary>
    public async Task OnReadingAsync(FanReading? reading)
    {
        IsAvailable = reading is not null;
        if (_guard.State != FanControlState.Auto)
            await RunAsync(() => _guard.Tick(reading, DateTime.Now), showBusy: false);
    }

    /// <summary>앱을 끌 때 부른다. 사용자 지정 중이었으면 자동 제어로 되돌린다. 실패해도 다음 실행 때 되돌린다.</summary>
    public void Release()
    {
        try
        {
            _guard.Release();
        }
        catch (Exception)
        {
            // 표시 파일이 남아 있어 다음 실행 때 자동 제어로 되돌린다.
        }
    }

    private async Task RunAsync(Action action, bool showBusy)
    {
        await _gate.WaitAsync();
        if (showBusy)
            IsBusy = true;
        string? error = null;
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // 무엇이 잘못됐든 펌웨어 자동 제어로 돌려 둔다.
            try
            {
                await Task.Run(_guard.Release);
            }
            catch (Exception)
            {
                // 이것도 안 되면 표시 파일이 남아 다음 실행 때 다시 시도한다.
            }
        }
        finally
        {
            if (showBusy)
                IsBusy = false;
            _gate.Release();
        }

        SyncFromGuard();
        if (error is not null)
            MessageText = $"팬 속도를 바꾸지 못해 자동 제어로 되돌렸습니다: {error}";
    }

    private void SyncFromGuard()
    {
        State = _guard.State;
        ControlText = _guard.StatusText;
    }

    private void LoadModes(string? select)
    {
        var saved = _store.Load();
        Modes =
        [
            new FanModeOption(AutoName, FanModeKind.Auto),
            new FanModeOption(FanMode.Max.Name, FanModeKind.Max),
            new FanModeOption(FreeName, FanModeKind.Free),
            .. saved.Select(m => new FanModeOption(m.Name, FanModeKind.Saved, m.CpuPercent, m.GpuPercent)),
        ];
        SelectedMode = Modes.FirstOrDefault(m => m.Name == select) ?? Modes[0];
    }
}
