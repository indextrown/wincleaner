using Microsoft.Win32;
using WinCleaner.Core;
using WinCleaner.Core.Optimization;

namespace WinCleaner.Optimizer;

/// <summary>hiberfil.sys 종류. 레지스트리 HiberFileType 값과 같다.</summary>
public enum HiberFileType
{
    Reduced = 1,
    Full = 2,
}

/// <param name="Enabled">최대 절전이 켜져 있는지.</param>
/// <param name="FileType">hiberfil.sys 종류. 레지스트리에 값이 없으면(Windows 기본값) null.</param>
/// <param name="FileBytes">hiberfil.sys 크기. 파일이 없으면 null.</param>
public sealed record HibernationStatus(bool Enabled, HiberFileType? FileType, long? FileBytes);

/// <summary>최대 절전 설정을 읽고 바꾸는 부분. 테스트에서 실제 설정을 건드리지 않도록 분리했다.</summary>
public interface IHibernationControl
{
    HibernationStatus Read();

    Task SetEnabledAsync(bool enabled, CancellationToken ct);

    Task SetFileTypeAsync(HiberFileType type, CancellationToken ct);
}

/// <summary>
/// 최대 절전 끄기 (사양서 3.3 <c>hibernate</c>). hiberfil.sys(보통 RAM의 40%)가 바로 사라진다.
/// 대신 최대 절전 모드와 빠른 시작을 쓸 수 없게 된다. 되돌리면 Windows가 파일을 다시 만든다.
/// </summary>
public sealed class HibernationTweak(IHibernationControl control) : ITweak
{
    private const string EnabledKey = "enabled";
    private const string FileTypeKey = "fileType";

    public HibernationTweak() : this(new PowerCfgHibernationControl())
    {
    }

    public string Id => "hibernate";
    public string Name => "최대 절전 끄기";
    public string Description =>
        "hiberfil.sys를 지워 C: 공간을 확보합니다. 대신 최대 절전 모드와 빠른 시작을 쓸 수 없게 되어 부팅이 조금 느려질 수 있습니다. 노트북에서 최대 절전을 쓴다면 끄지 마세요.";
    public RiskLevel Risk => RiskLevel.Medium;
    public bool RequiresElevation => true;
    public bool RequiresRestart => false;

    public Task<TweakState> GetStateAsync(CancellationToken ct)
    {
        var status = control.Read();
        var text = status.Enabled
            ? status.FileBytes is { } bytes ? $"최대 절전 켜짐 · hiberfil.sys {ByteSize.Format(bytes)}" : "최대 절전 켜짐"
            : "최대 절전 꺼짐 · hiberfil.sys 없음";
        return Task.FromResult(new TweakState(IsApplied: !status.Enabled, text));
    }

    public async Task<TweakBackup> ApplyAsync(CancellationToken ct)
    {
        var before = control.Read();
        var backup = new TweakBackup(Id, new Dictionary<string, string?>
        {
            [EnabledKey] = before.Enabled ? "true" : "false",
            [FileTypeKey] = before.FileType?.ToString(),
        });

        if (before.Enabled)
            await control.SetEnabledAsync(false, ct);
        return backup;
    }

    public async Task RevertAsync(TweakBackup? backup, CancellationToken ct)
    {
        // 백업은 사용자 폴더의 파일에서 오므로 아는 값만 받아들이고 나머지는 Windows 기본값으로 둔다.
        var enabled = true;
        HiberFileType? fileType = null;
        if (backup is not null)
        {
            if (backup.TweakId != Id)
                throw new ArgumentException($"다른 항목({backup.TweakId})의 백업입니다.", nameof(backup));
            if (backup.Values.GetValueOrDefault(EnabledKey) == "false")
                enabled = false;
            if (Enum.TryParse<HiberFileType>(backup.Values.GetValueOrDefault(FileTypeKey), out var parsed)
                && Enum.IsDefined(parsed))
                fileType = parsed;
        }

        if (!enabled)
            return;

        await control.SetEnabledAsync(true, ct);
        if (fileType is { } type)
            await control.SetFileTypeAsync(type, ct);
    }
}

/// <summary>레지스트리로 상태를 읽고 powercfg.exe로 바꾼다. 바꾸려면 관리자 권한이 필요하다.</summary>
public sealed class PowerCfgHibernationControl : IHibernationControl
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    public HibernationStatus Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PowerKey);

        // HibernateEnabled가 없으면 Windows는 HibernateEnabledDefault를 따른다.
        var enabledValue = key?.GetValue("HibernateEnabled") ?? key?.GetValue("HibernateEnabledDefault");
        var fileBytes = FindHiberFileSize();
        var enabled = enabledValue is int value ? value != 0 : fileBytes is not null;

        HiberFileType? fileType = key?.GetValue("HiberFileType") is int type && Enum.IsDefined((HiberFileType)type)
            ? (HiberFileType)type
            : null;

        return new HibernationStatus(enabled, fileType, fileBytes);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken ct) =>
        RunPowerCfgAsync(enabled ? "/hibernate on" : "/hibernate off", ct);

    public Task SetFileTypeAsync(HiberFileType type, CancellationToken ct) =>
        RunPowerCfgAsync(type == HiberFileType.Reduced ? "/hibernate /type reduced" : "/hibernate /type full", ct);

    /// <summary>
    /// hiberfil.sys는 Windows가 잠가 두어 파일 정보를 직접 읽으면 실패한다. 폴더 목록으로 읽으면 크기를 알 수 있다.
    /// </summary>
    private static long? FindHiberFileSize()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        return new DirectoryInfo(root).EnumerateFiles("hiberfil.sys", options).FirstOrDefault()?.Length;
    }

    private static async Task RunPowerCfgAsync(string arguments, CancellationToken ct)
    {
        var (exitCode, output) = await ConsoleProcess.RunAsync("powercfg.exe", arguments, null, ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"powercfg {arguments} 실패 (코드 {exitCode}): {output}");
    }
}
