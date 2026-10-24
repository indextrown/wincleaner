using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.Core;
using WinCleaner.Core.Optimization;
using WinCleaner.Optimizer;
using WinCleaner.Optimizer.Drives;
using WinCleaner.Optimizer.Startup;
using WinCleaner.Safety;

namespace WinCleaner.Tests;

/// <summary>복원 지점 → 적용 → 기록 → 되돌리기 흐름 (사양서 3.4).</summary>
public sealed class SafeChangeTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly ChangeJournal _journal;
    private readonly FakeRestorePoints _restorePoints = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeTweak _tweak = new();
    private readonly FakeStartupStore _startup = new();
    private readonly FakePower _power = new();
    private readonly FakeDrives _drives = new();
    private readonly TweakRunner _runner;
    private readonly TweakCatalog _catalog;

    public SafeChangeTests()
    {
        _journal = new ChangeJournal(Path.Combine(_box.Root, "journal.json"));
        _runner = new TweakRunner(_journal, _restorePoints, _dialogs);
        _catalog = new TweakCatalog([_tweak], _startup, _power);
    }

    public void Dispose() => _box.Dispose();

    private OptimizeViewModel CreateOptimize(bool isElevated = true) => new(_catalog, _drives, _runner, _dialogs, isElevated);

    [Fact]
    public async Task Apply_creates_restore_point_first_then_records_backup()
    {
        var applied = await _runner.ApplyAsync(_tweak, createRestorePoint: true, _ => { }, default);

        Assert.True(applied);
        Assert.Equal(["restore point", "apply"], _restorePoints.Calls.Concat(_tweak.Calls));
        var entries = _journal.Entries;
        Assert.Contains(entries, e => e.Kind == ChangeKind.RestorePoint);
        var change = entries.Single(e => e.Kind == ChangeKind.Tweak);
        Assert.Equal("fake", change.TweakId);
        Assert.Equal("before", change.Backup?["value"]);
    }

    [Fact]
    public async Task Failed_restore_point_asks_and_stops_when_user_declines()
    {
        _restorePoints.Outcome = RestorePointOutcome.Disabled;
        _dialogs.Answers.Enqueue(false);

        var applied = await _runner.ApplyAsync(_tweak, createRestorePoint: true, _ => { }, default);

        Assert.False(applied);
        Assert.Empty(_tweak.Calls);
        Assert.Empty(_journal.Entries);
        Assert.Contains("시스템 보호", _dialogs.LastMessage);
    }

    [Fact]
    public async Task Failed_restore_point_continues_when_user_agrees()
    {
        _restorePoints.Outcome = RestorePointOutcome.Failed;
        _dialogs.Answers.Enqueue(true);

        var applied = await _runner.ApplyAsync(_tweak, createRestorePoint: true, _ => { }, default);

        Assert.True(applied);
        Assert.DoesNotContain(_journal.Entries, e => e.Kind == ChangeKind.RestorePoint);
        Assert.Single(_journal.Entries, e => e.Kind == ChangeKind.Tweak);
    }

    [Fact]
    public async Task Recent_restore_point_is_used_without_asking()
    {
        _restorePoints.Outcome = RestorePointOutcome.RecentExists;

        var applied = await _runner.ApplyAsync(_tweak, createRestorePoint: true, _ => { }, default);

        Assert.True(applied);
        Assert.Equal(0, _dialogs.ConfirmCount);
    }

    [Fact]
    public async Task Revert_uses_recorded_backup_and_marks_entry()
    {
        await _runner.ApplyAsync(_tweak, createRestorePoint: false, _ => { }, default);
        var entry = _journal.LatestRevertible("fake");

        await _runner.RevertAsync(_tweak, entry, default);

        Assert.Equal("before", _tweak.RevertedWith?.Values["value"]);
        Assert.Null(_journal.LatestRevertible("fake"));
        Assert.Contains(_journal.Entries, e => e.Kind == ChangeKind.Revert);
    }

    [Fact]
    public async Task Optimize_screen_applies_then_reverts_with_confirmation()
    {
        var vm = CreateOptimize();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Tweaks.Single();
        Assert.Equal("적용", item.ActionText);

        await vm.ToggleTweakCommand.ExecuteAsync(item);
        Assert.True(item.IsApplied);
        Assert.Equal("되돌리기", item.ActionText);
        Assert.Equal(["restore point"], _restorePoints.Calls);

        await vm.ToggleTweakCommand.ExecuteAsync(item);
        Assert.False(item.IsApplied);
        Assert.Equal(2, _dialogs.ConfirmCount);
        Assert.Equal("before", _tweak.RevertedWith?.Values["value"]);
    }

    [Fact]
    public async Task Optimize_screen_blocks_admin_tweak_without_elevation()
    {
        var vm = CreateOptimize(isElevated: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Tweaks.Single();

        await vm.ToggleTweakCommand.ExecuteAsync(item);

        Assert.False(item.CanChange);
        Assert.Contains("관리자", item.WarningText);
        Assert.Empty(_tweak.Calls);
    }

    [Fact]
    public async Task Startup_switch_turns_entry_off_and_back_on_through_the_journal()
    {
        _startup.Add(StartupLocation.UserRun, "Discord", @"C:\Apps\Discord.exe");
        _startup.Add(StartupLocation.MachineRun, "Vendor", @"C:\Apps\Vendor.exe");
        var vm = CreateOptimize(isElevated: false);
        await vm.RefreshCommand.ExecuteAsync(null);

        var discord = vm.StartupItems.Single(i => i.DisplayName == "Discord");
        Assert.True(discord.IsOn);
        Assert.False(vm.StartupItems.Single(i => i.DisplayName == "Vendor").CanChange);

        await vm.ToggleStartupCommand.ExecuteAsync(discord);
        Assert.False(vm.StartupItems.Single(i => i.DisplayName == "Discord").IsOn);
        Assert.Empty(_restorePoints.Calls);
        Assert.Contains("꺼진 항목 1개", vm.StartupSummary);

        await vm.ToggleStartupCommand.ExecuteAsync(vm.StartupItems.Single(i => i.DisplayName == "Discord"));
        Assert.True(vm.StartupItems.Single(i => i.DisplayName == "Discord").IsOn);
        Assert.Null(_startup.ReadApproval(StartupLocation.UserRun, "Discord"));
    }

    [Fact]
    public async Task History_can_revert_a_startup_change_by_rebuilding_it_from_its_id()
    {
        _startup.Add(StartupLocation.UserRun, "Discord", @"C:\Apps\Discord.exe");
        var optimize = CreateOptimize();
        await optimize.RefreshCommand.ExecuteAsync(null);
        await optimize.ToggleStartupCommand.ExecuteAsync(optimize.StartupItems.Single());
        var history = new HistoryViewModel(_journal, _runner, _catalog, _restorePoints, _dialogs, isElevated: true);

        var change = history.Entries.Single(e => e.Entry.Kind == ChangeKind.Tweak);
        Assert.True(change.CanRevert);
        await history.RevertCommand.ExecuteAsync(change);

        Assert.True(StartupApproval.IsEnabled(_startup.ReadApproval(StartupLocation.UserRun, "Discord")));
    }

    [Fact]
    public async Task Power_plan_can_be_changed_and_reverted_from_the_screen()
    {
        var vm = CreateOptimize();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("현재: 균형 조정", vm.PowerStatusText);
        Assert.False(vm.CanRevertPowerPlan);

        vm.SelectedPowerScheme = vm.PowerSchemes.Single(s => s.Name == "고성능");
        await vm.ChangePowerPlanCommand.ExecuteAsync(null);
        Assert.Equal("현재: 고성능", vm.PowerStatusText);
        Assert.True(vm.CanRevertPowerPlan);

        await vm.RevertPowerPlanCommand.ExecuteAsync(null);
        Assert.Equal("현재: 균형 조정", vm.PowerStatusText);
        Assert.False(vm.CanRevertPowerPlan);
    }

    [Fact]
    public async Task Drive_optimization_reports_progress_and_is_recorded()
    {
        var vm = CreateOptimize();
        await vm.RefreshCommand.ExecuteAsync(null);
        var drive = vm.DriveItems.Single();
        Assert.Equal("TRIM 실행", drive.ActionText);

        await vm.OptimizeDriveCommand.ExecuteAsync(drive);

        Assert.Equal(['C'], _drives.Optimized);
        Assert.Contains("완료", drive.ProgressText);
        Assert.Contains(_journal.Entries, e => e.Kind == ChangeKind.Maintenance);
    }

    [Fact]
    public async Task Drive_optimization_requires_elevation()
    {
        var vm = CreateOptimize(isElevated: false);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.OptimizeDriveCommand.ExecuteAsync(vm.DriveItems.Single());

        Assert.Empty(_drives.Optimized);
    }

    [Fact]
    public async Task History_screen_lists_entries_and_reverts_a_change()
    {
        await _runner.ApplyAsync(_tweak, createRestorePoint: false, _ => { }, default);
        _journal.Add(new ChangeEntry { Kind = ChangeKind.Cleanup, Title = "디스크 청소" });
        var vm = new HistoryViewModel(_journal, _runner, _catalog, _restorePoints, _dialogs, isElevated: true);

        Assert.Equal(2, vm.Entries.Count);
        var change = vm.Entries.Single(e => e.Entry.Kind == ChangeKind.Tweak);
        Assert.True(change.CanRevert);
        Assert.False(vm.Entries.Single(e => e.Entry.Kind == ChangeKind.Cleanup).CanRevert);

        await vm.RevertCommand.ExecuteAsync(change);

        Assert.Equal(3, vm.Entries.Count);
        Assert.False(vm.Entries.Single(e => e.Entry.Kind == ChangeKind.Tweak).CanRevert);
        Assert.Contains("되돌림", vm.Entries.Single(e => e.Entry.Kind == ChangeKind.Tweak).StatusText);
    }

    [Fact]
    public async Task Restore_point_service_refuses_without_elevation()
    {
        var service = new WmiRestorePointService(isElevated: false);

        var result = await service.CreateAsync("test", default);

        Assert.Equal(RestorePointOutcome.NotElevated, result.Outcome);
        Assert.Null(await service.ListAsync(default));
    }

    private sealed class FakeTweak : ITweak
    {
        private bool _applied;

        public List<string> Calls { get; } = [];
        public TweakBackup? RevertedWith { get; private set; }

        public string Id => "fake";
        public string Name => "가짜 설정";
        public string Description => "";
        public RiskLevel Risk => RiskLevel.Medium;
        public bool RequiresElevation => true;
        public bool RequiresRestart => false;

        public Task<TweakState> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(new TweakState(_applied, _applied ? "적용됨" : "기본값"));

        public Task<TweakBackup> ApplyAsync(CancellationToken ct)
        {
            Calls.Add("apply");
            _applied = true;
            return Task.FromResult(new TweakBackup(Id, new Dictionary<string, string?> { ["value"] = "before" }));
        }

        public Task RevertAsync(TweakBackup? backup, CancellationToken ct)
        {
            Calls.Add("revert");
            RevertedWith = backup;
            _applied = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePower : IPowerSchemes
    {
        private readonly PowerScheme[] _schemes =
        [
            new(PowerPlanTweak.Balanced, "균형 조정"),
            new(Guid.Parse("48f12988-250d-4c2b-83e6-7356136fddc6"), "고성능"),
        ];

        private Guid _active = PowerPlanTweak.Balanced;

        public IReadOnlyList<PowerScheme> List() => _schemes;
        public Guid GetActive() => _active;
        public void SetActive(Guid scheme) => _active = scheme;
    }

    private sealed class FakeDrives : IDriveService
    {
        public List<char> Optimized { get; } = [];

        public IReadOnlyList<DriveDetails> ReadDrives() =>
            [new DriveDetails('C', "Test SSD", MediaKind.Ssd, "SATA", DiskHealth.Healthy)];

        public Task<string> OptimizeAsync(char letter, Action<string>? onProgress, CancellationToken ct)
        {
            Optimized.Add(letter);
            onProgress?.Invoke("50% 완료");
            return Task.FromResult("작업을 완료했습니다.");
        }
    }

    private sealed class FakeRestorePoints : IRestorePointService
    {
        public RestorePointOutcome Outcome { get; set; } = RestorePointOutcome.Created;
        public List<string> Calls { get; } = [];

        public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
        {
            Calls.Add("restore point");
            var message = Outcome == RestorePointOutcome.Disabled ? "시스템 보호가 꺼져 있습니다." : Outcome.ToString();
            return Task.FromResult(new RestorePointResult(Outcome, message));
        }

        public Task<IReadOnlyList<RestorePointInfo>?> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RestorePointInfo>?>([]);
    }

    private sealed class FakeDialogs : IUserDialogs
    {
        public Queue<bool> Answers { get; } = new();
        public int ConfirmCount { get; private set; }
        public string LastMessage { get; private set; } = "";

        public bool Confirm(string title, string message)
        {
            ConfirmCount++;
            LastMessage = message;
            return Answers.Count == 0 || Answers.Dequeue();
        }

        public void OpenFile(string path)
        {
        }

        public void ShowInExplorer(string path)
        {
        }
    }
}
