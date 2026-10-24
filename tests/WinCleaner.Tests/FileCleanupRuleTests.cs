using System.Diagnostics;
using WinCleaner.Cleaning;
using WinCleaner.Core.Cleaning;

namespace WinCleaner.Tests;

public sealed class FileCleanupRuleTests : IDisposable
{
    private static readonly TimeSpan Old = TimeSpan.FromDays(3);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private FileCleanupRule Rule(string folder, string pattern = "*", bool recursive = true,
        double minAgeHours = 24, string[]? skipIfRunning = null)
    {
        var definition = new RuleDefinition
        {
            Id = "test",
            Name = "테스트",
            Description = "",
            Category = "테스트",
            MinAgeHours = minAgeHours,
            SkipIfRunning = skipIfRunning ?? [],
            Targets = [new RuleTarget { Path = folder, Pattern = pattern, Recursive = recursive }],
        };
        var guard = new PathGuard(
            protectedFolders: [Path.Combine(_box.Root, "protected")],
            userDataFolders: [Path.Combine(_box.Root, "documents")]);
        return new FileCleanupRule(definition, guard);
    }

    private static IEnumerable<string> Paths(ScanResult result) => result.Items.Select(i => i.Path);

    [Fact]
    public async Task Scan_finds_only_files_older_than_min_age()
    {
        var old = _box.File(@"temp\old.tmp", Old);
        var fresh = _box.File(@"temp\fresh.tmp", Fresh);

        var result = await Rule(_box.Folder("temp")).ScanAsync(default);

        Assert.Contains(old, Paths(result));
        Assert.DoesNotContain(fresh, Paths(result));
    }

    [Fact]
    public async Task Scan_respects_pattern_and_recursion_setting()
    {
        var top = _box.File(@"cache\thumbcache_1.db", Old);
        var otherName = _box.File(@"cache\iconcache_1.db", Old);
        var nested = _box.File(@"cache\sub\thumbcache_2.db", Old);

        var result = await Rule(_box.Folder("cache"), "thumbcache_*.db", recursive: false).ScanAsync(default);

        Assert.Equal([top], Paths(result));
        Assert.DoesNotContain(otherName, Paths(result));
        Assert.DoesNotContain(nested, Paths(result));
    }

    [Fact]
    public async Task Scan_includes_hidden_files()
    {
        var hidden = _box.File(@"temp\hidden.tmp", Old);
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = await Rule(_box.Folder("temp")).ScanAsync(default);

        Assert.Contains(hidden, Paths(result));
    }

    [Fact]
    public async Task Scan_expands_wildcard_folders_and_environment_variables()
    {
        var a = _box.File(@"browser\Default\Cache\a", Old);
        var b = _box.File(@"browser\Profile 1\Cache\b", Old);
        Environment.SetEnvironmentVariable("WINCLEANER_TEST_ROOT", _box.Root);

        var result = await Rule(@"%WINCLEANER_TEST_ROOT%\browser\*\Cache", minAgeHours: 0).ScanAsync(default);

        Assert.Equal([a, b], Paths(result).Order());
    }

    [Fact]
    public async Task Missing_folder_gives_empty_result_without_warnings()
    {
        var result = await Rule(Path.Combine(_box.Root, "not-here")).ScanAsync(default);

        Assert.Empty(result.Items);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Protected_folder_is_skipped_with_warning()
    {
        _box.File(@"protected\system.dll", Old);

        var result = await Rule(_box.Folder("protected")).ScanAsync(default);

        Assert.Empty(result.Items);
        Assert.Contains(result.Warnings, w => w.Contains("보호"));
    }

    [Fact]
    public async Task Exact_file_target_is_allowed_inside_protected_folder()
    {
        var dump = _box.File(@"protected\MEMORY.DMP", Old);
        _box.File(@"protected\other.dll", Old);
        var rule = Rule(_box.Folder("protected"), "MEMORY.DMP", recursive: false, minAgeHours: 0);

        var scan = await rule.ScanAsync(default);
        var clean = await rule.CleanAsync(scan, default);

        Assert.Equal([dump], Paths(scan));
        Assert.Equal(1, clean.DeletedCount);
        Assert.True(File.Exists(Path.Combine(_box.Root, @"protected\other.dll")));
    }

    [Fact]
    public async Task Scan_and_clean_never_follow_junctions()
    {
        var outsideFile = _box.File(@"outside\precious.docx", Old);
        _box.Junction(@"temp\link", _box.Folder("outside"));
        var rule = Rule(_box.Folder("temp"));

        var scan = await rule.ScanAsync(default);
        Assert.DoesNotContain(scan.Items, i => i.Path.Contains("precious"));

        // 누군가 정션 경로를 끼워 넣은 스캔 결과를 줘도 지우지 않는다.
        var forged = new ScanResult("test",
            [new ScanItem(Path.Combine(_box.Root, @"temp\link\precious.docx"), 100, File.GetLastWriteTimeUtc(outsideFile))], []);
        var clean = await rule.CleanAsync(forged, default);

        Assert.Equal(0, clean.DeletedCount);
        Assert.Contains(clean.Skipped, s => s.Reason.Contains("링크"));
        Assert.True(File.Exists(outsideFile));
    }

    [Fact]
    public async Task Clean_rejects_paths_outside_the_rule_folder()
    {
        var outsideFile = _box.File(@"outside\keep.txt", Old);
        var rule = Rule(_box.Folder("temp"));
        var time = File.GetLastWriteTimeUtc(outsideFile);
        var forged = new ScanResult("test",
        [
            new ScanItem(outsideFile, 100, time),
            new ScanItem(Path.Combine(_box.Root, @"temp\..\outside\keep.txt"), 100, time),
        ], []);

        var clean = await rule.CleanAsync(forged, default);

        Assert.Equal(0, clean.DeletedCount);
        Assert.Equal(2, clean.Skipped.Count);
        Assert.True(File.Exists(outsideFile));
    }

    [Fact]
    public async Task Clean_deletes_scanned_files_and_empty_old_folders_but_keeps_root()
    {
        var root = _box.Folder("temp");
        _box.File(@"temp\a.tmp", Old, size: 1000);
        _box.File(@"temp\setup\b.tmp", Old, size: 2000);
        _box.Folder(@"temp\setup", Old);
        var rule = Rule(root);

        var scan = await rule.ScanAsync(default);
        var clean = await rule.CleanAsync(scan, default);

        Assert.Equal(2, clean.DeletedCount);
        Assert.Equal(3000, clean.FreedBytes);
        Assert.Empty(clean.Skipped);
        Assert.False(Directory.Exists(Path.Combine(root, "setup")));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task Clean_keeps_recently_created_folders_even_when_empty()
    {
        var root = _box.Folder("temp");
        _box.File(@"temp\job\old.tmp", Old);
        var rule = Rule(root);

        var scan = await rule.ScanAsync(default);
        await rule.CleanAsync(scan, default);

        Assert.True(Directory.Exists(Path.Combine(root, "job")));
    }

    [Fact]
    public async Task Clean_skips_files_in_use()
    {
        var locked = _box.File(@"temp\locked.tmp", Old);
        var rule = Rule(_box.Folder("temp"));
        var scan = await rule.ScanAsync(default);

        CleanResult clean;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            clean = await rule.CleanAsync(scan, default);

        Assert.Equal(0, clean.DeletedCount);
        Assert.Contains(clean.Skipped, s => s.Path == locked && s.Reason.Contains("사용 중"));
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public async Task Clean_deletes_read_only_files()
    {
        var readOnly = _box.File(@"temp\readonly.tmp", Old);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var rule = Rule(_box.Folder("temp"));

        var clean = await rule.CleanAsync(await rule.ScanAsync(default), default);

        Assert.Equal(1, clean.DeletedCount);
        Assert.False(File.Exists(readOnly));
    }

    [Fact]
    public async Task Clean_skips_files_changed_after_scan()
    {
        var file = _box.File(@"temp\changed.tmp", Old);
        var rule = Rule(_box.Folder("temp"));
        var scan = await rule.ScanAsync(default);

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromDays(2));
        var clean = await rule.CleanAsync(scan, default);

        Assert.Equal(0, clean.DeletedCount);
        Assert.Contains(clean.Skipped, s => s.Reason.Contains("바뀐"));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Rule_is_skipped_while_listed_program_runs()
    {
        _box.File(@"temp\a.tmp", Old);
        var self = Process.GetCurrentProcess().ProcessName;
        var rule = Rule(_box.Folder("temp"), skipIfRunning: [self]);

        var scan = await rule.ScanAsync(default);

        Assert.Empty(scan.Items);
        Assert.Contains(scan.Warnings, w => w.Contains(self));
    }

    [Fact]
    public async Task Cancelled_clean_returns_partial_result()
    {
        _box.File(@"temp\a.tmp", Old);
        var rule = Rule(_box.Folder("temp"));
        var scan = await rule.ScanAsync(default);

        var clean = await rule.CleanAsync(scan, new CancellationToken(canceled: true));

        Assert.Equal(0, clean.DeletedCount);
        Assert.Empty(clean.Skipped);
    }

    [Fact]
    public async Task Cleanup_log_lists_deleted_and_skipped_files()
    {
        var file = _box.File(@"temp\a.tmp", Old, size: 2048);
        var rule = Rule(_box.Folder("temp"));
        var clean = await rule.CleanAsync(await rule.ScanAsync(default), default);

        var logPath = CleanupLog.Write(_box.Folder("logs"), [(rule, clean)], DateTime.Now, isElevated: false);
        var log = await File.ReadAllTextAsync(logPath);

        Assert.Contains("[test] 테스트", log);
        Assert.Contains(file, log);
        Assert.Contains("2 KB", log);
    }
}
