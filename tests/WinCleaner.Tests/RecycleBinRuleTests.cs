using System.Text;
using WinCleaner.Cleaning;
using WinCleaner.Core.Cleaning;

namespace WinCleaner.Tests;

public sealed class RecycleBinRuleTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly string _bin;
    private readonly RecycleBinRule _rule;

    public RecycleBinRuleTests()
    {
        _bin = _box.Folder("bin");
        _rule = new RecycleBinRule(() => [_bin]);
    }

    public void Dispose() => _box.Dispose();

    /// <summary>Windows 10 형식(버전 2)의 $I 파일과 내용 파일을 만든다.</summary>
    private (string Content, string Info) AddFile(string id, string originalPath, int size, DateTime deletedAt)
    {
        var content = Path.Combine(_bin, "$R" + id);
        File.WriteAllBytes(content, new byte[size]);
        var info = Path.Combine(_bin, "$I" + id);
        WriteInfo(info, originalPath, size, deletedAt);
        return (content, info);
    }

    private static void WriteInfo(string path, string originalPath, long size, DateTime deletedAt)
    {
        var name = Encoding.Unicode.GetBytes(originalPath + "\0");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(2L);
        writer.Write(size);
        writer.Write(deletedAt.ToFileTimeUtc());
        writer.Write(name.Length / 2);
        writer.Write(name);
    }

    [Fact]
    public async Task Scan_shows_original_location_and_size()
    {
        var deletedAt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var (content, _) = AddFile("ABC123.docx", @"C:\Users\me\Documents\report.docx", 1234, deletedAt);

        var scan = await _rule.ScanAsync(default);

        var item = Assert.Single(scan.Items);
        Assert.Equal(content, item.Path);
        Assert.Equal(@"C:\Users\me\Documents\report.docx", item.ShownPath);
        Assert.Equal(1234, item.SizeBytes);
        Assert.Equal(deletedAt, item.LastWriteTimeUtc);
    }

    [Fact]
    public async Task Info_file_without_content_is_ignored()
    {
        WriteInfo(Path.Combine(_bin, "$IORPHAN.txt"), @"C:\gone.txt", 10, DateTime.UtcNow);

        var scan = await _rule.ScanAsync(default);

        Assert.Empty(scan.Items);
    }

    [Fact]
    public async Task Clean_removes_both_content_and_info_files()
    {
        var (content, info) = AddFile("A1.txt", @"C:\a.txt", 100, DateTime.UtcNow.AddDays(-1));
        var (folder, folderInfo) = (Path.Combine(_bin, "$RDIR"), Path.Combine(_bin, "$IDIR"));
        _box.File(@"bin\$RDIR\sub\b.txt", TimeSpan.FromDays(1));
        File.SetAttributes(Path.Combine(folder, @"sub\b.txt"), FileAttributes.ReadOnly);
        WriteInfo(folderInfo, @"C:\old-folder", 100, DateTime.UtcNow.AddDays(-2));

        var clean = await _rule.CleanAsync(await _rule.ScanAsync(default), default);

        Assert.Equal(2, clean.DeletedCount);
        Assert.Empty(clean.Skipped);
        Assert.False(File.Exists(content));
        Assert.False(File.Exists(info));
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(folderInfo));
    }

    [Fact]
    public async Task Clean_does_not_follow_junctions_inside_a_deleted_folder()
    {
        var precious = _box.File(@"outside\precious.docx", TimeSpan.FromDays(1));
        _box.Folder(@"bin\$RDIR");
        _box.Junction(@"bin\$RDIR\link", _box.Folder("outside"));
        WriteInfo(Path.Combine(_bin, "$IDIR"), @"C:\old-folder", 100, DateTime.UtcNow.AddDays(-1));

        var clean = await _rule.CleanAsync(await _rule.ScanAsync(default), default);

        Assert.Equal(1, clean.DeletedCount);
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public async Task Clean_skips_paths_outside_the_bin_and_items_changed_after_scan()
    {
        var outside = _box.File(@"outside\$Rkeep.txt", TimeSpan.FromDays(1));
        var (content, info) = AddFile("B1.txt", @"C:\b.txt", 100, DateTime.UtcNow.AddDays(-3));
        var scan = await _rule.ScanAsync(default);

        // 스캔 뒤에 같은 이름으로 다른 항목이 버려진 경우
        WriteInfo(info, @"C:\b.txt", 100, DateTime.UtcNow);
        var forged = scan with { Items = [.. scan.Items, new ScanItem(outside, 10, DateTime.UtcNow)] };
        var clean = await _rule.CleanAsync(forged, default);

        Assert.Equal(0, clean.DeletedCount);
        Assert.Equal(2, clean.Skipped.Count);
        Assert.True(File.Exists(content));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Reads_version_1_info_files()
    {
        var path = Path.Combine(_bin, "$IV1");
        var name = new byte[520];
        Encoding.Unicode.GetBytes(@"C:\legacy.txt").CopyTo(name, 0);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(1L);
            writer.Write(42L);
            writer.Write(DateTime.UtcNow.ToFileTimeUtc());
            writer.Write(name);
        }

        Assert.True(RecycleBinRule.TryReadInfo(path, out var original, out var size, out _));
        Assert.Equal(@"C:\legacy.txt", original);
        Assert.Equal(42, size);
    }

    [Fact]
    public void Recycle_bin_is_medium_risk_and_listed_first_in_built_in_rules()
    {
        var rules = RuleCatalog.CreateBuiltInRules();

        Assert.Equal("recyclebin", rules[0].Id);
        Assert.Equal(WinCleaner.Core.RiskLevel.Medium, rules[0].Risk);
    }
}
