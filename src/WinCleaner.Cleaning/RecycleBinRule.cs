using System.Security.Principal;
using System.Text;
using WinCleaner.Core;
using WinCleaner.Core.Cleaning;

namespace WinCleaner.Cleaning;

/// <summary>
/// 휴지통 비우기 (사양서 3.1 <c>recyclebin</c>, 위험도 중간).
/// 휴지통 전체를 한 번에 비우지 않고, 스캔해서 미리보기로 보여 준 항목만 지운다.
/// 휴지통 폴더에는 항목마다 내용($R…)과 원래 위치·크기·삭제 시각을 담은 정보 파일($I…)이 한 쌍으로 있다.
/// </summary>
public sealed class RecycleBinRule(Func<IEnumerable<string>> binFolders) : ICleanupRule
{
    private static readonly EnumerationOptions ListOptions = new() { AttributesToSkip = 0, IgnoreInaccessible = true };
    private static readonly EnumerationOptions DeleteOptions = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    public RecycleBinRule() : this(CurrentUserBinFolders)
    {
    }

    public string Id => "recyclebin";
    public string Name => "휴지통";
    public string Description => "휴지통에 버린 파일. 비우면 되살릴 수 없으니 미리보기로 원래 위치를 확인하세요";
    public string Category => "시스템";
    public RiskLevel Risk => RiskLevel.Medium;
    public bool RequiresElevation => false;

    public Task<ScanResult> ScanAsync(CancellationToken ct) => Task.Run(() => Scan(ct), ct);

    public Task<CleanResult> CleanAsync(ScanResult selected, CancellationToken ct) =>
        Task.Run(() => Clean(selected, ct), CancellationToken.None);

    /// <summary>각 고정 디스크의 $Recycle.Bin 안에서 현재 사용자 몫의 폴더.</summary>
    public static IEnumerable<string> CurrentUserBinFolders()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (sid is null)
            return [];

        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.Name, "$Recycle.Bin", sid))
            .ToList();
    }

    private ScanResult Scan(CancellationToken ct)
    {
        var items = new List<ScanItem>();
        foreach (var folder in ExistingBinFolders())
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles("$I*", ListOptions))
            {
                ct.ThrowIfCancellationRequested();
                var content = ContentPathFor(info.FullName);
                if (!TryReadInfo(info.FullName, out var original, out var size, out var deletedAt) || !Exists(content))
                    continue;

                items.Add(new ScanItem(content, size, deletedAt, DisplayPath: original));
            }
        }
        return new ScanResult(Id, items, []);
    }

    private CleanResult Clean(ScanResult selected, CancellationToken ct)
    {
        var folders = ExistingBinFolders().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = new List<ScanItem>();
        var skipped = new List<SkippedItem>();

        foreach (var item in selected.Items)
        {
            if (ct.IsCancellationRequested)
                break;

            var content = Path.GetFullPath(item.Path);
            var name = Path.GetFileName(content);
            if (!folders.Contains(Path.GetDirectoryName(content) ?? "") || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new SkippedItem(item.ShownPath, "휴지통 밖의 경로"));
                continue;
            }

            var infoPath = Path.Combine(Path.GetDirectoryName(content)!, "$I" + name[2..]);
            if (!TryReadInfo(infoPath, out _, out _, out var deletedAt) || deletedAt != item.LastWriteTimeUtc)
            {
                skipped.Add(new SkippedItem(item.ShownPath, "스캔 뒤에 바뀐 항목"));
                continue;
            }

            try
            {
                DeleteContent(content);
                File.Delete(infoPath);
                deleted.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new SkippedItem(item.ShownPath, "사용 중이거나 권한 없음"));
            }
        }
        return new CleanResult(Id, deleted, skipped);
    }

    private IEnumerable<string> ExistingBinFolders() =>
        binFolders()
            .Select(PathGuard.Normalize)
            .Where(f => Directory.Exists(f) && !new DirectoryInfo(f).Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static string ContentPathFor(string infoPath) =>
        Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + Path.GetFileName(infoPath)[2..]);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void DeleteContent(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists)
        {
            if (info.IsReadOnly)
                info.IsReadOnly = false;
            info.Delete();
        }
        else if (Directory.Exists(path))
        {
            DeleteFolder(new DirectoryInfo(path));
        }
    }

    /// <summary>폴더를 버린 항목을 지운다. 안에 있는 링크·정션은 따라 들어가지 않고 링크 자체만 지운다.</summary>
    private static void DeleteFolder(DirectoryInfo folder)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos("*", DeleteOptions))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (entry is DirectoryInfo)
                    Directory.Delete(entry.FullName);
                else
                    entry.Delete();
            }
            else if (entry is DirectoryInfo subfolder)
            {
                DeleteFolder(subfolder);
            }
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        folder.Delete();
    }

    /// <summary>
    /// $I 파일을 읽는다. 버전 1(Vista~8.1)은 경로가 260자 고정, 버전 2(Windows 10 이상)는 길이가 앞에 붙는다.
    /// </summary>
    internal static bool TryReadInfo(string path, out string originalPath, out long size, out DateTime deletedAtUtc)
    {
        originalPath = "";
        size = 0;
        deletedAtUtc = default;
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 24)
                return false;

            var version = BitConverter.ToInt64(data, 0);
            size = BitConverter.ToInt64(data, 8);
            deletedAtUtc = DateTime.FromFileTimeUtc(BitConverter.ToInt64(data, 16));

            switch (version)
            {
                case 1:
                    originalPath = Encoding.Unicode.GetString(data, 24, Math.Min(520, data.Length - 24)).TrimEnd('\0');
                    return true;
                case 2 when data.Length >= 28:
                    var chars = BitConverter.ToInt32(data, 24);
                    if (chars < 0 || 28 + (long)chars * 2 > data.Length)
                        return false;
                    originalPath = Encoding.Unicode.GetString(data, 28, chars * 2).TrimEnd('\0');
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
