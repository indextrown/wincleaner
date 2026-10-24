using WinCleaner.Core.Platform;

namespace WinCleaner.Tests;

public class DriveStatusTests
{
    [Fact]
    public void Ratios_are_computed_from_total_and_free()
    {
        var drive = new DriveStatus("C:\\", "", TotalBytes: 1000, FreeBytes: 70, IsSystemDrive: true);

        Assert.Equal(930, drive.UsedBytes);
        Assert.Equal(0.93, drive.UsedRatio, precision: 10);
        Assert.Equal(0.07, drive.FreeRatio, precision: 10);
        Assert.True(drive.IsLowOnSpace);
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, false)]
    [InlineData(50, false)]
    public void Low_space_threshold_is_15_percent(long freeBytes, bool expectedLow)
    {
        var drive = new DriveStatus("D:\\", "", TotalBytes: 100, FreeBytes: freeBytes, IsSystemDrive: false);

        Assert.Equal(expectedLow, drive.IsLowOnSpace);
    }

    [Fact]
    public void Empty_drive_is_not_reported_as_low()
    {
        var drive = new DriveStatus("E:\\", "", TotalBytes: 0, FreeBytes: 0, IsSystemDrive: false);

        Assert.Equal(0, drive.UsedRatio);
        Assert.False(drive.IsLowOnSpace);
    }
}
