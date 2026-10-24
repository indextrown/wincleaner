using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.Memory;
using WinCleaner.Safety;

namespace WinCleaner.Tests;

public sealed class MemoryViewModelTests : IDisposable
{
    private const long Gb = 1L << 30;

    private readonly Sandbox _box = new();
    private readonly FakeMemory _memory = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ChangeJournal _journal;

    public MemoryViewModelTests()
    {
        _journal = new ChangeJournal(Path.Combine(_box.Root, "journal.json"));
    }

    public void Dispose() => _box.Dispose();

    private MemoryViewModel Create(bool isElevated = true) => new(_memory, _dialogs, _journal, isElevated);

    [Fact]
    public async Task Refresh_groups_programs_and_fills_summary()
    {
        _memory.Processes = [new(1, "chrome", 2 * Gb, Gb), new(2, "chrome", Gb, Gb), new(3, "notepad", Gb / 2, Gb / 4)];
        var vm = Create();

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["Google Chrome", "notepad"], vm.Groups.Select(g => g.Title));
        Assert.Equal("chrome · 프로세스 2개", vm.Groups[0].Subtitle);
        Assert.Equal("3 GB", vm.Groups[0].WorkingSetText);
        Assert.Equal(18.75, vm.Groups[0].Percent, precision: 2);
        Assert.Equal("10 GB / 16 GB", vm.UsedText);
        Assert.Equal("4 GB", vm.CacheText);
    }

    [Fact]
    public async Task Refresh_keeps_rows_and_selection_while_reordering()
    {
        _memory.Processes = [new(1, "chrome", 2 * Gb, Gb), new(3, "notepad", Gb, Gb)];
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        var notepad = vm.Groups.Single(g => g.Name == "notepad");
        notepad.IsSelected = true;

        _memory.Processes = [new(3, "notepad", 3 * Gb, Gb), new(4, "explorer", Gb, Gb)];
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Same(notepad, vm.Groups[0]);
        Assert.True(vm.Groups[0].IsSelected);
        Assert.Equal(["notepad", "explorer"], vm.Groups.Select(g => g.Name));
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task Trim_needs_selection_and_confirmation()
    {
        _memory.Processes = [new(1, "chrome", 2 * Gb, Gb), new(2, "chrome", Gb, Gb), new(3, "notepad", Gb, Gb)];
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.TrimSelectedCommand.CanExecute(null));

        vm.Groups.Single(g => g.Name == "chrome").IsSelected = true;
        Assert.True(vm.TrimSelectedCommand.CanExecute(null));

        _dialogs.Answer = false;
        await vm.TrimSelectedCommand.ExecuteAsync(null);
        Assert.Null(_memory.TrimmedIds);

        _dialogs.Answer = true;
        await vm.TrimSelectedCommand.ExecuteAsync(null);
        Assert.Equal([1, 2], _memory.TrimmedIds!.Order());
        Assert.Contains("Google Chrome", _dialogs.LastMessage);
        Assert.Contains("512 MB", vm.StatusText);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Contains(_journal.Entries, e => e.Kind == ChangeKind.Maintenance && e.Title.Contains("작업 집합"));
    }

    [Fact]
    public async Task Standby_purge_is_admin_only_and_recorded()
    {
        var standard = Create(isElevated: false);
        await standard.PurgeStandbyCommand.ExecuteAsync(null);
        Assert.False(standard.CanPurgeStandby);
        Assert.False(_memory.Purged);

        var admin = Create(isElevated: true);
        await admin.PurgeStandbyCommand.ExecuteAsync(null);
        Assert.True(_memory.Purged);
        Assert.Contains("1 GB", admin.StatusText);
        Assert.Contains(_journal.Entries, e => e.Title == "대기 목록 비우기");
    }

    private sealed class FakeMemory : IMemoryService
    {
        public IReadOnlyList<ProcessMemory> Processes { get; set; } = [];
        public IReadOnlyCollection<int>? TrimmedIds { get; private set; }
        public bool Purged { get; private set; }

        public SystemMemory ReadSystem() => new(16 * Gb, 6 * Gb, 4 * Gb, 12 * Gb, 24 * Gb);

        public IReadOnlyList<ProcessMemory> ReadProcesses() => Processes;

        public string? DescribeProcess(int processId) =>
            Processes.FirstOrDefault(p => p.Id == processId)?.Name == "chrome" ? "Google Chrome" : null;

        public TrimResult TrimWorkingSets(IReadOnlyCollection<int> processIds)
        {
            TrimmedIds = processIds;
            return new TrimResult(processIds.Count, 0, Gb / 2);
        }

        public PurgeResult PurgeStandbyList()
        {
            Purged = true;
            return new PurgeResult(true, "대기 목록을 비웠습니다.", Gb);
        }
    }

    private sealed class FakeDialogs : IUserDialogs
    {
        public bool Answer { get; set; } = true;
        public string LastMessage { get; private set; } = "";

        public bool Confirm(string title, string message)
        {
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
}
