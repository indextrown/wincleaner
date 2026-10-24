using System.Diagnostics.CodeAnalysis;

namespace WinCleaner.Cleaning;

/// <summary>
/// 청소 규칙이 시스템 폴더나 사용자 문서를 통째로 정리하지 못하게 막는다 (사양서 3.4).
/// 규칙을 잘못 썼거나 %TEMP% 같은 환경 변수가 엉뚱한 곳을 가리켜도 여기서 걸러진다.
/// </summary>
public sealed class PathGuard
{
    // 이 폴더 자체나 이 폴더를 품은 상위 폴더는 정리 대상이 될 수 없다. 하위 폴더는 괜찮다.
    private readonly List<string> _protectedFolders;

    // 이 폴더와 그 안은 어디든 정리 대상이 될 수 없다.
    private readonly List<string> _userDataFolders;

    public PathGuard(IEnumerable<string> protectedFolders, IEnumerable<string> userDataFolders)
    {
        _protectedFolders = protectedFolders.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).ToList();
        _userDataFolders = userDataFolders.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).ToList();
    }

    public static PathGuard ForCurrentUser()
    {
        static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        var windows = Folder(Environment.SpecialFolder.Windows);
        var profile = Folder(Environment.SpecialFolder.UserProfile);

        string?[] protectedFolders =
        [
            windows,
            Folder(Environment.SpecialFolder.System),
            Folder(Environment.SpecialFolder.SystemX86),
            Path.Combine(windows, "WinSxS"),
            Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86),
            Folder(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(profile),
            profile,
            Folder(Environment.SpecialFolder.ApplicationData),
            Folder(Environment.SpecialFolder.LocalApplicationData),
        ];

        string?[] userDataFolders =
        [
            Folder(Environment.SpecialFolder.Desktop),
            Folder(Environment.SpecialFolder.MyDocuments),
            Folder(Environment.SpecialFolder.MyPictures),
            Folder(Environment.SpecialFolder.MyMusic),
            Folder(Environment.SpecialFolder.MyVideos),
            Path.Combine(profile, "Downloads"),
            Environment.GetEnvironmentVariable("OneDrive"),
        ];

        return new PathGuard(protectedFolders.OfType<string>(), userDataFolders.OfType<string>());
    }

    /// <summary>폴더를 정리 대상으로 삼아도 되는지 확인한다. 안 되면 이유를 돌려준다.</summary>
    public bool IsAllowedRoot(string folder, [NotNullWhen(false)] out string? reason)
    {
        if (folder.Contains('%'))
        {
            reason = "환경 변수를 해석하지 못한 경로입니다";
            return false;
        }

        if (!Path.IsPathFullyQualified(folder))
        {
            reason = "전체 경로가 아닙니다";
            return false;
        }

        var path = Normalize(folder);

        if (string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
        {
            reason = "드라이브 최상위 폴더입니다";
            return false;
        }

        if (_protectedFolders.Any(p => SamePath(path, p) || IsUnder(p, path)))
        {
            reason = "보호된 시스템 폴더입니다";
            return false;
        }

        if (_userDataFolders.Any(u => SamePath(path, u) || IsUnder(path, u) || IsUnder(u, path)))
        {
            reason = "사용자 문서 폴더입니다";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary><paramref name="path"/>가 <paramref name="ancestor"/> 안에 있는지. 두 경로 모두 정규화돼 있어야 한다.</summary>
    public static bool IsUnder(string path, string ancestor) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(ancestor) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
