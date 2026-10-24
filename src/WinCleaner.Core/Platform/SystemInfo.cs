using Microsoft.Win32;

namespace WinCleaner.Core.Platform;

public static class SystemInfo
{
    /// <summary>예: "Windows 10 Pro 22H2 (빌드 19045)"</summary>
    public static string GetOsDescription()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        var displayVersion = key?.GetValue("DisplayVersion") as string;
        var build = Environment.OSVersion.Version.Build;

        // Windows 11도 레지스트리 ProductName에는 "Windows 10"으로 남아 있다.
        if (build >= 22000)
            product = product.Replace("Windows 10", "Windows 11");

        return displayVersion is null
            ? $"{product} (빌드 {build})"
            : $"{product} {displayVersion} (빌드 {build})";
    }

    /// <summary>준비된 고정 디스크 목록. USB·네트워크 드라이브는 제외한다.</summary>
    public static IReadOnlyList<DriveStatus> GetFixedDrives()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);

        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveStatus(
                d.Name,
                d.VolumeLabel,
                d.TotalSize,
                d.TotalFreeSpace,
                string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
