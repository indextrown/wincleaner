using WinCleaner.App.ViewModels;
using WinCleaner.Optimizer.Fans;

namespace WinCleaner.Tests;

public class ClevoFanControlTests
{
    [Theory]
    [InlineData(100, 255)]
    [InlineData(50, 128)]
    [InlineData(25, 64)]
    [InlineData(10, 64)] // 25% 아래로는 내리지 않는다.
    [InlineData(0, 64)]
    [InlineData(150, 255)]
    public void Percent_is_converted_to_raw_duty_with_a_floor(int percent, byte raw)
    {
        Assert.Equal(raw, ClevoFanControl.ToRaw(percent));
    }

    [Fact]
    public void Fans_are_packed_one_byte_each_in_fan_order()
    {
        // tuxedo-drivers와 같은 형식: 팬 1은 비트 0~7, 팬 2는 8~15, 팬 3은 16~23.
        Assert.Equal(0x00FF8040u, ClevoFanControl.Pack(0x40, 0x80, 0xFF));
        Assert.Equal(0x0Fu, ClevoFanControl.AllFansAuto);
    }
}

public sealed class FanGuardTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0);

    private readonly Sandbox _box = new();
    private readonly FakeFanControl _control = new();
    private readonly string _marker;
    private readonly FanGuard _guard;

    public FanGuardTests()
    {
        _marker = Path.Combine(_box.Root, "WinCleaner", "fan-manual.flag");
        _guard = new FanGuard(_control, _marker);
    }

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Applying_a_mode_sets_duty_and_leaves_a_crash_marker()
    {
        _guard.Apply(new FanMode("조용히", 40, 30));

        Assert.Equal(["duty 40/30"], _control.Calls);
        Assert.Equal(FanControlState.Manual, _guard.State);
        Assert.True(File.Exists(_marker));
        Assert.Contains("조용히", _guard.StatusText);
    }

    [Fact]
    public void Values_below_the_floor_are_raised()
    {
        _guard.Apply(new FanMode("너무 낮음", 0, 5));

        Assert.Equal(["duty 25/25"], _control.Calls);
        Assert.Equal(25, _guard.Mode!.CpuPercent);
    }

    [Fact]
    public void Auto_restores_firmware_control_and_removes_marker()
    {
        _guard.Apply(new FanMode("조용히", 40, 40));
        _guard.Apply(null);

        Assert.Equal(["duty 40/40", "auto"], _control.Calls);
        Assert.Equal(FanControlState.Auto, _guard.State);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void Overheating_hands_control_back_to_firmware_then_resumes_after_cooling()
    {
        _guard.Apply(new FanMode("조용히", 40, 40));

        _guard.Tick(Reading(celsius: 91, percent: 40), T0);
        Assert.Equal(FanControlState.Protecting, _guard.State);
        Assert.Equal("auto", _control.Calls[^1]);
        Assert.Contains("과열 보호", _guard.StatusText);

        _guard.Tick(Reading(celsius: 85, percent: 100), T0.AddSeconds(2));
        Assert.Equal(FanControlState.Protecting, _guard.State);

        _guard.Tick(Reading(celsius: 79, percent: 100), T0.AddSeconds(4));
        Assert.Equal(FanControlState.Manual, _guard.State);
        Assert.Equal("duty 40/40", _control.Calls[^1]);
        Assert.True(File.Exists(_marker));
    }

    [Fact]
    public void Max_mode_is_not_interrupted_by_heat()
    {
        _guard.Apply(FanMode.Max);

        _guard.Tick(Reading(celsius: 97, percent: 100), T0);

        Assert.Equal(FanControlState.Manual, _guard.State);
        Assert.Equal(["duty 100/100"], _control.Calls);
    }

    [Fact]
    public void Fan_not_spinning_for_ten_seconds_gives_up_and_returns_to_auto()
    {
        _guard.Apply(new FanMode("조용히", 40, 40));

        _guard.Tick(Reading(celsius: 60, percent: 40, rpmRaw: 0), T0);
        _guard.Tick(Reading(celsius: 60, percent: 40, rpmRaw: 0), T0.AddSeconds(8));
        Assert.Equal(FanControlState.Manual, _guard.State);

        _guard.Tick(Reading(celsius: 60, percent: 40, rpmRaw: 0), T0.AddSeconds(10));

        Assert.Equal(FanControlState.Auto, _guard.State);
        Assert.Equal("auto", _control.Calls[^1]);
        Assert.Contains("도는 것이 확인되지 않아", _guard.StatusText);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void Spinning_again_resets_the_stall_timer()
    {
        _guard.Apply(new FanMode("조용히", 40, 40));

        _guard.Tick(Reading(celsius: 60, percent: 40, rpmRaw: 0), T0);
        _guard.Tick(Reading(celsius: 60, percent: 40), T0.AddSeconds(6));
        _guard.Tick(Reading(celsius: 60, percent: 40, rpmRaw: 0), T0.AddSeconds(12));

        Assert.Equal(FanControlState.Manual, _guard.State);
    }

    [Fact]
    public void Missing_temperature_returns_to_auto()
    {
        _guard.Apply(new FanMode("조용히", 40, 40));

        _guard.Tick(null, T0);

        Assert.Equal(FanControlState.Auto, _guard.State);
        Assert.Contains("온도를 읽지 못해", _guard.StatusText);
    }

    [Fact]
    public void Duty_overwritten_by_firmware_is_applied_again()
    {
        _guard.Apply(new FanMode("조용히", 40, 40));

        _guard.Tick(Reading(celsius: 60, percent: 41), T0);
        Assert.Single(_control.Calls);

        _guard.Tick(Reading(celsius: 60, percent: 70), T0.AddSeconds(2));
        Assert.Equal(["duty 40/40", "duty 40/40"], _control.Calls);
    }

    [Fact]
    public void Ticks_do_nothing_in_auto()
    {
        _guard.Tick(Reading(celsius: 99, percent: 100), T0);
        _guard.Release();

        Assert.Empty(_control.Calls);
    }

    [Fact]
    public void Release_and_crash_recovery_restore_auto()
    {
        _guard.Apply(new FanMode("조용히", 40, 40));
        _guard.Release();
        Assert.Equal("auto", _control.Calls[^1]);
        Assert.False(File.Exists(_marker));

        // 사용자 지정 중 앱이 강제로 꺼진 경우: 표시 파일만 남아 있다.
        _guard.Apply(new FanMode("조용히", 40, 40));
        var next = new FanGuard(_control, _marker);
        Assert.True(next.RecoverAfterCrash());
        Assert.Equal("auto", _control.Calls[^1]);
        Assert.False(File.Exists(_marker));
        Assert.False(next.RecoverAfterCrash());
    }

    [Fact]
    public void Failed_auto_keeps_the_marker_for_the_next_launch()
    {
        _guard.Apply(new FanMode("조용히", 40, 40));
        _control.FailAuto = true;

        Assert.Throws<InvalidOperationException>(_guard.Release);
        Assert.True(File.Exists(_marker));
    }

    /// <summary>Fan1Info에 온도·듀티를, GetFan12RPM에 회전 주기를 넣은 읽기 값. rpmRaw 332 ≈ 6,494 RPM.</summary>
    internal static FanReading Reading(int celsius, int percent, uint rpmRaw = 332) => new(
    [
        ("Fan1Info", (uint)celsius << 16 | (uint)Math.Round(percent * 255 / 100.0)),
        ("GetFan12RPM", rpmRaw),
    ]);

    internal sealed class FakeFanControl : IFanControl
    {
        public List<string> Calls { get; } = [];
        public bool FailAuto { get; set; }

        public void SetDuty(int cpuPercent, int gpuPercent) => Calls.Add($"duty {cpuPercent}/{gpuPercent}");

        public void SetAuto()
        {
            if (FailAuto)
                throw new InvalidOperationException("auto failed");
            Calls.Add("auto");
        }
    }
}

public sealed class FanModeStoreTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Modes_round_trip_and_bad_values_are_cleaned()
    {
        var store = new FanModeStore(Path.Combine(_box.Root, "sub", "fan-modes.json"));
        Assert.Empty(store.Load());

        store.Save([new FanMode(" 조용히 ", 30, 10), new FanMode("게임", 90, 200), new FanMode("게임", 50, 50), new FanMode(" ", 50, 50)]);

        var loaded = store.Load();
        Assert.Equal([new FanMode("조용히", 30, 25), new FanMode("게임", 90, 100)], loaded);
    }

    [Fact]
    public void Broken_file_is_ignored()
    {
        var path = Path.Combine(_box.Root, "fan-modes.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(new FanModeStore(path).Load());
    }
}

public sealed class FanControlViewModelTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FanGuardTests.FakeFanControl _control = new();
    private readonly FanModeStore _store;
    private readonly FanControlViewModel _vm;

    public FanControlViewModelTests()
    {
        _store = new FanModeStore(Path.Combine(_box.Root, "fan-modes.json"));
        _vm = new FanControlViewModel(new FanGuard(_control, Path.Combine(_box.Root, "fan-manual.flag")), _store, isElevated: true);
    }

    public void Dispose() => _box.Dispose();

    [Fact]
    public async Task Nothing_changes_until_a_reading_shows_the_fan_interface()
    {
        Assert.False(_vm.CanControl);
        _vm.SelectedMode = _vm.Modes.Single(m => m.Kind == FanModeKind.Max);

        await _vm.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task Free_mode_uses_sliders_and_auto_button_restores_firmware_control()
    {
        await _vm.OnReadingAsync(FanGuardTests.Reading(celsius: 60, percent: 50));
        _vm.SelectedMode = _vm.Modes.Single(m => m.Kind == FanModeKind.Free);
        _vm.CpuPercent = 45;
        _vm.GpuPercent = 35;

        await _vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(["duty 45/35"], _control.Calls);
        Assert.True(_vm.IsManual);
        Assert.Contains("직접 설정", _vm.ControlText);

        await _vm.RestoreAutoCommand.ExecuteAsync(null);

        Assert.Equal("auto", _control.Calls[^1]);
        Assert.False(_vm.IsManual);
        Assert.Equal(FanModeKind.Auto, _vm.SelectedMode?.Kind);
    }

    [Fact]
    public async Task Saved_modes_persist_load_their_values_and_can_be_deleted()
    {
        await _vm.OnReadingAsync(FanGuardTests.Reading(celsius: 60, percent: 50));
        _vm.SelectedMode = _vm.Modes.Single(m => m.Kind == FanModeKind.Free);
        _vm.CpuPercent = 40;
        _vm.GpuPercent = 30;
        _vm.NewModeName = "조용히";

        _vm.SaveModeCommand.Execute(null);

        Assert.Equal([new FanMode("조용히", 40, 30)], _store.Load());
        Assert.Equal("조용히", _vm.SelectedMode?.Name);
        Assert.Equal("", _vm.NewModeName);
        Assert.Empty(_control.Calls); // 저장만 하고 팬은 바꾸지 않는다.

        // 다른 모드를 골랐다가 돌아오면 저장한 값이 슬라이더에 들어온다.
        _vm.SelectedMode = _vm.Modes.Single(m => m.Kind == FanModeKind.Max);
        Assert.Equal(100, _vm.CpuPercent);
        _vm.SelectedMode = _vm.Modes.Single(m => m.Name == "조용히");
        Assert.Equal((40, 30), (_vm.CpuPercent, _vm.GpuPercent));

        // 이름을 비우고 저장하면 고른 모드를 덮어쓴다.
        _vm.CpuPercent = 50;
        _vm.SaveModeCommand.Execute(null);
        Assert.Equal([new FanMode("조용히", 50, 30)], _store.Load());

        await _vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(["duty 50/30"], _control.Calls);

        _vm.DeleteModeCommand.Execute(null);
        Assert.Empty(_store.Load());
        Assert.DoesNotContain(_vm.Modes, m => m.Kind == FanModeKind.Saved);
        Assert.True(_vm.IsManual); // 지워도 지금 걸린 속도는 그대로다.
    }

    [Theory]
    [InlineData("")]
    [InlineData("최대")]
    [InlineData(FanControlViewModel.AutoName)]
    public void Saving_needs_a_name_that_is_not_a_built_in(string name)
    {
        _vm.NewModeName = name;

        _vm.SaveModeCommand.Execute(null);

        Assert.Empty(_store.Load());
        Assert.NotEqual("", _vm.MessageText);
    }

    [Fact]
    public async Task Readings_drive_the_guard_while_manual()
    {
        await _vm.OnReadingAsync(FanGuardTests.Reading(celsius: 60, percent: 50));
        _vm.SelectedMode = _vm.Modes.Single(m => m.Kind == FanModeKind.Free);
        await _vm.ApplyCommand.ExecuteAsync(null);

        await _vm.OnReadingAsync(FanGuardTests.Reading(celsius: 95, percent: 60));

        Assert.Equal(FanControlState.Protecting, _vm.State);
        Assert.Contains("과열 보호", _vm.ControlText);
        Assert.True(_vm.IsManual);
    }

    [Fact]
    public async Task Hardware_errors_fall_back_to_auto_and_explain()
    {
        var failing = new FanControlViewModel(
            new FanGuard(new ThrowingControl(), Path.Combine(_box.Root, "other.flag")), _store, isElevated: true);
        await failing.OnReadingAsync(FanGuardTests.Reading(celsius: 60, percent: 50));
        failing.SelectedMode = failing.Modes.Single(m => m.Kind == FanModeKind.Max);

        await failing.ApplyCommand.ExecuteAsync(null);

        Assert.False(failing.IsManual);
        Assert.Contains("자동 제어로 되돌렸습니다", failing.MessageText);
    }

    private sealed class ThrowingControl : IFanControl
    {
        public void SetDuty(int cpuPercent, int gpuPercent) => throw new InvalidOperationException("WMI 오류");

        public void SetAuto()
        {
        }
    }
}
