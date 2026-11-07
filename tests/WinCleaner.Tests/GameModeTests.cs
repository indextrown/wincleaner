using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.Core.Optimization;
using WinCleaner.Memory;
using WinCleaner.Optimizer;
using WinCleaner.Optimizer.Fans;
using WinCleaner.Safety;

namespace WinCleaner.Tests;

public class QuickMemoryOptimizerTests
{
    private const long Mb = 1L << 20;

    [Fact]
    public void Trims_big_background_programs_but_keeps_game_system_and_voice_apps()
    {
        var memory = new FakeMemory(
        [
            new(10, "chrome", 900 * Mb, 0),
            new(11, "chrome", 60 * Mb, 0),
            new(12, "tiny", 10 * Mb, 0),
            new(20, "LeagueOfLegends", 2000 * Mb, 0),
            new(30, "dwm", 300 * Mb, 0),
            new(31, "audiodg", 80 * Mb, 0),
            new(40, "Discord", 400 * Mb, 0),
        ]);
        var optimizer = new QuickMemoryOptimizer(memory, new FakeForeground(20));

        var result = optimizer.Run(purgeStandby: true);

        Assert.Equal([10, 11], memory.TrimmedIds!.Order());
        Assert.Equal(["trim", "purge"], memory.Calls);
        Assert.True(result.StandbyPurged);
        Assert.Equal(["LeagueOfLegends"], result.KeptNames);
        Assert.Equal(10L << 30, result.UsedBefore);
        Assert.Equal(9L << 30, result.UsedAfter);
    }

    [Fact]
    public void Standby_purge_is_skipped_without_admin()
    {
        var memory = new FakeMemory([new(10, "chrome", 900 * Mb, 0)]);

        var result = new QuickMemoryOptimizer(memory, new FakeForeground()).Run(purgeStandby: false);

        Assert.Equal(["trim"], memory.Calls);
        Assert.False(result.StandbyPurged);
        Assert.Contains("관리자", result.StandbyMessage);
    }

    [Fact]
    public void Result_text_summarizes_what_happened()
    {
        var result = new QuickOptimizeResult(10L << 30, 9L << 30, true, "", 1L << 30, new TrimResult(5, 1, 512 * Mb), ["Game"]);

        var text = QuickOptimizeRunner.Describe(result);

        Assert.Equal("사용 중 10 GB → 9 GB · 프로그램 5개에서 512 MB 내보냄 · 대기 목록 1 GB 비움 · 지금 쓰는 프로그램은 그대로 둠: Game", text);
    }

    [Fact]
    public void Real_foreground_tracker_always_keeps_this_process()
    {
        using var tracker = new ForegroundApps();

        Assert.Contains(Environment.ProcessId, tracker.ProcessesToKeep());
        ForegroundApps.FullScreenProcesses();
    }

    internal sealed class FakeMemory(IReadOnlyList<ProcessMemory> processes) : IMemoryService
    {
        private bool _trimmed;

        public List<string> Calls { get; } = [];
        public IReadOnlyCollection<int>? TrimmedIds { get; private set; }

        public SystemMemory ReadSystem() => new(16L << 30, _trimmed ? 7L << 30 : 6L << 30, 3L << 30, 12L << 30, 24L << 30);
        public IReadOnlyList<ProcessMemory> ReadProcesses() => processes;
        public string? DescribeProcess(int processId) => null;

        public TrimResult TrimWorkingSets(IReadOnlyCollection<int> processIds)
        {
            Calls.Add("trim");
            TrimmedIds = processIds;
            _trimmed = true;
            return new TrimResult(processIds.Count, 0, 1L << 30);
        }

        public PurgeResult PurgeStandbyList()
        {
            Calls.Add("purge");
            return new PurgeResult(true, "대기 목록을 비웠습니다.", 2L << 30);
        }
    }

    internal sealed class FakeForeground(params int[] keep) : IForegroundApps
    {
        public IReadOnlySet<int> ProcessesToKeep() => keep.ToHashSet();
    }
}

public class FanReadingTests
{
    [Fact]
    public void Reading_exposes_values_by_method_name()
    {
        var reading = new FanReading([("GetFANCount", 2u), ("GetCPUFANDuty", 0x80u)]);

        Assert.Equal(2, reading.FanCount);
        Assert.Equal(0x80u, reading["GetCPUFANDuty"]);
        Assert.Null(reading["Missing"]);
        Assert.Contains("0x00000080", reading.RawText);
    }

    [Fact]
    public void Decodes_values_measured_on_the_Hansung_X58K()
    {
        // 2026-11-07 실제 기록: Windows 열 센서 97.1°C, 팬 최대.
        var reading = new FanReading([("Fan1Info", 0x00604CFFu), ("GetFan12RPM", 0x14Cu)]);

        Assert.Equal(96, reading.CpuTemperature);
        Assert.Equal(100, reading.FanPercent);
        Assert.Equal(6494, reading.FanRpm);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0xFFFFu)]
    [InlineData(0x000000FFu, 0u)]
    public void Nonsense_values_are_not_shown_as_temperature_or_rpm(uint fan1Info, uint rpmRaw)
    {
        var reading = new FanReading([("Fan1Info", fan1Info), ("GetFan12RPM", rpmRaw)]);

        Assert.Null(reading.CpuTemperature);
        Assert.Null(reading.FanRpm);
        if (fan1Info == 0xFFFFFFFF)
            Assert.Null(reading.FanPercent);
    }

    [Fact]
    public void Gpu_fan_is_not_detected_on_the_single_fan_Hansung_X58K()
    {
        // 실제 기록: 팬 수 1, Fan2Info 바이트 0 = 0, GetFan12RPM 위 16비트 = 0.
        var reading = new FanReading([("GetFANCount", 1u), ("Fan2Info", 0x00140100u), ("GetFan12RPM", 0x14Cu)]);

        Assert.False(reading.HasGpuFan);
        Assert.Equal(0, reading.GpuFanPercent);
        Assert.Null(reading.GpuFanRpm);
    }

    [Fact]
    public void Gpu_fan_is_decoded_from_fan2_and_upper_rpm_word()
    {
        var reading = new FanReading([("GetFANCount", 2u), ("Fan2Info", 0x00450080u), ("GetFan12RPM", 0x01AE014Cu)]);

        Assert.True(reading.HasGpuFan);
        Assert.Equal(50, reading.GpuFanPercent);
        Assert.Equal(5014, reading.GpuFanRpm);
        Assert.Equal(6494, reading.FanRpm);
    }

    [Fact]
    public void Missing_methods_give_no_decoded_values()
    {
        var reading = new FanReading([]);

        Assert.Null(reading.CpuTemperature);
        Assert.Null(reading.FanPercent);
        Assert.Null(reading.FanRpm);
    }

    [Fact]
    public void Real_monitor_returns_null_instead_of_throwing_without_admin()
    {
        // 관리자 권한으로 테스트를 돌리면 실제 값을 읽는다. 어느 쪽이든 예외가 나면 안 된다.
        new ClevoFanMonitor().Read();
    }
}

public sealed class GameModeViewModelTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakePower _power = new();
    private readonly TurboLimitTweakTests.FakeProcessorState _processor = new((100, 100), (100, 100));
    private readonly ChangeJournal _journal;
    private readonly GameModeViewModel _vm;

    public GameModeViewModelTests()
    {
        _journal = new ChangeJournal(Path.Combine(_box.Root, "journal.json"));
        var runner = new TweakRunner(_journal, new NoRestorePoints(), new YesDialogs());
        var memory = new QuickMemoryOptimizerTests.FakeMemory([new(10, "chrome", 900L << 20, 0)]);
        var quick = new QuickOptimizeRunner(new QuickMemoryOptimizer(memory, new QuickMemoryOptimizerTests.FakeForeground()), _journal, isElevated: true);
        var guard = new FanGuard(new FanGuardTests.FakeFanControl(), Path.Combine(_box.Root, "fan-manual.flag"));
        var modes = new FanModeStore(Path.Combine(_box.Root, "fan-modes.json"));
        var undervolt = new UndervoltViewModel(new NoUndervolt(), new NoUndervoltStartup(), new YesDialogs(), _journal, isElevated: false);
        _vm = new GameModeViewModel(runner, _power, quick, new NoFans(), guard, modes, new TurboLimitTweak(_processor), undervolt, isElevated: false);
    }

    public void Dispose()
    {
        _vm.Deactivate();
        _box.Dispose();
    }

    [Fact]
    public async Task Turning_on_switches_to_best_plan_cleans_memory_and_off_restores()
    {
        _vm.Activate();
        Assert.Equal("최고의 성능", _vm.GamePlan?.Name);
        Assert.False(_vm.IsGameModeOn);

        await _vm.TurnOnCommand.ExecuteAsync(null);
        Assert.Equal(FakePower.Ultimate, _power.Active);
        Assert.True(_vm.IsGameModeOn);
        Assert.Contains("프로그램 1개", _vm.MemoryResultText);
        Assert.Contains("게임 모드 켜짐", _vm.GameModeText);

        await _vm.TurnOffCommand.ExecuteAsync(null);
        Assert.Equal(FakePower.Balanced, _power.Active);
        Assert.False(_vm.IsGameModeOn);
        Assert.False(_vm.CanTurnOff);
    }

    [Fact]
    public async Task Turning_off_after_repeated_changes_returns_to_the_plan_before_the_first_change()
    {
        // 실제로 있었던 일: 같은 옵션으로 두 번 바꿔 두 번째 기록의 백업이 "최고의 성능"이 되었다.
        _power.SetActive(FakePower.Ultimate);
        _journal.Add(new ChangeEntry
        {
            Kind = ChangeKind.Tweak, Title = "전원 관리 옵션: 최고의 성능", TweakId = PowerPlanTweak.TweakId,
            Time = DateTime.Now.AddMinutes(-2), Backup = new Dictionary<string, string?> { ["scheme"] = FakePower.Balanced.ToString() },
        });
        _journal.Add(new ChangeEntry
        {
            Kind = ChangeKind.Tweak, Title = "전원 관리 옵션: 최고의 성능", TweakId = PowerPlanTweak.TweakId,
            Time = DateTime.Now.AddMinutes(-1), Backup = new Dictionary<string, string?> { ["scheme"] = FakePower.Ultimate.ToString() },
        });
        _vm.Activate();
        Assert.True(_vm.IsGameModeOn);

        await _vm.TurnOffCommand.ExecuteAsync(null);

        Assert.Equal(FakePower.Balanced, _power.Active);
        Assert.Null(_journal.LatestRevertible(PowerPlanTweak.TweakId));
    }

    [Fact]
    public async Task Turning_on_when_plan_is_already_active_records_nothing()
    {
        _power.SetActive(FakePower.Ultimate);
        _vm.Activate();

        await _vm.TurnOnCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_journal.Entries, e => e.TweakId == PowerPlanTweak.TweakId);
    }

    [Fact]
    public void Fan_section_explains_admin_requirement()
    {
        Assert.Contains("관리자", _vm.FanStatusText);
        Assert.False(_vm.HasFanReading);
        Assert.False(_vm.HasTemperature);
        Assert.Equal("", _vm.TemperatureAdvice);
    }

    [Fact]
    public async Task Overheating_shows_warning_and_suggests_turbo_limit_until_it_is_on()
    {
        _vm.Activate();
        _vm.FanReading = new FanReading([("Fan1Info", 0x00604CFFu), ("GetFan12RPM", 0x14Cu)]);

        Assert.True(_vm.IsOverheating);
        Assert.False(_vm.IsWarm);
        Assert.Equal("96°C", _vm.CpuTemperatureText);
        Assert.Equal("100%", _vm.FanSpeedText);
        Assert.Equal("약 6,494 RPM", _vm.FanRpmText);
        Assert.Contains("과열", _vm.TemperatureAdvice);
        Assert.Contains("팬은 이미 최대", _vm.TemperatureAdvice);
        Assert.Contains("터보 부스트 제한", _vm.TemperatureAdvice);

        await _vm.ToggleTurboCommand.ExecuteAsync(null);

        Assert.DoesNotContain("터보 부스트 제한", _vm.TemperatureAdvice);
    }

    [Fact]
    public void Normal_and_warm_temperatures_are_not_overheating()
    {
        _vm.FanReading = new FanReading([("Fan1Info", 0x00410080u)]);
        Assert.False(_vm.IsOverheating);
        Assert.Equal("정상 온도입니다.", _vm.TemperatureAdvice);

        _vm.FanReading = new FanReading([("Fan1Info", 0x00540080u)]);
        Assert.True(_vm.IsWarm);
        Assert.False(_vm.IsOverheating);
        Assert.Contains("뜨거운 편", _vm.TemperatureAdvice);
    }

    [Fact]
    public async Task Turbo_limit_toggles_on_and_back_to_original_values()
    {
        _vm.Activate();
        Assert.False(_vm.Turbo.IsApplied);

        await _vm.ToggleTurboCommand.ExecuteAsync(null);
        Assert.True(_vm.Turbo.IsApplied);
        Assert.All(_processor.Values.Values, v => Assert.Equal((99u, 99u), v));
        Assert.NotNull(_journal.LatestRevertible("turbo.limit"));

        await _vm.ToggleTurboCommand.ExecuteAsync(null);
        Assert.False(_vm.Turbo.IsApplied);
        Assert.All(_processor.Values.Values, v => Assert.Equal((100u, 100u), v));
        Assert.Null(_journal.LatestRevertible("turbo.limit"));
    }

    [Fact]
    public async Task Turbo_limit_set_elsewhere_turns_off_to_windows_default()
    {
        _processor.Values[_processor.Values.Keys.First()] = (80, 80);
        _processor.Values[_processor.Values.Keys.Last()] = (99, 99);
        _vm.Activate();
        Assert.True(_vm.Turbo.IsApplied);

        await _vm.ToggleTurboCommand.ExecuteAsync(null);

        Assert.All(_processor.Values.Values, v => Assert.Equal((100u, 100u), v));
    }

    private sealed class FakePower : IPowerSchemes
    {
        public static readonly Guid Balanced = PowerPlanTweak.Balanced;
        public static readonly Guid Ultimate = Guid.NewGuid();

        public Guid Active { get; private set; } = Balanced;

        public IReadOnlyList<PowerScheme> List() => [new(Balanced, "균형 조정"), new(Guid.NewGuid(), "고성능"), new(Ultimate, "최고의 성능")];
        public Guid GetActive() => Active;
        public void SetActive(Guid scheme) => Active = scheme;
    }

    private sealed class NoFans : IFanMonitor
    {
        public FanReading? Read() => null;
    }

    private sealed class NoUndervolt : WinCleaner.Optimizer.Cpu.IUndervolt
    {
        public bool IsAvailable => false;

        public Task<WinCleaner.Optimizer.Cpu.UndervoltState> ReadAsync(CancellationToken ct) => throw new InvalidOperationException();

        public Task<WinCleaner.Optimizer.Cpu.UndervoltState> ApplyAsync(int millivolts, CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class NoUndervoltStartup : WinCleaner.Optimizer.Cpu.IUndervoltStartup
    {
        public Task<WinCleaner.Optimizer.Cpu.UndervoltStartupState> ReadAsync(CancellationToken ct) => throw new InvalidOperationException();

        public Task RegisterAsync(int millivolts, CancellationToken ct) => throw new InvalidOperationException();

        public Task UnregisterAsync(CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class NoRestorePoints : IRestorePointService
    {
        public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct) =>
            Task.FromResult(new RestorePointResult(RestorePointOutcome.NotElevated, ""));

        public Task<IReadOnlyList<RestorePointInfo>?> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RestorePointInfo>?>(null);
    }

    private sealed class YesDialogs : IUserDialogs
    {
        public bool Confirm(string title, string message) => true;

        public void OpenFile(string path)
        {
        }

        public void ShowInExplorer(string path)
        {
        }

        public bool MoveToRecycleBin(string path, out string? error)
        {
            error = null;
            return false;
        }
    }
}

public class TurboLimitTweakTests
{
    [Fact]
    public async Task Apply_limits_every_scheme_to_99_and_revert_restores_each_value()
    {
        var processor = new FakeProcessorState((100, 100), (100, 50), (70, 99));
        var tweak = new TurboLimitTweak(processor);
        var before = processor.Values.ToDictionary();

        var backup = await tweak.ApplyAsync(CancellationToken.None);

        Assert.Equal([(99u, 99u), (99u, 50u), (70u, 99u)], processor.Values.Values);
        Assert.Equal(1, processor.ApplyActiveCount);
        var state = await tweak.GetStateAsync(CancellationToken.None);
        Assert.True(state.IsApplied);
        Assert.Contains("99%", state.StatusText);

        await tweak.RevertAsync(backup, CancellationToken.None);

        Assert.Equal(before, processor.Values);
        Assert.Equal(2, processor.ApplyActiveCount);
        Assert.False((await tweak.GetStateAsync(CancellationToken.None)).IsApplied);
    }

    [Fact]
    public async Task Revert_without_backup_or_with_bad_values_uses_windows_default_100()
    {
        var processor = new FakeProcessorState((99, 99));
        var scheme = processor.Values.Keys.Single();
        var tweak = new TurboLimitTweak(processor);

        await tweak.RevertAsync(null, CancellationToken.None);
        Assert.Equal((100u, 100u), processor.Values[scheme]);

        processor.Values[scheme] = (99, 99);
        var bad = new TweakBackup(tweak.Id, new Dictionary<string, string?> { [$"{scheme}:ac"] = "500", [$"{scheme}:dc"] = "-1" });
        await tweak.RevertAsync(bad, CancellationToken.None);
        Assert.Equal((100u, 100u), processor.Values[scheme]);
    }

    [Fact]
    public async Task Revert_rejects_another_tweaks_backup()
    {
        var tweak = new TurboLimitTweak(new FakeProcessorState((99, 99)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            tweak.RevertAsync(new TweakBackup(PowerPlanTweak.TweakId, new Dictionary<string, string?>()), CancellationToken.None));
    }

    [Fact]
    public void Real_power_settings_can_be_read()
    {
        // 읽기만 한다. 실제 전원 설정은 바꾸지 않는다.
        var processor = new PowrProfProcessorState();

        var schemes = processor.Schemes();

        Assert.NotEmpty(schemes);
        Assert.All(schemes, s =>
        {
            var (ac, dc) = processor.Read(s);
            Assert.InRange(ac, 0u, 100u);
            Assert.InRange(dc, 0u, 100u);
        });
    }

    internal sealed class FakeProcessorState : IProcessorStateSettings
    {
        public FakeProcessorState(params (uint Ac, uint Dc)[] values)
        {
            foreach (var value in values)
                Values[Guid.NewGuid()] = value;
        }

        public Dictionary<Guid, (uint Ac, uint Dc)> Values { get; } = [];
        public int ApplyActiveCount { get; private set; }

        public IReadOnlyList<Guid> Schemes() => Values.Keys.ToList();
        public (uint Ac, uint Dc) Read(Guid scheme) => Values[scheme];
        public void Write(Guid scheme, uint ac, uint dc) => Values[scheme] = (ac, dc);
        public void ApplyActive() => ApplyActiveCount++;
    }
}