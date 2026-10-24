using System.Runtime.InteropServices;
using System.Text;
using WinCleaner.Core;
using WinCleaner.Core.Optimization;

namespace WinCleaner.Optimizer;

public sealed record PowerScheme(Guid Id, string Name);

/// <summary>전원 관리 옵션을 읽고 바꾸는 부분. 테스트에서 실제 설정을 건드리지 않도록 분리했다.</summary>
public interface IPowerSchemes
{
    IReadOnlyList<PowerScheme> List();

    Guid GetActive();

    void SetActive(Guid scheme);
}

/// <summary>
/// 전원 관리 옵션 바꾸기 (사양서 3.3 <c>power.plan</c>). PC마다 옵션 목록과 GUID가 달라서
/// (예: 복제한 "고성능") 화면에서 고른 옵션을 <paramref name="target"/>으로 받는다.
/// 되돌릴 때는 목표가 필요 없으므로 기록 화면은 target 없이 만든다.
/// </summary>
public sealed class PowerPlanTweak(IPowerSchemes schemes, PowerScheme? target) : ITweak
{
    public const string TweakId = "power.plan";

    /// <summary>Windows 기본 "균형 조정". 되돌릴 값이 없거나 이상하면 이것으로 돌아간다.</summary>
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    private const string SchemeKey = "scheme";

    public string Id => TweakId;
    public string Name => target is null ? "전원 관리 옵션" : $"전원 관리 옵션: {target.Name}";
    public string Description => "고성능 계열은 CPU를 늘 빠르게 돌려 반응이 빨라지지만 전기를 더 씁니다. 노트북은 '균형 조정'을 권장합니다.";
    public RiskLevel Risk => RiskLevel.Low;
    public bool RequiresElevation => false;
    public bool RequiresRestart => false;

    public Task<TweakState> GetStateAsync(CancellationToken ct)
    {
        var active = schemes.GetActive();
        var name = schemes.List().FirstOrDefault(s => s.Id == active)?.Name ?? active.ToString();
        return Task.FromResult(new TweakState(target is not null && active == target.Id, $"현재: {name}"));
    }

    public Task<TweakBackup> ApplyAsync(CancellationToken ct)
    {
        if (target is null)
            throw new InvalidOperationException("바꿀 전원 관리 옵션을 고르지 않았습니다.");

        var before = schemes.GetActive();
        schemes.SetActive(target.Id);
        return Task.FromResult(new TweakBackup(Id, new Dictionary<string, string?> { [SchemeKey] = before.ToString() }));
    }

    public Task RevertAsync(TweakBackup? backup, CancellationToken ct)
    {
        if (backup is not null && backup.TweakId != Id)
            throw new ArgumentException($"다른 항목({backup.TweakId})의 백업입니다.", nameof(backup));

        // 기록 파일의 값은 이 PC에 실제로 있는 옵션일 때만 쓴다.
        var available = schemes.List();
        var restore = Guid.TryParse(backup?.Values.GetValueOrDefault(SchemeKey), out var saved) && available.Any(s => s.Id == saved)
            ? saved
            : available.Any(s => s.Id == Balanced) ? Balanced : available.First().Id;

        schemes.SetActive(restore);
        return Task.CompletedTask;
    }
}

/// <summary>powrprof.dll로 전원 관리 옵션을 다룬다. 바꾸는 데 관리자 권한은 필요 없다.</summary>
public sealed partial class PowrProfSchemes : IPowerSchemes
{
    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259;

    public IReadOnlyList<PowerScheme> List()
    {
        var schemes = new List<PowerScheme>();
        for (uint index = 0; ; index++)
        {
            var id = Guid.Empty;
            var size = (uint)Marshal.SizeOf<Guid>();
            var result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, ref id, ref size);
            if (result == ErrorNoMoreItems)
                break;
            Check(result, "PowerEnumerate");
            schemes.Add(new PowerScheme(id, ReadName(id)));
        }
        return schemes;
    }

    public Guid GetActive()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var pointer), "PowerGetActiveScheme");
        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public void SetActive(Guid scheme) => Check(PowerSetActiveScheme(IntPtr.Zero, in scheme), "PowerSetActiveScheme");

    private static string ReadName(Guid scheme)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        var buffer = new byte[size];
        if (size == 0 || PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
            return scheme.ToString();
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static void Check(uint result, string function)
    {
        if (result != 0)
            throw new InvalidOperationException($"{function} 실패 (오류 코드 {result})");
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid,
        uint accessFlags, uint index, ref Guid buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(IntPtr rootPowerKey, in Guid schemeGuid, IntPtr subGroupGuid,
        IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
