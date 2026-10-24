using WinCleaner.Core.Platform;
using WinCleaner.Memory;

namespace WinCleaner.Tests;

public class MemoryServiceTests
{
    private readonly WindowsMemoryService _service = new();

    [Fact]
    public void Groups_processes_by_name_and_sorts_by_memory()
    {
        ProcessMemory[] processes =
        [
            new(10, "chrome", 300, 200),
            new(11, "Chrome", 500, 400),
            new(20, "notepad", 700, 600),
            new(30, "explorer", 100, 50),
        ];

        var groups = ProcessGrouping.ByName(processes);

        Assert.Equal(["chrome", "notepad", "explorer"], groups.Select(g => g.Name));
        Assert.Equal(800, groups[0].WorkingSetBytes);
        Assert.Equal(600, groups[0].PrivateBytes);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal([11, 10], groups[0].Processes.Select(p => p.Id));
    }

    [Fact]
    public void System_memory_is_read_from_this_pc()
    {
        var memory = _service.ReadSystem();

        Assert.True(memory.TotalBytes > 0);
        Assert.InRange(memory.AvailableBytes, 1, memory.TotalBytes);
        Assert.True(memory.CacheBytes > 0);
        Assert.True(memory.CommitLimitBytes >= memory.CommitBytes);
    }

    [Fact]
    public void Process_list_includes_this_test_process()
    {
        var self = Environment.ProcessId;

        var processes = _service.ReadProcesses();

        var me = Assert.Single(processes, p => p.Id == self);
        Assert.True(me.WorkingSetBytes > 0);
        Assert.DoesNotContain(processes, p => p.Id == 0);
    }

    [Fact]
    public void Trimming_own_process_succeeds_without_ending_it()
    {
        var result = _service.TrimWorkingSets([Environment.ProcessId]);

        Assert.Equal(1, result.Trimmed);
        Assert.Equal(0, result.Failed);
        Assert.True(result.FreedBytes >= 0);
    }

    [Fact]
    public void System_and_idle_processes_are_never_trimmed()
    {
        var result = _service.TrimWorkingSets([0, 4]);

        Assert.Equal(0, result.Trimmed);
        Assert.Equal(2, result.Failed);
    }

    [Fact]
    public void Describing_own_process_does_not_throw()
    {
        _service.DescribeProcess(Environment.ProcessId);
        Assert.Null(_service.DescribeProcess(int.MaxValue));
    }

    [Fact]
    public void Purging_standby_list_needs_admin()
    {
        // 관리자 권한으로 테스트를 돌리면 실제로 비워지므로 그때는 건너뛴다.
        if (Elevation.IsElevated)
            return;

        var result = _service.PurgeStandbyList();

        Assert.False(result.Succeeded);
        Assert.Contains("관리자", result.Message);
    }
}
