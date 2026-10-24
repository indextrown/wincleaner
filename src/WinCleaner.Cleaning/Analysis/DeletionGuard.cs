namespace WinCleaner.Cleaning.Analysis;

/// <summary>
/// 용량 분석 화면에서 지워도 되는 항목인지 판단한다 (사양서 3.1.1).
/// 청소 규칙용 <see cref="PathGuard"/>와 달리 문서·다운로드 안의 파일은 사용자가 고른 것이므로 지울 수 있다.
/// 대신 Windows 폴더 전체, 드라이브 최상위, 기본 폴더 자체는 막는다.
/// </summary>
public sealed class DeletionGuard
{
    // 어느 위치에 있든 Windows가 쓰는 이름
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "swapfile.sys", "hiberfil.sys", "bootmgr", "BOOTNXT",
        "System Volume Information", "$Recycle.Bin", "Recovery", "$WinREAgent",
    };

    // 이 폴더와 그 안 전체를 막는다.
    private readonly List<string> _blockedTrees;

    // 이 폴더 자체와 그 상위 폴더를 막는다. 안에 있는 항목은 지울 수 있다.
    private readonly List<string> _blockedFolders;

    // 지울 수는 있지만 한 번 더 알린다.
    private readonly List<string> _cautionTrees;

    public DeletionGuard(IEnumerable<string> blockedTrees, IEnumerable<string> blockedFolders, IEnumerable<string> cautionTrees)
    {
        _blockedTrees = Normalize(blockedTrees);
        _blockedFolders = Normalize(blockedFolders);
        _cautionTrees = Normalize(cautionTrees);
    }

    public static DeletionGuard ForCurrentUser()
    {
        static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        var profile = Folder(Environment.SpecialFolder.UserProfile);
        var programFiles = new[] { Folder(Environment.SpecialFolder.ProgramFiles), Folder(Environment.SpecialFolder.ProgramFilesX86) };

        return new DeletionGuard(
            blockedTrees: [Folder(Environment.SpecialFolder.Windows)],
            blockedFolders:
            [
                .. programFiles,
                Folder(Environment.SpecialFolder.CommonApplicationData),
                Path.GetDirectoryName(profile) ?? "",
                Folder(Environment.SpecialFolder.CommonDocuments) is { Length: > 0 } common ? Path.GetDirectoryName(common) ?? "" : "",
                profile,
                Folder(Environment.SpecialFolder.ApplicationData),
                Folder(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(profile, "AppData"),
                Folder(Environment.SpecialFolder.Desktop),
                Folder(Environment.SpecialFolder.MyDocuments),
                Folder(Environment.SpecialFolder.MyPictures),
                Folder(Environment.SpecialFolder.MyMusic),
                Folder(Environment.SpecialFolder.MyVideos),
                Path.Combine(profile, "Downloads"),
                Environment.GetEnvironmentVariable("OneDrive") ?? "",
            ],
            cautionTrees: programFiles);
    }

    /// <summary>지우면 안 되는 이유. 지워도 되면 null.</summary>
    public string? BlockReason(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            return "전체 경로가 아닙니다.";

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            return "드라이브 최상위는 지울 수 없습니다.";
        if (SystemNames.Contains(Path.GetFileName(full)))
            return "Windows가 쓰는 항목이라 지울 수 없습니다.";
        if (_blockedTrees.Any(t => Same(full, t) || PathGuard.IsUnder(full, t)))
            return "Windows 폴더 안은 지울 수 없습니다. 디스크 청소 메뉴를 쓰세요.";
        if (_blockedFolders.Any(f => Same(full, f) || PathGuard.IsUnder(f, full)))
            return "기본 폴더라 통째로 지울 수 없습니다. 안에 있는 항목을 지우세요.";
        return null;
    }

    /// <summary>지울 수는 있지만 알려야 할 내용. 없으면 null.</summary>
    public string? Caution(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return _cautionTrees.Any(t => PathGuard.IsUnder(full, t))
            ? "설치된 프로그램의 파일입니다. 프로그램은 설정 > 앱에서 제거하는 것이 안전합니다."
            : null;
    }

    private static List<string> Normalize(IEnumerable<string> paths) =>
        paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathGuard.Normalize).ToList();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
