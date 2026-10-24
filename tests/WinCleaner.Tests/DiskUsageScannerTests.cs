using WinCleaner.Cleaning.Analysis;

namespace WinCleaner.Tests;

public sealed class DiskUsageScannerTests : IDisposable
{
    private static readonly TimeSpan Age = TimeSpan.FromDays(1);
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private static FolderNode Child(FolderNode folder, string name) => folder.Children.Single(c => c.Name == name);

    [Fact]
    public async Task Sizes_roll_up_and_children_are_sorted_by_size()
    {
        var root = _box.Folder("scan");
        _box.File(@"scan\small\a.bin", Age, size: 200);
        _box.File(@"scan\big\b.bin", Age, size: 500);
        _box.File(@"scan\big\deep\c.bin", Age, size: 1000);
        _box.File(@"scan\top.bin", Age, size: 50);

        var report = await new DiskUsageScanner().ScanAsync(root, null, default);

        Assert.Equal(1750, report.Root.SizeBytes);
        Assert.Equal(4, report.FileCount);
        Assert.Equal(50, report.Root.OwnFilesBytes);
        Assert.Equal(["big", "small"], report.Root.Children.Select(c => c.Name));

        var big = Child(report.Root, "big");
        Assert.Equal(1500, big.SizeBytes);
        Assert.Equal(500, big.OwnFilesBytes);
        Assert.Equal(Path.Combine(root, "big"), big.FullPath);
    }

    [Fact]
    public async Task Junctions_are_neither_followed_nor_counted()
    {
        var root = _box.Folder("scan");
        _box.File(@"scan\a.bin", Age, size: 100);
        _box.File(@"outside\huge.bin", Age, size: 10_000);
        _box.Junction(@"scan\link", _box.Folder("outside"));

        var report = await new DiskUsageScanner().ScanAsync(root, null, default);

        Assert.Equal(100, report.Root.SizeBytes);
        Assert.Equal(1, report.SkippedLinks);
        Assert.Empty(report.Root.Children);
    }

    [Fact]
    public async Task Hidden_files_are_counted()
    {
        var root = _box.Folder("scan");
        var hidden = _box.File(@"scan\hidden.bin", Age, size: 300);
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var report = await new DiskUsageScanner().ScanAsync(root, null, default);

        Assert.Equal(300, report.Root.SizeBytes);
    }

    [Fact]
    public async Task Largest_files_keep_only_top_n_in_descending_order()
    {
        var root = _box.Folder("scan");
        int[] sizes = [10, 50, 30, 70, 20];
        for (var i = 0; i < sizes.Length; i++)
            _box.File($@"scan\d{i % 2}\f{i}.bin", Age, size: sizes[i]);

        var report = await new DiskUsageScanner { LargestFileCount = 3 }.ScanAsync(root, null, default);

        Assert.Equal([70L, 50L, 30L], report.LargestFiles.Select(f => f.SizeBytes));
        Assert.EndsWith(@"d1\f3.bin", report.LargestFiles[0].Path);
    }

    [Fact]
    public async Task Single_thread_and_parallel_scans_agree()
    {
        var root = _box.Folder("scan");
        for (var i = 0; i < 40; i++)
            _box.File($@"scan\{i % 7}\{i % 3}\f{i}.bin", Age, size: 100 + i);

        var single = await new DiskUsageScanner { Parallelism = 1 }.ScanAsync(root, null, default);
        var parallel = await new DiskUsageScanner { Parallelism = 8 }.ScanAsync(root, null, default);

        Assert.Equal(single.Root.SizeBytes, parallel.Root.SizeBytes);
        Assert.Equal(40, parallel.FileCount);
        Assert.Equal(
            single.Root.Children.Select(c => (c.Name, c.SizeBytes)),
            parallel.Root.Children.Select(c => (c.Name, c.SizeBytes)));
    }

    [Fact]
    public async Task Progress_reports_final_totals()
    {
        var root = _box.Folder("scan");
        _box.File(@"scan\a.bin", Age, size: 400);
        var progress = new RecordingProgress();

        await new DiskUsageScanner().ScanAsync(root, progress, default);

        Assert.Equal(1, progress.Last?.Files);
        Assert.Equal(400, progress.Last?.Bytes);
    }

    [Fact]
    public async Task Cancelled_scan_throws()
    {
        var root = _box.Folder("scan");
        _box.File(@"scan\a.bin", Age);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DiskUsageScanner().ScanAsync(root, null, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Known_locations_explain_system_files_and_nearest_parent()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");

        Assert.Contains("가상 메모리", KnownLocations.Describe(Path.Combine(drive, "pagefile.sys")));
        Assert.Contains("임시", KnownLocations.Describe(temp + Path.DirectorySeparatorChar));
        Assert.Contains("임시", KnownLocations.DescribeNearest(Path.Combine(temp, "setup", "big.cab")));
        Assert.Null(KnownLocations.Describe(Path.Combine(drive, "Games")));
        Assert.Null(KnownLocations.DescribeNearest(Path.Combine(drive, "Games", "a.bin")));
    }

    /// <summary>Progress&lt;T&gt;는 비동기로 전달돼 테스트에서 순서를 보장하지 않으므로 바로 기록한다.</summary>
    private sealed class RecordingProgress : IProgress<DiskUsageProgress>
    {
        public DiskUsageProgress? Last { get; private set; }

        public void Report(DiskUsageProgress value) => Last = value;
    }
}
