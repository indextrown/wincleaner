using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.Cleaning;
using WinCleaner.Cleaning.Analysis;
using WinCleaner.Optimizer.Programs;
using WinCleaner.Safety;

namespace WinCleaner.Tests;

public class DeletionGuardTests
{
    private static readonly DeletionGuard Guard = new(
        blockedTrees: [@"C:\Windows"],
        blockedFolders: [@"C:\Program Files", @"C:\Users", @"C:\Users\me", @"C:\Users\me\Documents"],
        cautionTrees: [@"C:\Program Files"]);

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\setup.exe")]
    [InlineData(@"C:\Users\me\Documents\old-project")]
    [InlineData(@"C:\Riot Games")]
    [InlineData(@"D:\Movies\big.mkv")]
    [InlineData(@"C:\Program Files\Leftover")]
    public void Allows_user_chosen_items(string path)
    {
        Assert.Null(Guard.BlockReason(path));
    }

    [Theory]
    [InlineData(@"C:\", "최상위")]
    [InlineData(@"C:\Windows", "Windows 폴더")]
    [InlineData(@"C:\Windows\System32\drivers", "Windows 폴더")]
    [InlineData(@"C:\pagefile.sys", "Windows가 쓰는")]
    [InlineData(@"D:\System Volume Information", "Windows가 쓰는")]
    [InlineData(@"C:\Program Files", "기본 폴더")]
    [InlineData(@"C:\Users", "기본 폴더")]
    [InlineData(@"C:\Users\me", "기본 폴더")]
    [InlineData(@"C:\Users\me\Documents\", "기본 폴더")]
    public void Blocks_system_and_default_folders(string path, string expected)
    {
        Assert.Contains(expected, Guard.BlockReason(path));
    }

    [Fact]
    public void Program_files_contents_get_a_caution()
    {
        Assert.NotNull(Guard.Caution(@"C:\Program Files\Leftover"));
        Assert.Null(Guard.Caution(@"C:\Users\me\Downloads\a.zip"));
    }

    [Fact]
    public void Current_user_guard_blocks_windows_and_profile_but_allows_temp_files()
    {
        var guard = DeletionGuard.ForCurrentUser();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.NotNull(guard.BlockReason(windows));
        Assert.NotNull(guard.BlockReason(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.Null(guard.BlockReason(Path.Combine(Path.GetTempPath(), "something.tmp")));
    }
}

public sealed class FolderTreeEditTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public async Task Find_and_remove_update_sizes_up_the_tree()
    {
        _box.File(@"scan\a\b\big.bin", TimeSpan.FromDays(1), size: 1000);
        _box.File(@"scan\a\small.bin", TimeSpan.FromDays(1), size: 10);
        var report = await new DiskUsageScanner().ScanAsync(_box.Folder("scan"), null, default);

        var b = report.Root.Find(Path.Combine(_box.Root, @"scan\a\b"));
        Assert.NotNull(b);
        Assert.Null(report.Root.Find(Path.Combine(_box.Root, "elsewhere")));

        b.Parent!.RemoveChild(b);
        Assert.Equal(10, report.Root.SizeBytes);
        Assert.Equal(1, report.Root.FileCount);

        report.Root.Find(Path.Combine(_box.Root, @"scan\a"))!.RemoveOwnFile(10);
        Assert.Equal(0, report.Root.SizeBytes);
    }

    [Fact]
    public async Task Real_recycle_bin_round_trip()
    {
        // 실제 휴지통을 쓰므로 직접 켤 때만 실행한다: WINCLEANER_REAL_RECYCLE=1
        if (Environment.GetEnvironmentVariable("WINCLEANER_REAL_RECYCLE") != "1")
            return;

        var file = _box.File("recycle-me.txt", TimeSpan.Zero, size: 10);
        Assert.True(ShellRecycleBin.Send(file, 0, out var error), error);
        Assert.False(File.Exists(file));

        // 테스트가 휴지통에 넣은 항목만 찾아 지운다.
        var rule = new RecycleBinRule();
        var scan = await rule.ScanAsync(default);
        var mine = scan.Items.Where(i => string.Equals(i.ShownPath, file, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(mine);
        Assert.Equal(1, (await rule.CleanAsync(scan with { Items = mine }, default)).DeletedCount);
    }
}

public class UninstallCommandTests
{
    private static readonly string System32 = Environment.SystemDirectory;

    [Fact]
    public void Quoted_path_is_split_from_arguments()
    {
        Assert.Equal((@"C:\Program Files\App\unins000.exe", "/SILENT"),
            UninstallCommand.Parse(@"""C:\Program Files\App\unins000.exe"" /SILENT"));
        Assert.Equal((@"C:\App\app.exe", ""), UninstallCommand.Parse(@"""C:\App\app.exe"""));
    }

    [Fact]
    public void Unquoted_path_with_spaces_is_split_after_exe()
    {
        Assert.Equal((@"C:\Program Files\App\uninstall.exe", "--remove"),
            UninstallCommand.Parse(@"C:\Program Files\App\uninstall.exe --remove"));
    }

    [Fact]
    public void Msi_repair_switch_becomes_uninstall_and_msiexec_comes_from_system32()
    {
        Assert.Equal((Path.Combine(System32, "MsiExec.exe"), "/X{1234-ABCD}"),
            UninstallCommand.Parse("MsiExec.exe /I{1234-ABCD}"));
        Assert.Equal((Path.Combine(System32, "MsiExec.exe"), "/X{1234-ABCD}"),
            UninstallCommand.Parse("MsiExec.exe /X{1234-ABCD}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("notepad")]
    [InlineData("not-a-windows-tool.exe /x")]
    [InlineData(@"""C:\unterminated.exe")]
    public void Unusable_commands_are_rejected(string command)
    {
        Assert.Null(UninstallCommand.Parse(command));
    }

    [Fact]
    public void Real_catalog_lists_this_pcs_programs_without_system_components()
    {
        var programs = new RegistryProgramCatalog().List();

        Assert.NotEmpty(programs);
        Assert.All(programs, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.Equal(programs.Count, programs.Select(p => (p.Name.ToUpperInvariant(), p.Version)).Distinct().Count());
    }
}

public sealed class ProgramsViewModelTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeUninstaller _uninstaller = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ChangeJournal _journal;

    public ProgramsViewModelTests()
    {
        _journal = new ChangeJournal(Path.Combine(_box.Root, "journal.json"));
        _catalog.Programs =
        [
            Program("game", "Big Game", "Game Co", @"""C:\Games\uninstall.exe"" /S", 40L << 30),
            Program("chat", "Chat App", "Chat Inc", @"""C:\Chat\Update.exe"" --uninstall", null, perUser: true),
            Program("broken", "Broken", null, "uninstall-me", null),
        ];
        _uninstaller.OnRun = () => _catalog.Programs = _catalog.Programs.Where(p => p.Key != "game").ToList();
    }

    public void Dispose() => _box.Dispose();

    private ProgramsViewModel Create() => new(_catalog, _uninstaller, _dialogs, _journal);

    private static InstalledProgram Program(string key, string name, string? publisher, string command, long? size, bool perUser = false) =>
        new(key, name, publisher, "1.0", null, size, command, perUser, false);

    [Fact]
    public async Task Lists_programs_and_filters_by_name_or_publisher()
    {
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.VisiblePrograms.Count);
        Assert.Contains("40 GB", vm.SummaryText);
        Assert.False(vm.VisiblePrograms.Single(p => p.Name == "Broken").CanUninstall);

        vm.Filter = "chat inc";
        Assert.Equal(["Chat App"], vm.VisiblePrograms.Select(p => p.Name));
    }

    [Fact]
    public async Task Uninstall_shows_command_runs_it_and_records_when_gone()
    {
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.UninstallCommand.ExecuteAsync(vm.VisiblePrograms.Single(p => p.Name == "Big Game"));

        Assert.Contains(@"C:\Games\uninstall.exe /S", _dialogs.LastConfirm);
        Assert.Equal((@"C:\Games\uninstall.exe", "/S"), _uninstaller.LastRun);
        Assert.Contains("제거했습니다", vm.StatusText);
        Assert.DoesNotContain(vm.VisiblePrograms, p => p.Name == "Big Game");
        Assert.Contains(_journal.Entries, e => e.Kind == ChangeKind.Uninstalled && e.Title.Contains("Big Game"));
    }

    [Fact]
    public async Task Program_still_listed_after_uninstaller_is_reported_and_not_recorded()
    {
        _uninstaller.OnRun = () => { };
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.UninstallCommand.ExecuteAsync(vm.VisiblePrograms.Single(p => p.Name == "Chat App"));

        Assert.Contains("현재 사용자", _dialogs.LastConfirm);
        Assert.Contains("아직 목록에", vm.StatusText);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public async Task Declined_confirmation_runs_nothing()
    {
        _dialogs.Answer = false;
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.UninstallCommand.ExecuteAsync(vm.VisiblePrograms[0]);

        Assert.Null(_uninstaller.LastRun);
    }

    private sealed class FakeCatalog : IProgramCatalog
    {
        public IReadOnlyList<InstalledProgram> Programs { get; set; } = [];
        public IReadOnlyList<InstalledProgram> List() => Programs;
    }

    private sealed class FakeUninstaller : IProgramUninstaller
    {
        public (string, string)? LastRun { get; private set; }
        public Action OnRun { get; set; } = () => { };

        public Task<int> RunAsync(string fileName, string arguments, CancellationToken ct)
        {
            LastRun = (fileName, arguments);
            OnRun();
            return Task.FromResult(0);
        }
    }

    private sealed class FakeDialogs : IUserDialogs
    {
        public bool Answer { get; set; } = true;
        public string LastConfirm { get; private set; } = "";

        public bool Confirm(string title, string message)
        {
            LastConfirm = message;
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
