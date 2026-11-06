namespace WinCleaner.Memory;

/// <param name="UsedBefore">정리 전 사용 중 메모리.</param>
/// <param name="StandbyPurged">대기 목록을 비웠는지. 관리자 권한이 없으면 false.</param>
/// <param name="StandbyMessage">대기 목록 결과 문구.</param>
/// <param name="StandbyFreedBytes">줄어든 캐시 크기.</param>
/// <param name="Trim">작업 집합 비우기 결과.</param>
/// <param name="KeptNames">지금 쓰는 프로그램이라 건드리지 않은 프로그램 이름 (예: 게임).</param>
public sealed record QuickOptimizeResult(
    long UsedBefore,
    long UsedAfter,
    bool StandbyPurged,
    string StandbyMessage,
    long StandbyFreedBytes,
    TrimResult Trim,
    IReadOnlyList<string> KeptNames);

/// <summary>
/// 게임 전이나 버벅일 때 쓰는 원클릭 메모리 정리 (사양서 3.2). 사용자가 누를 때 한 번만 실행하고, 자동으로 반복하지 않는다.
/// 1) 백그라운드 프로그램 중 메모리를 많이 쓰는 것의 작업 집합을 비우고
/// 2) 관리자 권한이면 대기 목록을 비운다. 1)에서 밀려난 메모리도 대기 목록으로 가므로 이 순서가 여유 메모리를 가장 많이 만든다.
/// </summary>
public sealed class QuickMemoryOptimizer(IMemoryService memory, IForegroundApps foreground)
{
    /// <summary>이보다 작은 프로그램은 비워도 얻는 것이 거의 없어 건드리지 않는다.</summary>
    public const long MinimumWorkingSetBytes = 50L << 20;

    // 비우면 화면·소리·입력이 잠깐 끊길 수 있는 Windows 핵심 프로세스와, 게임하면서 같이 쓰는 음성·방송 프로그램
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso",
        "dwm", "audiodg", "fontdrvhost", "ctfmon", "sihost", "TextInputHost",
        "MsMpEng", "NisSrv", "SecurityHealthService",
        "Discord", "obs64", "obs32",
    };

    public QuickOptimizeResult Run(bool purgeStandby)
    {
        var usedBefore = memory.ReadSystem().UsedBytes;
        var keep = foreground.ProcessesToKeep();
        var processes = memory.ReadProcesses();

        var targets = processes
            .Where(p => p.WorkingSetBytes >= MinimumWorkingSetBytes && !Protected.Contains(p.Name) && !keep.Contains(p.Id))
            .Select(p => p.Id)
            .ToList();
        var keptNames = processes
            .Where(p => keep.Contains(p.Id) && p.Id != Environment.ProcessId)
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var trim = memory.TrimWorkingSets(targets);
        var purge = purgeStandby
            ? memory.PurgeStandbyList()
            : new PurgeResult(false, "관리자 권한이 아니라 대기 목록 비우기는 건너뛰었습니다.", 0);

        return new QuickOptimizeResult(usedBefore, memory.ReadSystem().UsedBytes, purge.Succeeded, purge.Message,
            purge.FreedCacheBytes, trim, keptNames);
    }
}
