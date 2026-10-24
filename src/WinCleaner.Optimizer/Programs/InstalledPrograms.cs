using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinCleaner.Optimizer.Programs;

/// <param name="Key">레지스트리 위치와 키 이름. 같은 프로그램을 다시 찾을 때 쓴다.</param>
/// <param name="SizeBytes">프로그램이 설치할 때 알려 준 크기(EstimatedSize). 없거나 실제와 다를 수 있다.</param>
/// <param name="IsPerUser">현재 사용자에게만 설치된 프로그램(HKCU).</param>
public sealed record InstalledProgram(
    string Key,
    string Name,
    string? Publisher,
    string? Version,
    DateTime? InstallDate,
    long? SizeBytes,
    string UninstallCommand,
    bool IsPerUser,
    bool Is32Bit);

/// <summary>설치된 프로그램 목록. 테스트에서 실제 레지스트리를 읽지 않도록 분리했다.</summary>
public interface IProgramCatalog
{
    IReadOnlyList<InstalledProgram> List();
}

/// <summary>프로그램 제거 프로그램을 실행한다. 테스트에서 실제로 실행하지 않도록 분리했다.</summary>
public interface IProgramUninstaller
{
    /// <summary>제거 프로그램을 실행하고 끝날 때까지 기다린다. 끝난 코드를 돌려준다.</summary>
    Task<int> RunAsync(string fileName, string arguments, CancellationToken ct);
}

/// <summary>
/// 설정 > 앱(프로그램 및 기능)과 같은 기준으로 레지스트리의 제거 항목을 읽는다 (사양서 3.3 <c>programs.uninstall</c>).
/// Windows 구성 요소(SystemComponent), 업데이트(ParentKeyName, ReleaseType), 제거 명령이 없는 항목은 뺀다.
/// Microsoft Store 앱은 이 목록에 없다.
/// </summary>
public sealed class RegistryProgramCatalog : IProgramCatalog
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledProgram> List()
    {
        var programs = new List<InstalledProgram>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, programs);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, programs);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, programs);

        // 64비트·32비트 위치에 같은 프로그램이 두 번 등록되는 경우가 있다.
        return programs
            .GroupBy(p => (p.Name.ToUpperInvariant(), p.Version))
            .Select(g => g.First())
            .OrderByDescending(p => p.SizeBytes ?? -1)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void Read(RegistryHive hive, RegistryView view, List<InstalledProgram> programs)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = root.OpenSubKey(UninstallKey);
        if (uninstall is null)
            return;

        foreach (var keyName in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(keyName);
            if (key is null || ReadProgram(key) is not { } program)
                continue;

            programs.Add(program with
            {
                Key = $"{hive}:{view}:{keyName}",
                IsPerUser = hive == RegistryHive.CurrentUser,
                Is32Bit = view == RegistryView.Registry32,
            });
        }
    }

    private static InstalledProgram? ReadProgram(RegistryKey key)
    {
        if (key.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name))
            return null;
        if (key.GetValue("SystemComponent") is int system && system == 1)
            return null;
        if (key.GetValue("ParentKeyName") is string)
            return null;
        if (key.GetValue("ReleaseType") is string release
            && (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Equals("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (key.GetValue("UninstallString") is not string command || string.IsNullOrWhiteSpace(command))
            return null;

        long? size = key.GetValue("EstimatedSize") is int kilobytes && kilobytes > 0 ? kilobytes * 1024L : null;
        DateTime? installed = DateTime.TryParseExact(key.GetValue("InstallDate") as string, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

        return new InstalledProgram("", name.Trim(), (key.GetValue("Publisher") as string)?.Trim(),
            (key.GetValue("DisplayVersion") as string)?.Trim(), installed, size, command.Trim(), false, false);
    }
}

public static partial class UninstallCommand
{
    /// <summary>
    /// 레지스트리의 제거 명령을 실행 파일과 인수로 나눈다. 해석할 수 없으면 null.
    /// 경로 없이 적힌 Windows 프로그램(MsiExec.exe, rundll32.exe)은 System32에서만 찾는다.
    /// PATH 앞에 끼워 넣은 같은 이름의 가짜 프로그램이 관리자 권한으로 실행되지 않게 하기 위해서다.
    /// MSI의 /I(수정·복구 창)는 /X(제거)로 바꾼다.
    /// </summary>
    public static (string FileName, string Arguments)? Parse(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0)
            return null;

        string file, arguments;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
                return null;
            file = text[1..end];
            arguments = text[(end + 1)..].Trim();
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0)
                return null;
            file = text[..(exe + 4)];
            arguments = text[(exe + 4)..].Trim();
        }

        if (!Path.IsPathFullyQualified(file))
        {
            var system = Path.Combine(Environment.SystemDirectory, Path.GetFileName(file));
            if (!File.Exists(system))
                return null;
            file = system;
        }

        if (Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            arguments = MsiInstallSwitch().Replace(arguments, "$1/X");

        return (file, arguments);
    }

    [GeneratedRegex(@"(^|\s)/I(?=\s*\{)", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstallSwitch();
}

/// <summary>제거 프로그램을 실행한다. 대부분 자기 매니페스트로 관리자 권한을 요청한다.</summary>
public sealed class ProcessUninstaller : IProgramUninstaller
{
    public async Task<int> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true })
            ?? throw new InvalidOperationException("제거 프로그램을 실행하지 못했습니다.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
