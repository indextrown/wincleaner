using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinCleaner.Memory;

/// <param name="CacheBytes">시스템 캐시. 대기 목록(최근에 쓴 파일을 담아 둔 메모리)과 시스템 작업 집합을 더한 값이다.</param>
/// <param name="CommitBytes">프로그램들이 확보해 둔 메모리(커밋). 한도를 넘으면 메모리 부족 오류가 난다.</param>
public sealed record SystemMemory(long TotalBytes, long AvailableBytes, long CacheBytes, long CommitBytes, long CommitLimitBytes)
{
    public long UsedBytes => TotalBytes - AvailableBytes;

    public double UsedRatio => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
}

/// <param name="WorkingSetBytes">지금 실제 메모리(RAM)에 올라와 있는 크기. 작업 관리자의 "메모리" 열과 비슷하다.</param>
/// <param name="PrivateBytes">이 프로세스만 쓰는 메모리. 다른 프로세스와 나눠 쓰는 부분은 빠진다.</param>
public sealed record ProcessMemory(int Id, string Name, long WorkingSetBytes, long PrivateBytes);

public sealed record TrimResult(int Trimmed, int Failed, long FreedBytes);

public sealed record PurgeResult(bool Succeeded, string Message, long FreedCacheBytes);

/// <summary>메모리 현황과 수동 정리 (사양서 3.2). 테스트에서 실제 시스템을 건드리지 않도록 분리했다.</summary>
public interface IMemoryService
{
    SystemMemory ReadSystem();

    IReadOnlyList<ProcessMemory> ReadProcesses();

    /// <summary>실행 파일의 설명(예: "Google Chrome"). 알 수 없으면 null.</summary>
    string? DescribeProcess(int processId);

    /// <summary>프로세스의 작업 집합을 비운다. 프로세스는 종료되지 않는다.</summary>
    TrimResult TrimWorkingSets(IReadOnlyCollection<int> processIds);

    /// <summary>대기 목록을 비운다. 관리자 권한이 필요하다.</summary>
    PurgeResult PurgeStandbyList();
}

/// <summary>프로세스를 같은 실행 파일 이름끼리 묶는다. Chrome처럼 프로세스가 수십 개인 프로그램을 한 줄로 보여 주기 위해서다.</summary>
public static class ProcessGrouping
{
    public sealed record Group(string Name, IReadOnlyList<ProcessMemory> Processes)
    {
        public long WorkingSetBytes => Processes.Sum(p => p.WorkingSetBytes);
        public long PrivateBytes => Processes.Sum(p => p.PrivateBytes);
        public int Count => Processes.Count;
    }

    public static IReadOnlyList<Group> ByName(IEnumerable<ProcessMemory> processes) =>
        processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Group(g.First().Name, g.OrderByDescending(p => p.WorkingSetBytes).ToList()))
            .OrderByDescending(g => g.WorkingSetBytes)
            .ToList();
}

public sealed class WindowsMemoryService : IMemoryService
{
    // Idle(0)과 System(4)은 정리 대상에서 뺀다.
    private static readonly HashSet<int> Untouchable = [0, 4];

    public SystemMemory ReadSystem()
    {
        var info = new NativeMethods.PerformanceInformation { Size = (uint)Marshal.SizeOf<NativeMethods.PerformanceInformation>() };
        if (!NativeMethods.GetPerformanceInfo(ref info, info.Size))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var page = (long)info.PageSize;
        return new SystemMemory(
            (long)info.PhysicalTotal * page,
            (long)info.PhysicalAvailable * page,
            (long)info.SystemCache * page,
            (long)info.CommitTotal * page,
            (long)info.CommitLimit * page);
    }

    public IReadOnlyList<ProcessMemory> ReadProcesses()
    {
        // GetProcesses는 프로세스를 열지 않고 시스템 스냅숏에서 크기를 읽으므로 권한 없이도 모든 프로세스가 보인다.
        var processes = Process.GetProcesses();
        try
        {
            var result = new List<ProcessMemory>(processes.Length);
            foreach (var process in processes)
            {
                if (process.Id == 0)
                    continue;
                try
                {
                    result.Add(new ProcessMemory(process.Id, process.ProcessName, process.WorkingSet64, process.PrivateMemorySize64));
                }
                catch (InvalidOperationException)
                {
                    // 읽는 사이에 끝난 프로세스
                }
            }
            return result;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public string? DescribeProcess(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
            return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                return null;

            var description = FileVersionInfo.GetVersionInfo(new string(buffer, 0, (int)size)).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public TrimResult TrimWorkingSets(IReadOnlyCollection<int> processIds)
    {
        var before = ReadProcesses().ToDictionary(p => p.Id, p => p.WorkingSetBytes);
        int trimmed = 0, failed = 0;

        foreach (var id in processIds)
        {
            if (Untouchable.Contains(id))
            {
                failed++;
                continue;
            }

            // 다른 사용자나 관리자 권한 프로세스는 열리지 않는다. 실패로 세고 넘어간다.
            var handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessSetQuota, false, id);
            if (handle == 0)
            {
                failed++;
                continue;
            }

            try
            {
                if (NativeMethods.EmptyWorkingSet(handle))
                    trimmed++;
                else
                    failed++;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        var after = ReadProcesses().ToDictionary(p => p.Id, p => p.WorkingSetBytes);
        var freed = processIds.Sum(id =>
            before.TryGetValue(id, out var b) && after.TryGetValue(id, out var a) ? Math.Max(0, b - a) : 0);
        return new TrimResult(trimmed, failed, freed);
    }

    public PurgeResult PurgeStandbyList()
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
            return new PurgeResult(false, "대기 목록을 비우려면 관리자 권한이 필요합니다.", 0);

        var before = ReadSystem().CacheBytes;
        var command = NativeMethods.MemoryPurgeStandbyList;
        var status = NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0)
            return new PurgeResult(false, $"대기 목록을 비우지 못했습니다 (NTSTATUS 0x{status:X8}).", 0);

        var freed = Math.Max(0, before - ReadSystem().CacheBytes);
        return new PurgeResult(true, "대기 목록을 비웠습니다.", freed);
    }

    /// <summary>관리자 토큰에 있지만 꺼져 있는 권한을 켠다. 토큰에 그 권한이 없으면 false.</summary>
    private static bool EnablePrivilege(string name)
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery, out var token))
            return false;

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, name, out var luid))
                return false;

            var privileges = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeMethods.PrivilegeEnabled,
            };

            // 성공해도 권한이 토큰에 없으면 ERROR_NOT_ALL_ASSIGNED가 남는다.
            return NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0)
                   && Marshal.GetLastPInvokeError() != NativeMethods.ErrorNotAllAssigned;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
