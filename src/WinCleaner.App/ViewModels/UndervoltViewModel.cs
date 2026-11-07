using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCleaner.App.Services;
using WinCleaner.Optimizer.Cpu;
using WinCleaner.Safety;

namespace WinCleaner.App.ViewModels;

/// <summary>
/// 게임 모드 화면의 CPU 전압 낮추기(언더볼팅). 값을 바로 적용하고, 원하면 로그인할 때마다 다시 적용하는 작업을 등록한다.
/// 이 PC(i7-8750H)는 2026-11-07 측정에서 −125mV가 2분 부하를 오류 없이 통과했다 (사양서 5.12).
/// </summary>
public partial class UndervoltViewModel : ObservableObject
{
    public const int RecommendedMillivolts = -125;

    private readonly IUndervolt _undervolt;
    private readonly IUndervoltStartup _startup;
    private readonly IUserDialogs _dialogs;
    private readonly ChangeJournal _journal;
    private readonly bool _isElevated;
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MillivoltsText))]
    private int _millivolts = RecommendedMillivolts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentText))]
    private UndervoltState? _current;

    [ObservableProperty]
    private bool _autoApply;

    [ObservableProperty]
    private string _autoApplyText = "";

    [ObservableProperty]
    private string _messageText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    private bool _isBusy;

    public UndervoltViewModel(IUndervolt undervolt, IUndervoltStartup startup, IUserDialogs dialogs, ChangeJournal journal, bool isElevated)
    {
        _undervolt = undervolt;
        _startup = startup;
        _dialogs = dialogs;
        _journal = journal;
        _isElevated = isElevated;
    }

    public int MinMillivolts => XtuUndervolt.MinMillivolts;

    public int MaxMillivolts => XtuUndervolt.MaxMillivolts;

    public bool IsSupported => _isElevated && _undervolt.IsAvailable;

    public bool CanChange => IsSupported && !IsBusy;

    public string MillivoltsText => Format(Millivolts);

    public string CurrentText => Current switch
    {
        _ when !_undervolt.IsAvailable => "이 PC에는 인텔 튜닝 서비스(Control Center 2.0과 함께 설치됨)가 없어 쓸 수 없습니다.",
        _ when !_isElevated => "CPU 전압은 관리자 권한으로 실행해야 읽고 바꿀 수 있습니다.",
        null => "지금 값을 읽는 중입니다.",
        { IsDefault: true } => "지금: 원래대로 (0mV)",
        { CoreMillivolts: var core, CacheMillivolts: var cache } when core == cache => $"지금: 코어·캐시 {Format(core)}",
        { CoreMillivolts: var core, CacheMillivolts: var cache } => $"지금: 코어 {Format(core)} · 캐시 {Format(cache)} (둘이 달라 효과가 줄 수 있습니다)",
    };

    /// <summary>화면에 들어올 때 지금 값과 자동 적용 상태를 읽는다.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsSupported || IsBusy)
        {
            OnPropertyChanged(nameof(CurrentText));
            return;
        }

        IsBusy = true;
        try
        {
            Current = await _undervolt.ReadAsync(CancellationToken.None);
            if (!Current.IsDefault)
                Millivolts = Current.CoreMillivolts;
            await ReadStartupAsync();
        }
        catch (Exception ex)
        {
            MessageText = $"지금 값을 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanChange)
            return;
        var target = Millivolts;
        if (target != 0 && !_dialogs.Confirm("CPU 전압 낮추기",
                $"CPU 코어·캐시 전압을 {Format(target)} 낮춥니다.\n\n" +
                "너무 낮으면 화면이 멈추거나 블루스크린이 날 수 있습니다. 그래도 고장 나지는 않고, 재부팅하면 원래대로 돌아갑니다.\n\n적용할까요?"))
            return;

        await ChangeAsync(target, AutoApply);
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!CanChange)
            return;
        // 원래대로 돌리면 자동 적용도 끈다. 0mV를 매번 적용할 이유가 없다.
        await ChangeAsync(0, keepAutoApply: false);
    }

    partial void OnAutoApplyChanged(bool value)
    {
        if (!_syncing)
            _ = SetAutoApplyAsync(value);
    }

    private async Task SetAutoApplyAsync(bool enable)
    {
        if (!CanChange)
        {
            SetAutoApplySilently(!enable);
            return;
        }
        if (enable && !_dialogs.Confirm("로그인할 때 자동 적용",
                $"Windows에 로그인할 때와 절전에서 깨어날 때 CPU 전압을 {Format(Millivolts)} 다시 적용합니다.\n\n" +
                "블루스크린이나 강제 종료가 한 번이라도 있으면 다음 부팅 때 적용하지 않고 자동 적용을 스스로 끕니다.\n\n켤까요?"))
        {
            SetAutoApplySilently(false);
            return;
        }

        IsBusy = true;
        try
        {
            if (enable)
            {
                await _startup.RegisterAsync(Millivolts, CancellationToken.None);
                Record($"CPU 전압 자동 적용 켬: {Format(Millivolts)}", "로그인할 때와 절전에서 깨어날 때 다시 적용합니다.");
                MessageText = $"자동 적용을 켰습니다. 다음 로그인부터 {Format(Millivolts)}을 적용합니다.";
            }
            else
            {
                await _startup.UnregisterAsync(CancellationToken.None);
                Record("CPU 전압 자동 적용 끔", "재부팅하면 원래대로(0mV) 돌아갑니다.");
                MessageText = "자동 적용을 껐습니다. 재부팅하면 원래대로(0mV) 돌아갑니다.";
            }
            await ReadStartupAsync();
        }
        catch (Exception ex)
        {
            MessageText = $"자동 적용을 바꾸지 못했습니다: {ex.Message}";
            await ReadStartupSafelyAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChangeAsync(int target, bool keepAutoApply)
    {
        IsBusy = true;
        try
        {
            Current = await _undervolt.ApplyAsync(target, CancellationToken.None);
            Millivolts = target == 0 ? RecommendedMillivolts : target;

            // 자동 적용이 켜져 있으면 작업도 새 값으로 바꾼다. 원래대로 돌렸으면 작업을 지운다.
            if (AutoApply && keepAutoApply)
                await _startup.RegisterAsync(target, CancellationToken.None);
            else if (AutoApply)
                await _startup.UnregisterAsync(CancellationToken.None);

            Record(target == 0 ? "CPU 전압 원래대로 (0mV)" : $"CPU 전압 낮추기: {Format(target)}",
                AutoApply && keepAutoApply ? "로그인할 때 자동으로 다시 적용합니다." : "재부팅하면 원래대로(0mV) 돌아갑니다.");
            MessageText = target == 0
                ? "원래대로(0mV) 돌렸습니다."
                : $"{Format(target)}을 적용했습니다. 평소처럼 쓰다가 멈춤이나 블루스크린이 생기면 재부팅하고 한 단계(25mV) 덜 낮추세요.";
            await ReadStartupAsync();
        }
        catch (Exception ex)
        {
            MessageText = $"바꾸지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReadStartupAsync()
    {
        var state = await _startup.ReadAsync(CancellationToken.None);
        SetAutoApplySilently(state.IsRegistered && state.IsEnabled);
        AutoApplyText = DescribeStartup(state);
    }

    private async Task ReadStartupSafelyAsync()
    {
        try
        {
            await ReadStartupAsync();
        }
        catch (Exception)
        {
            // 읽기 실패는 위의 오류 문구로 이미 알렸다.
        }
    }

    private void SetAutoApplySilently(bool value)
    {
        _syncing = true;
        AutoApply = value;
        _syncing = false;
    }

    public static string DescribeStartup(UndervoltStartupState state)
    {
        var last = DescribeLastRun(state.LastRun);
        return state switch
        {
            { IsRegistered: false } => last is null ? "자동 적용 꺼짐: 재부팅하면 원래대로(0mV) 돌아갑니다." : $"자동 적용 꺼짐 · {last}",
            { IsEnabled: false } => $"자동 적용이 꺼져 있습니다. {last ?? ""}".Trim(),
            { Millivolts: { } mv } => $"자동 적용 켜짐: 로그인할 때 {Format(mv)}" + (last is null ? "" : $" · {last}"),
            _ => "자동 적용 켜짐" + (last is null ? "" : $" · {last}"),
        };
    }

    /// <summary>상태 파일 한 줄을 사람이 읽는 문장으로 바꾼다.</summary>
    public static string? DescribeLastRun(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        var parts = line.Split(' ', 3);
        var time = DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? $"{at:MM-dd HH:mm}" : parts[0];
        var kind = parts.Length > 1 ? parts[1] : "";
        return kind switch
        {
            "applied" => $"마지막 적용 {time}",
            "skipped-crash" => $"{time}에 지난번 비정상 종료(블루스크린·강제 종료)를 감지해 적용하지 않고 자동 적용을 껐습니다. 전압을 한 단계 덜 낮춰 보세요.",
            "error" => $"{time} 자동 적용 실패: {(parts.Length > 2 ? parts[2] : "")}",
            _ => null,
        };
    }

    private void Record(string title, string detail) =>
        _journal.Add(new ChangeEntry { Kind = ChangeKind.Tweak, Title = title, Detail = detail });

    private static string Format(int millivolts) => millivolts == 0 ? "0mV" : $"{millivolts.ToString("+0;−0", CultureInfo.InvariantCulture)}mV";
}
