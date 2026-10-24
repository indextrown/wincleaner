namespace WinCleaner.Core.Platform;

public sealed record DriveStatus(string Name, string Label, long TotalBytes, long FreeBytes, bool IsSystemDrive)
{
    /// <summary>여유 공간이 이 비율 미만이면 부족으로 본다 (사양서 1.1).</summary>
    public const double LowSpaceThreshold = 0.15;

    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedRatio => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;

    public double FreeRatio => TotalBytes == 0 ? 0 : (double)FreeBytes / TotalBytes;

    public bool IsLowOnSpace => TotalBytes > 0 && FreeRatio < LowSpaceThreshold;
}
