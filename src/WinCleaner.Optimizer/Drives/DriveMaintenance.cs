using System.Management;

namespace WinCleaner.Optimizer.Drives;

public enum MediaKind
{
    Unknown,
    Hdd,
    Ssd,
}

public enum DiskHealth
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy,
}

public sealed record DriveDetails(char Letter, string Model, MediaKind Media, string Bus, DiskHealth Health);

/// <summary>드라이브 정보와 최적화. 테스트에서 실제 디스크를 건드리지 않도록 분리했다.</summary>
public interface IDriveService
{
    IReadOnlyList<DriveDetails> ReadDrives();

    /// <summary>최적화하고 마지막 결과 문구를 돌려준다. 취소되면 OperationCanceledException.</summary>
    Task<string> OptimizeAsync(char letter, Action<string>? onProgress, CancellationToken ct);
}

/// <summary>
/// 드라이브 정보(SSD/HDD, 건강 상태)와 드라이브 최적화 (사양서 3.3 <c>drive.optimize</c>, <c>disk.health</c>).
/// 정보는 Windows 저장소 WMI(root\Microsoft\Windows\Storage)에서 읽으며 일반 권한으로도 읽힌다.
/// </summary>
public sealed class DriveMaintenance : IDriveService
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    public IReadOnlyList<DriveDetails> ReadDrives()
    {
        var disks = new Dictionary<uint, (string Model, MediaKind Media, string Bus, DiskHealth Health)>();
        using (var searcher = new ManagementObjectSearcher(StorageNamespace,
                   "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus FROM MSFT_PhysicalDisk"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject disk in results)
            {
                if (!uint.TryParse(disk["DeviceId"] as string, out var number))
                    continue;
                disks[number] = (
                    disk["FriendlyName"] as string ?? "알 수 없는 디스크",
                    ToMedia(Convert.ToUInt16(disk["MediaType"] ?? 0)),
                    ToBus(Convert.ToUInt16(disk["BusType"] ?? 0)),
                    ToHealth(Convert.ToUInt16(disk["HealthStatus"] ?? 5)));
            }
        }

        var drives = new List<DriveDetails>();
        using (var searcher = new ManagementObjectSearcher(StorageNamespace, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject partition in results)
            {
                var letter = Convert.ToChar(partition["DriveLetter"] ?? '\0');
                if (!char.IsLetter(letter) || !disks.TryGetValue(Convert.ToUInt32(partition["DiskNumber"]), out var disk))
                    continue;
                drives.Add(new DriveDetails(char.ToUpperInvariant(letter), disk.Model, disk.Media, disk.Bus, disk.Health));
            }
        }

        return drives.OrderBy(d => d.Letter).ToList();
    }

    /// <summary>
    /// defrag.exe의 /O는 매체에 맞는 최적화를 고른다: SSD는 TRIM, HDD는 조각 모음. /U는 진행률을 출력한다.
    /// 관리자 권한이 필요하다.
    /// </summary>
    public static string DefragArguments(char letter) => $"{letter}: /O /U";

    public async Task<string> OptimizeAsync(char letter, Action<string>? onProgress, CancellationToken ct)
    {
        var (exitCode, output) = await ConsoleProcess.RunAsync("defrag.exe", DefragArguments(letter), onProgress, ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"드라이브 최적화 실패 (코드 {exitCode}): {LastLine(output)}");
        return LastLine(output);
    }

    internal static MediaKind ToMedia(ushort value) => value switch
    {
        3 => MediaKind.Hdd,
        4 => MediaKind.Ssd,
        _ => MediaKind.Unknown,
    };

    internal static DiskHealth ToHealth(ushort value) => value switch
    {
        0 => DiskHealth.Healthy,
        1 => DiskHealth.Warning,
        2 => DiskHealth.Unhealthy,
        _ => DiskHealth.Unknown,
    };

    private static string ToBus(ushort value) => value switch
    {
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        17 => "NVMe",
        _ => "",
    };

    private static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
