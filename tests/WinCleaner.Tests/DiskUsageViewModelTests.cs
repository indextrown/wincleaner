using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;
using WinCleaner.Safety;

namespace WinCleaner.Tests;

public sealed class DiskUsageViewModelTests : IDisposable
{
    private static readonly TimeSpan Age = TimeSpan.FromDays(1);
    private readonly Sandbox _box = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _box.Dispose();

    private DiskUsageViewModel Create(bool isElevated = true) =>
        new([new DriveChoice(_box.Folder("scan"), "테스트", IsSystemDrive: true)], _dialogs, isElevated);

    [Fact]
    public async Task Analyze_lists_folders_by_size_with_own_files_row()
    {
        _box.File(@"scan\big\a.bin", Age, size: 900);
        _box.File(@"scan\small\b.bin", Age, size: 100);
        _box.File(@"scan\loose.bin", Age, size: 300);
        var vm = Create();

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(vm.HasReport);
        Assert.False(vm.IsBusy);
        Assert.Equal(["big", "이 폴더에 바로 있는 파일", "small"], vm.FolderRows.Select(r => r.Name));
        Assert.Equal(69.2, vm.FolderRows[0].Percent, precision: 1);
        Assert.Contains("1.3 KB", vm.AnalyzedText);
        Assert.Equal(3, vm.LargeFiles.Count);
        Assert.Equal("a.bin", vm.LargeFiles[0].Name);
    }

    [Fact]
    public async Task Open_breadcrumb_and_up_navigate_the_tree()
    {
        _box.File(@"scan\outer\inner\a.bin", Age, size: 500);
        var vm = Create();
        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.False(vm.CanGoUp);
        vm.OpenCommand.Execute(vm.FolderRows.Single(r => r.Name == "outer"));
        Assert.Equal("outer", vm.CurrentFolder?.Name);
        Assert.Equal(["scan", "outer"], vm.Breadcrumbs.Select(b => b.Name.Split('\\').Last()));

        // 하위 폴더 없이 파일만 있는 폴더는 들어가지 않는다 (탐색기 버튼으로 연다).
        var inner = vm.FolderRows.Single(r => r.Name == "inner");
        Assert.False(inner.CanOpen);
        vm.OpenCommand.Execute(inner);
        Assert.Equal("outer", vm.CurrentFolder?.Name);

        vm.GoUpCommand.Execute(null);
        Assert.False(vm.CanGoUp);

        vm.OpenCommand.Execute(vm.FolderRows.Single());
        vm.GoToCommand.Execute(vm.Breadcrumbs[0].Node);
        Assert.Null(vm.CurrentFolder?.Parent);
    }

    [Fact]
    public async Task Folders_beyond_the_row_limit_are_grouped_into_one_row()
    {
        var extra = 5;
        for (var i = 0; i < DiskUsageViewModel.MaxFolderRows + extra; i++)
            _box.File($@"scan\d{i:000}\f.bin", Age, size: 10);
        var vm = Create();

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(DiskUsageViewModel.MaxFolderRows + 1, vm.FolderRows.Count);
        var others = vm.FolderRows.Single(r => r.Node is null);
        Assert.Contains($"{extra}개", others.Name);
        Assert.Equal(extra * 10, others.SizeBytes);
    }

    [Fact]
    public async Task Show_in_explorer_passes_path_to_shell()
    {
        var file = _box.File(@"scan\a.bin", Age, size: 10);
        var vm = Create();
        await vm.AnalyzeCommand.ExecuteAsync(null);

        vm.ShowInExplorerCommand.Execute(vm.LargeFiles.Single().Path);

        Assert.Equal(file, _dialogs.LastExplorerPath);
    }

    [Fact]
    public async Task Selecting_another_drive_clears_the_report()
    {
        _box.File(@"scan\a.bin", Age, size: 10);
        var other = new DriveChoice(_box.Folder("other"), "다른 드라이브", IsSystemDrive: false);
        var vm = new DiskUsageViewModel([new DriveChoice(_box.Folder("scan"), "테스트", true), other], _dialogs, true);
        await vm.AnalyzeCommand.ExecuteAsync(null);

        vm.SelectedDrive = other;

        Assert.False(vm.HasReport);
        Assert.Empty(vm.FolderRows);
    }

    [Fact]
    public async Task Inaccessible_text_suggests_elevation_only_when_not_elevated()
    {
        var vm = Create(isElevated: false);
        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal("없음", vm.InaccessibleText);
    }

    [Fact]
    public async Task Deleting_a_folder_recycles_it_and_shrinks_the_tree_without_rescanning()
    {
        _box.File(@"scan\old-setup\a.bin", Age, size: 600);
        _box.File(@"scan\old-setup\sub\b.bin", Age, size: 300);
        _box.File(@"scan\keep\c.bin", Age, size: 100);
        var journal = new ChangeJournal(Path.Combine(_box.Root, "journal.json"));
        var vm = new DiskUsageViewModel([new DriveChoice(_box.Folder("scan"), "테스트", true)], _dialogs, true, journal);
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var row = vm.FolderRows.Single(r => r.Name == "old-setup");
        Assert.True(row.CanDelete);
        vm.DeleteFolderCommand.Execute(row);

        Assert.Equal(row.Path, _dialogs.LastRecycledPath);
        Assert.Contains("old-setup", _dialogs.LastConfirm);
        Assert.Equal(["keep"], vm.FolderRows.Select(r => r.Name));
        Assert.Equal(100, vm.CurrentFolder!.SizeBytes);
        Assert.DoesNotContain(vm.LargeFiles, f => f.Path.Contains("old-setup"));
        Assert.Contains("휴지통", vm.StatusText);
        Assert.Contains(journal.Entries, e => e.Kind == ChangeKind.Recycled);
    }

    [Fact]
    public async Task Deleting_a_large_file_updates_its_folder_size()
    {
        _box.File(@"scan\videos\movie.mp4", Age, size: 5000);
        _box.File(@"scan\videos\clip.mp4", Age, size: 100);
        var vm = Create();
        await vm.AnalyzeCommand.ExecuteAsync(null);

        vm.DeleteFileCommand.Execute(vm.LargeFiles.Single(f => f.Name == "movie.mp4"));

        Assert.Equal(100, vm.CurrentFolder!.SizeBytes);
        Assert.Equal(["clip.mp4"], vm.LargeFiles.Select(f => f.Name));
    }

    [Fact]
    public async Task Nothing_is_recycled_when_user_cancels_or_windows_fails()
    {
        _box.File(@"scan\stuff\a.bin", Age, size: 100);
        var vm = Create();
        await vm.AnalyzeCommand.ExecuteAsync(null);
        var row = vm.FolderRows.Single();

        _dialogs.Answer = false;
        vm.DeleteFolderCommand.Execute(row);
        Assert.Null(_dialogs.LastRecycledPath);

        _dialogs.Answer = true;
        _dialogs.RecycleError = "사용 중인 파일이 있습니다.";
        vm.DeleteFolderCommand.Execute(row);
        Assert.Equal("사용 중인 파일이 있습니다.", vm.StatusText);
        Assert.Equal(100, vm.CurrentFolder!.SizeBytes);
    }

    private sealed class FakeDialogs : IUserDialogs
    {
        public string? LastExplorerPath { get; private set; }
        public string? LastRecycledPath { get; private set; }
        public string LastConfirm { get; private set; } = "";
        public bool Answer { get; set; } = true;
        public string? RecycleError { get; set; }

        public bool Confirm(string title, string message)
        {
            LastConfirm = message;
            return Answer;
        }

        public void OpenFile(string path)
        {
        }

        public void ShowInExplorer(string path) => LastExplorerPath = path;

        public bool MoveToRecycleBin(string path, out string? error)
        {
            error = RecycleError;
            if (error is not null)
                return false;
            LastRecycledPath = path;
            return true;
        }
    }
}
