using System.Management;
using Microsoft.Win32;

namespace WinCleaner.Safety;

public enum RestorePointOutcome
{
    Created,

    /// <summary>24시간 안에 만든 복원 지점이 있다. Windows는 그동안 새로 만들지 않으므로 그것을 쓴다.</summary>
    RecentExists,

    /// <summary>시스템 보호가 꺼져 있다.</summary>
    Disabled,

    NotElevated,
    Failed,
}

public sealed record RestorePointResult(RestorePointOutcome Outcome, string Message)
{
    /// <summary>되돌아갈 수 있는 복원 지점이 준비됐는지.</summary>
    public bool IsAvailable => Outcome is RestorePointOutcome.Created or RestorePointOutcome.RecentExists;
}

public sealed record RestorePointInfo(uint SequenceNumber, string Description, DateTime CreatedAt);

public interface IRestorePointService
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken ct);

    /// <summary>최근 것이 먼저 오는 복원 지점 목록. 관리자 권한이 없으면 null.</summary>
    Task<IReadOnlyList<RestorePointInfo>?> ListAsync(CancellationToken ct);
}

/// <summary>WMI(root\default:SystemRestore)로 시스템 복원 지점을 만들고 읽는다. 관리자 권한이 필요하다.</summary>
public sealed class WmiRestorePointService(bool isElevated) : IRestorePointService
{
    private const string Namespace = @"\\.\root\default";
    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;
    private const uint ErrorServiceDisabled = 1058;
    private static readonly TimeSpan CreationInterval = TimeSpan.FromHours(24);

    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
    {
        if (!isElevated)
            return new RestorePointResult(RestorePointOutcome.NotElevated, "복원 지점을 만들려면 관리자 권한이 필요합니다.");
        if (IsDisabledByRegistry())
            return Disabled();

        // Windows는 24시간에 한 번만 새 복원 지점을 만들고, 그 안에 요청하면 오류 없이 넘어간다. 그래서 먼저 확인한다.
        var before = await ListAsync(ct) ?? [];
        if (before.FirstOrDefault() is { } latest && DateTime.Now - latest.CreatedAt < CreationInterval)
        {
            return new RestorePointResult(RestorePointOutcome.RecentExists,
                $"{latest.CreatedAt:yyyy-MM-dd HH:mm}에 만든 복원 지점({latest.Description})이 있어 그것을 사용합니다.");
        }

        var code = await Task.Run(() => InvokeCreate(description), ct);
        if (code == ErrorServiceDisabled)
            return Disabled();
        if (code != 0)
            return new RestorePointResult(RestorePointOutcome.Failed, $"복원 지점을 만들지 못했습니다 (오류 코드 {code}).");

        var after = await ListAsync(ct) ?? [];
        return after.Count > before.Count || after.FirstOrDefault()?.SequenceNumber != before.FirstOrDefault()?.SequenceNumber
            ? new RestorePointResult(RestorePointOutcome.Created, $"복원 지점을 만들었습니다 ({DateTime.Now:yyyy-MM-dd HH:mm}).")
            : new RestorePointResult(RestorePointOutcome.Failed, "복원 지점 요청은 성공했지만 새 복원 지점이 보이지 않습니다.");
    }

    public Task<IReadOnlyList<RestorePointInfo>?> ListAsync(CancellationToken ct)
    {
        if (!isElevated)
            return Task.FromResult<IReadOnlyList<RestorePointInfo>?>(null);

        return Task.Run<IReadOnlyList<RestorePointInfo>?>(() =>
        {
            using var searcher = new ManagementObjectSearcher(Namespace, "SELECT * FROM SystemRestore");
            using var results = searcher.Get();
            return results.Cast<ManagementObject>()
                .Select(o => new RestorePointInfo(
                    Convert.ToUInt32(o["SequenceNumber"]),
                    o["Description"] as string ?? "",
                    ManagementDateTimeConverter.ToDateTime((string)o["CreationTime"])))
                .OrderByDescending(p => p.CreatedAt)
                .ToList();
        }, ct);
    }

    private static uint InvokeCreate(string description)
    {
        using var restore = new ManagementClass(Namespace, "SystemRestore", null);
        using var parameters = restore.GetMethodParameters("CreateRestorePoint");
        parameters["Description"] = description;
        parameters["RestorePointType"] = ModifySettings;
        parameters["EventType"] = BeginSystemChange;
        using var result = restore.InvokeMethod("CreateRestorePoint", parameters, null);
        return Convert.ToUInt32(result["ReturnValue"]);
    }

    /// <summary>정책으로 꺼졌거나 모든 드라이브의 시스템 보호가 꺼지면 이 값이 0이 된다.</summary>
    private static bool IsDisabledByRegistry()
    {
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore");
        if (policy?.GetValue("DisableSR") is int disabled && disabled != 0)
            return true;

        using var settings = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
        return settings?.GetValue("RPSessionInterval") is int interval && interval == 0;
    }

    private static RestorePointResult Disabled() => new(RestorePointOutcome.Disabled,
        "시스템 보호가 꺼져 있어 복원 지점을 만들 수 없습니다. 제어판 > 시스템 > 시스템 보호에서 C: 드라이브 보호를 켤 수 있습니다.");
}
