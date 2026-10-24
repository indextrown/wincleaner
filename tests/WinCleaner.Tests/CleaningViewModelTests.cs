using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.Core;
using WinCleaner.Core.Cleaning;
using WinCleaner.Safety;

namespace WinCleaner.Tests;

public sealed class CleaningViewModelTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _box.Dispose();

    private ChangeJournal Journal => new(Path.Combine(_box.Root, "journal.json"));

    private CleaningViewModel Create(bool isElevated, params ICleanupRule[] rules) =>
        new(rules, _dialogs, isElevated, Path.Combine(_box.Root, "logs"), Journal);

    [Fact]
    public async Task Scan_selects_low_risk_rules_and_sums_their_size()
    {
        var vm = Create(isElevated: true, new FakeRule("a", 1000), new FakeRule("b", 500), new FakeRule("c", 200, RiskLevel.Medium));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal([true, true, false], vm.Rules.Select(r => r.IsSelected));
        Assert.Equal(1500, vm.SelectedBytes);
        Assert.True(vm.CleanCommand.CanExecute(null));
        Assert.False(vm.HasSummary);
    }

    [Fact]
    public async Task Admin_rule_is_scan_only_without_elevation()
    {
        var admin = new FakeRule("admin", 1000, requiresElevation: true);
        var vm = Create(isElevated: false, admin);

        await vm.ScanCommand.ExecuteAsync(null);

        var item = vm.Rules.Single();
        Assert.False(item.CanClean);
        Assert.False(item.IsSelected);
        Assert.Equal(1000, item.FoundBytes);
        Assert.Contains("관리자", item.WarningText);
        Assert.False(vm.CleanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Nothing_is_cleaned_when_user_cancels_confirmation()
    {
        var rule = new FakeRule("a", 1000);
        var vm = Create(isElevated: true, rule);
        await vm.ScanCommand.ExecuteAsync(null);
        _dialogs.Answer = false;

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal(1, _dialogs.ConfirmCount);
        Assert.Equal(0, rule.CleanCalls);
        Assert.Null(vm.Summary);
    }

    [Fact]
    public async Task Clean_runs_only_selected_rules_and_writes_log()
    {
        var a = new FakeRule("a", 1000);
        var b = new FakeRule("b", 2000);
        var vm = Create(isElevated: true, a, b);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.Rules[1].IsSelected = false;

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal(1, a.CleanCalls);
        Assert.Equal(0, b.CleanCalls);
        Assert.NotNull(vm.Summary);
        Assert.True(vm.HasSummary);
        Assert.Contains("1000 B", vm.Summary.Title);
        Assert.True(File.Exists(vm.Summary.LogPath));
        Assert.Contains("1000 B 정리함", vm.Rules[0].CleanedText);
        Assert.Equal(0, vm.SelectedBytes);

        var entry = Assert.Single(Journal.Entries);
        Assert.Equal(ChangeKind.Cleanup, entry.Kind);
        Assert.Equal(vm.Summary.LogPath, entry.LogPath);
        Assert.False(entry.CanRevert);
    }

    [Fact]
    public async Task Medium_risk_rule_is_not_selected_and_is_called_out_in_confirmation()
    {
        var bin = new FakeRule("bin", 500, RiskLevel.Medium);
        var vm = Create(isElevated: true, new FakeRule("a", 1000), bin);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.False(vm.Rules[1].IsSelected);
        vm.Rules[1].IsSelected = true;
        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Contains("주의 항목", _dialogs.LastMessage);
        Assert.Contains("bin", _dialogs.LastMessage);
        Assert.Equal(1, bin.CleanCalls);
    }

    [Fact]
    public async Task Preview_shows_display_path_when_rule_provides_one()
    {
        var rule = new FakeRule("bin", 10) { DisplayPath = @"C:\Users\me\Documents\report.docx" };
        var vm = Create(isElevated: true, rule);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Users\me\Documents\report.docx", vm.Rules.Single().PreviewItems.Single().Path);
    }

    [Fact]
    public async Task Rule_that_throws_during_scan_shows_warning_instead_of_crashing()
    {
        var vm = Create(isElevated: true, new FakeRule("broken", 0) { ScanError = new IOException("디스크 오류") });

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains("디스크 오류", vm.Rules.Single().WarningText);
        Assert.False(vm.IsBusy);
    }

    private sealed class FakeDialogs : IUserDialogs
    {
        public bool Answer { get; set; } = true;
        public int ConfirmCount { get; private set; }
        public string LastMessage { get; private set; } = "";

        public bool Confirm(string title, string message)
        {
            ConfirmCount++;
            LastMessage = message;
            return Answer;
        }

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

    private sealed class FakeRule(string id, long bytes, RiskLevel risk = RiskLevel.Low, bool requiresElevation = false)
        : ICleanupRule
    {
        public int CleanCalls { get; private set; }
        public Exception? ScanError { get; init; }
        public string? DisplayPath { get; init; }

        public string Id => id;
        public string Name => id;
        public string Description => "";
        public string Category => "테스트";
        public RiskLevel Risk => risk;
        public bool RequiresElevation => requiresElevation;

        public Task<ScanResult> ScanAsync(CancellationToken ct) =>
            ScanError is not null
                ? Task.FromException<ScanResult>(ScanError)
                : Task.FromResult(new ScanResult(Id, [new ScanItem($@"C:\fake\{id}.tmp", bytes, DateTime.UtcNow, DisplayPath)], []));

        public Task<CleanResult> CleanAsync(ScanResult selected, CancellationToken ct)
        {
            CleanCalls++;
            return Task.FromResult(new CleanResult(Id, selected.Items, []));
        }
    }
}
