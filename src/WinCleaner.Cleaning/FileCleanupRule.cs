using System.Diagnostics;
using System.IO.Enumeration;
using WinCleaner.Core;
using WinCleaner.Core.Cleaning;

namespace WinCleaner.Cleaning;

/// <summary>
/// 정해진 폴더에서 조건에 맞는 파일을 찾아 지우는 규칙.
/// 안전 원칙(사양서 3.4): 링크·정션은 따라가지 않고, 정리 대상 폴더 밖이나 스캔 뒤 바뀐 파일은 지우지 않는다.
/// </summary>
public sealed class FileCleanupRule : ICleanupRule
{
    // 숨김·시스템 파일도 포함하고, 하위 폴더는 직접 따라가며 링크 여부를 확인한다.
    private static readonly EnumerationOptions ListOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    private readonly RuleDefinition _definition;
    private readonly PathGuard _guard;

    public FileCleanupRule(RuleDefinition definition, PathGuard guard)
    {
        _definition = definition;
        _guard = guard;
    }

    public string Id => _definition.Id;
    public string Name => _definition.Name;
    public string Description => _definition.Description;
    public string Category => _definition.Category;
    public RiskLevel Risk => _definition.Risk;
    public bool RequiresElevation => _definition.RequiresElevation;

    private DateTime Cutoff => DateTime.UtcNow - TimeSpan.FromHours(_definition.MinAgeHours);

    public Task<ScanResult> ScanAsync(CancellationToken ct) => Task.Run(() => Scan(ct), ct);

    public Task<CleanResult> CleanAsync(ScanResult selected, CancellationToken ct) =>
        Task.Run(() => Clean(selected, ct), CancellationToken.None);

    private ScanResult Scan(CancellationToken ct)
    {
        if (FindRunningProcess() is { } running)
            return new ScanResult(Id, [], [$"{running}.exe가 실행 중이라 건너뜁니다. 프로그램을 완전히 닫은 뒤 다시 스캔하세요."]);

        var warnings = new List<string>();
        var items = new List<ScanItem>();
        var cutoff = Cutoff;
        var inaccessible = 0;

        foreach (var target in ResolveTargets(warnings))
        {
            if (target.IsExactFile)
            {
                var file = new FileInfo(target.ExactFilePath!);
                if (file.Exists && !IsLink(file) && file.LastWriteTimeUtc < cutoff)
                    items.Add(new ScanItem(file.FullName, file.Length, file.LastWriteTimeUtc));
                continue;
            }

            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(target.Root));

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var directory = pending.Pop();

                List<FileSystemInfo> entries;
                try
                {
                    entries = directory.EnumerateFileSystemInfos("*", ListOptions).ToList();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    inaccessible++;
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (IsLink(entry))
                        continue;

                    if (entry is DirectoryInfo subdirectory)
                    {
                        if (target.Recursive)
                            pending.Push(subdirectory);
                    }
                    else if (entry is FileInfo file
                             && FileSystemName.MatchesSimpleExpression(target.Pattern, file.Name)
                             && file.LastWriteTimeUtc < cutoff)
                    {
                        items.Add(new ScanItem(file.FullName, file.Length, file.LastWriteTimeUtc));
                    }
                }
            }
        }

        if (inaccessible > 0)
            warnings.Add($"열 수 없는 폴더 {inaccessible:N0}개는 건너뛰었습니다.");

        return new ScanResult(Id, items, warnings);
    }

    private CleanResult Clean(ScanResult selected, CancellationToken ct)
    {
        if (selected.RuleId != Id)
            throw new ArgumentException($"다른 규칙({selected.RuleId})의 스캔 결과입니다.", nameof(selected));

        var deleted = new List<ScanItem>();
        var skipped = new List<SkippedItem>();

        if (FindRunningProcess() is { } running)
        {
            skipped.AddRange(selected.Items.Select(i => new SkippedItem(i.Path, $"{running}.exe 실행 중")));
            return new CleanResult(Id, deleted, skipped);
        }

        var targets = ResolveTargets([]);
        var verifiedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touchedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutoff = Cutoff;

        foreach (var item in selected.Items)
        {
            if (ct.IsCancellationRequested)
                break;

            var path = Path.GetFullPath(item.Path);
            var target = targets.FirstOrDefault(t => t.Contains(path));
            if (target is null)
            {
                skipped.Add(new SkippedItem(item.Path, "정리 대상 폴더 밖의 경로"));
                continue;
            }

            if (!target.IsExactFile && !HasNoLinksBetween(target.Root, Path.GetDirectoryName(path)!, verifiedFolders))
            {
                skipped.Add(new SkippedItem(item.Path, "링크·정션을 거치는 경로"));
                continue;
            }

            var file = new FileInfo(path);
            if (!file.Exists)
                continue;

            if (IsLink(file))
            {
                skipped.Add(new SkippedItem(item.Path, "링크 파일"));
                continue;
            }

            if (file.LastWriteTimeUtc != item.LastWriteTimeUtc || file.LastWriteTimeUtc >= cutoff)
            {
                skipped.Add(new SkippedItem(item.Path, "스캔 뒤에 바뀐 파일"));
                continue;
            }

            // 지우고 나면 FileInfo 정보가 무효화되므로 크기를 먼저 읽어 둔다.
            var size = file.Length;
            if (TryDelete(file, out var reason))
            {
                deleted.Add(item with { Path = path, SizeBytes = size });
                touchedFolders.Add(file.DirectoryName!);
            }
            else
            {
                skipped.Add(new SkippedItem(item.Path, reason));
            }
        }

        foreach (var target in targets.Where(t => t.Recursive))
            RemoveEmptyFolders(target.Root, touchedFolders, cutoff);

        return new CleanResult(Id, deleted, skipped);
    }

    private static bool TryDelete(FileInfo file, out string reason)
    {
        var wasReadOnly = file.IsReadOnly;
        try
        {
            if (wasReadOnly)
                file.IsReadOnly = false;
            file.Delete();
            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex is IOException ? "사용 중" : "사용 중이거나 권한 없음";
            try
            {
                if (wasReadOnly)
                    file.IsReadOnly = true;
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
            }
            return false;
        }
    }

    /// <summary>파일을 지운 폴더 중 비게 된 것을 지운다. 정리 대상 폴더 자체와 최근에 만든 폴더는 남긴다.</summary>
    private static void RemoveEmptyFolders(string root, HashSet<string> touchedFolders, DateTime cutoff)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in touchedFolders)
        {
            for (var current = folder; current is not null && PathGuard.IsUnder(current, root); current = Path.GetDirectoryName(current))
                candidates.Add(current);
        }

        // 깊은 폴더부터 지워야 상위 폴더도 비게 된다.
        foreach (var folder in candidates.OrderByDescending(f => f.Length))
        {
            try
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists || IsLink(info) || info.CreationTimeUtc >= cutoff
                    || info.EnumerateFileSystemInfos("*", ListOptions).Any())
                    continue;

                info.Delete(recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary><paramref name="root"/>부터 <paramref name="folder"/>까지 거치는 폴더 중 링크·정션이 없는지.</summary>
    private static bool HasNoLinksBetween(string root, string folder, HashSet<string> verified)
    {
        var chain = new List<string>();
        for (var current = folder; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
        {
            if (current is null)
                return false;
            if (verified.Contains(current))
                break;

            var info = new DirectoryInfo(current);
            if (!info.Exists || IsLink(info))
                return false;
            chain.Add(current);
        }

        verified.UnionWith(chain);
        return true;
    }

    private List<ResolvedTarget> ResolveTargets(List<string> warnings)
    {
        var result = new List<ResolvedTarget>();
        foreach (var target in _definition.Targets)
        {
            var expanded = Environment.ExpandEnvironmentVariables(target.Path);
            if (expanded.Contains('%'))
            {
                warnings.Add($"경로를 해석하지 못해 건너뜁니다: {target.Path}");
                continue;
            }

            foreach (var folder in ExpandWildcards(expanded))
            {
                var resolved = new ResolvedTarget(PathGuard.Normalize(folder), target.Pattern, target.Recursive);

                if (!resolved.IsExactFile && !_guard.IsAllowedRoot(resolved.Root, out var reason))
                {
                    warnings.Add($"{resolved.Root}: {reason}. 건너뜁니다.");
                    continue;
                }

                if (IsLink(new DirectoryInfo(resolved.Root)))
                {
                    warnings.Add($"{resolved.Root}: 링크 폴더라 건너뜁니다.");
                    continue;
                }

                result.Add(resolved);
            }
        }
        return result;
    }

    /// <summary>경로의 폴더 이름에 있는 * ? 를 실제 폴더로 펼친다. 없는 폴더는 뺀다.</summary>
    private static IEnumerable<string> ExpandWildcards(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        List<string> current = [segments[0] + Path.DirectorySeparatorChar];

        foreach (var segment in segments.Skip(1))
        {
            current = HasWildcard(segment)
                ? current.SelectMany(parent => MatchFolders(parent, segment)).ToList()
                : current.Select(parent => Path.Combine(parent, segment)).ToList();
        }

        return current.Where(Directory.Exists);
    }

    private static List<string> MatchFolders(string parent, string pattern)
    {
        try
        {
            return new DirectoryInfo(parent).EnumerateDirectories(pattern, ListOptions)
                .Where(d => !IsLink(d))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private string? FindRunningProcess()
    {
        foreach (var name in _definition.SkipIfRunning)
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
                process.Dispose();
            if (processes.Length > 0)
                return name;
        }
        return null;
    }

    private static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool HasWildcard(string text) => text.AsSpan().IndexOfAny('*', '?') >= 0;

    private sealed record ResolvedTarget(string Root, string Pattern, bool Recursive)
    {
        /// <summary>와일드카드 없는 파일 하나만 가리키는 대상 (예: MEMORY.DMP). 폴더 보호 검사를 거치지 않는다.</summary>
        public bool IsExactFile => !Recursive && !HasWildcard(Pattern);

        public string? ExactFilePath => IsExactFile ? Path.Combine(Root, Pattern) : null;

        public bool Contains(string fullPath)
        {
            if (IsExactFile)
                return string.Equals(fullPath, ExactFilePath, StringComparison.OrdinalIgnoreCase);

            var inFolder = Recursive
                ? PathGuard.IsUnder(fullPath, Root)
                : string.Equals(Path.GetDirectoryName(fullPath), Root, StringComparison.OrdinalIgnoreCase);

            return inFolder && FileSystemName.MatchesSimpleExpression(Pattern, Path.GetFileName(fullPath));
        }
    }
}
