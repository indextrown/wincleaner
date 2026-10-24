namespace WinCleaner.Cleaning.Analysis;

/// <summary>
/// 용량을 많이 차지하는 대표적인 폴더·파일이 무엇이고 지워도 되는지 짧게 설명한다.
/// 분석 화면에서 "이게 뭐지?" 하고 함부로 지우는 일을 막기 위한 안내다.
/// </summary>
public static class KnownLocations
{
    // 어느 드라이브에 있어도 이름만으로 알 수 있는 것
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pagefile.sys"] = "가상 메모리 파일입니다. 직접 지우면 안 됩니다.",
        ["swapfile.sys"] = "Windows 앱용 가상 메모리 파일입니다. 직접 지우면 안 됩니다.",
        ["hiberfil.sys"] = "최대 절전 파일입니다. 최대 절전과 빠른 시작을 끄면 사라집니다 (최적화 메뉴에서 지원 예정).",
        ["$Recycle.Bin"] = "휴지통입니다. 휴지통을 비우면 정리됩니다.",
        ["System Volume Information"] = "시스템 복원 지점이 저장되는 곳입니다. 직접 열거나 지울 수 없습니다.",
        ["node_modules"] = "Node.js 패키지 폴더입니다. 프로젝트에서 다시 설치할 수 있습니다.",
    };

    private static readonly Dictionary<string, string> ByPath = BuildPathHints();

    /// <summary>이 경로 자체에 대한 설명. 모르는 경로면 null.</summary>
    public static string? Describe(string fullPath)
    {
        var path = Path.TrimEndingDirectorySeparator(fullPath);
        if (ByPath.TryGetValue(path, out var hint))
            return hint;
        return ByName.TryGetValue(Path.GetFileName(path), out hint) ? hint : null;
    }

    /// <summary>이 경로나 가장 가까운 상위 폴더에 대한 설명. 큰 파일이 어디에 속하는지 알려 줄 때 쓴다.</summary>
    public static string? DescribeNearest(string fullPath)
    {
        for (var current = fullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Describe(current) is { } hint)
                return hint;
        }
        return null;
    }

    private static Dictionary<string, string> BuildPathHints()
    {
        static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var windows = Folder(Environment.SpecialFolder.Windows);
        var local = Folder(Environment.SpecialFolder.LocalApplicationData);
        var programData = Folder(Environment.SpecialFolder.CommonApplicationData);
        var profile = Folder(Environment.SpecialFolder.UserProfile);

        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string hint) => hints[Path.TrimEndingDirectorySeparator(path)] = hint;

        Add(Path.Combine(drive, "Windows.old"), "이전 Windows 설치 파일입니다. 이전 버전으로 되돌릴 일이 없으면 정리할 수 있습니다 (지원 예정).");
        Add(Path.Combine(windows, "WinSxS"), "Windows 구성 요소 저장소입니다. 하드 링크 때문에 실제보다 크게 보이며, 직접 지우면 안 됩니다.");
        Add(Path.Combine(windows, "Installer"), "설치된 프로그램의 설치 정보입니다. 지우면 프로그램 제거·업데이트가 망가집니다.");
        Add(Path.Combine(windows, "SoftwareDistribution"), "Windows Update 파일입니다. 다운로드 파일은 디스크 청소 메뉴에서 정리할 수 있습니다.");
        Add(Path.Combine(windows, "Temp"), "Windows 임시 파일입니다. 디스크 청소 메뉴에서 정리할 수 있습니다.");
        Add(Path.Combine(local, "Temp"), "사용자 임시 파일입니다. 디스크 청소 메뉴에서 정리할 수 있습니다.");
        Add(Path.Combine(programData, "Package Cache"), "프로그램 설치 캐시입니다. 지우면 프로그램을 고치거나 제거하지 못할 수 있습니다.");
        Add(Path.Combine(profile, "Downloads"), "다운로드 폴더입니다. 다 쓴 설치 파일이 쌓이기 쉽습니다.");
        return hints;
    }
}
