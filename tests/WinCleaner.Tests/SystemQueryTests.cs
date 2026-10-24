using WinCleaner.Core.Platform;
using WinCleaner.Memory;

namespace WinCleaner.Tests;

/// <summary>실제 PC 상태를 읽는 테스트. 읽기 전용이라 그대로 실행해도 안전하다.</summary>
public class SystemQueryTests
{
    [Fact]
    public void Fixed_drives_include_the_system_drive()
    {
        var drives = SystemInfo.GetFixedDrives();

        Assert.Contains(drives, d => d.IsSystemDrive && d.TotalBytes > 0);
    }

    [Fact]
    public void Os_description_contains_build_number()
    {
        var build = Environment.OSVersion.Version.Build.ToString();

        Assert.Contains(build, SystemInfo.GetOsDescription());
    }

    [Fact]
    public void Memory_status_reports_physical_memory()
    {
        var status = MemoryInfo.GetStatus();

        Assert.True(status.TotalBytes > 0);
        Assert.InRange(status.AvailableBytes, 1, status.TotalBytes);
    }
}
