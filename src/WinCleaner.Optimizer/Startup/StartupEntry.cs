using System.Diagnostics;

namespace WinCleaner.Optimizer.Startup;

/// <summary>시작 프로그램이 등록되는 곳. 작업 관리자의 "시작 프로그램" 탭과 같은 범위다.</summary>
public enum StartupLocation
{
    /// <summary>HKCU\...\Run</summary>
    UserRun,

    /// <summary>HKLM\...\Run</summary>
    MachineRun,

    /// <summary>HKLM\WOW6432Node\...\Run (32비트 프로그램)</summary>
    MachineRun32,

    /// <summary>%APPDATA%\...\Start Menu\Programs\Startup</summary>
    UserFolder,

    /// <summary>%ProgramData%\...\Start Menu\Programs\StartUp</summary>
    MachineFolder,
}

/// <param name="Name">레지스트리 값 이름이나 시작 폴더의 파일 이름. StartupApproved 값 이름으로도 쓴다.</param>
/// <param name="Command">실행 명령, 시작 폴더 항목이면 파일 경로.</param>
public sealed record StartupEntry(StartupLocation Location, string Name, string Command, bool IsEnabled)
{
    public bool IsMachineWide => Location is StartupLocation.MachineRun or StartupLocation.MachineRun32 or StartupLocation.MachineFolder;

    public bool IsFolderItem => Location is StartupLocation.UserFolder or StartupLocation.MachineFolder;

    public string DisplayName => IsFolderItem ? Path.GetFileNameWithoutExtension(Name) : Name;

    public string LocationText => Location switch
    {
        StartupLocation.UserRun => "현재 사용자",
        StartupLocation.MachineRun => "모든 사용자",
        StartupLocation.MachineRun32 => "모든 사용자 (32비트)",
        StartupLocation.UserFolder => "시작 폴더",
        _ => "모든 사용자 시작 폴더",
    };

    /// <summary>명령에서 실행 파일 경로만 뽑는다. 알 수 없으면 null.</summary>
    public string? ExecutablePath
    {
        get
        {
            if (IsFolderItem)
                return Command;

            var command = Environment.ExpandEnvironmentVariables(Command.Trim());
            string candidate;
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                candidate = end > 1 ? command[1..end] : command.Trim('"');
            }
            else
            {
                var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                candidate = exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
            }
            return Path.IsPathFullyQualified(candidate) ? candidate : null;
        }
    }

    /// <summary>실행 파일이 없는 항목. 지운 프로그램이 남긴 것일 수 있다.</summary>
    public bool IsMissingFile => ExecutablePath is { } path && !File.Exists(path);

    public string? Publisher
    {
        get
        {
            if (ExecutablePath is not { } path || IsFolderItem || !File.Exists(path))
                return null;
            try
            {
                return FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() is { Length: > 0 } company ? company : null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// 작업 관리자와 같은 방식으로 켜고 끈다. 항목은 지우지 않고 Explorer\StartupApproved 아래 값만 바꾼다.
/// 값은 12바이트이고 첫 바이트가 짝수(02, 06)면 켜짐, 홀수(03, 07)면 꺼짐이다. 꺼짐이면 4번째 바이트부터 끈 시각(FILETIME)이 들어간다.
/// </summary>
public static class StartupApproval
{
    public const int Length = 12;

    public static bool IsEnabled(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;

    public static byte[] Enabled() => [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] Disabled(DateTime utcNow)
    {
        var value = new byte[Length];
        value[0] = 0x03;
        BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(value, 4);
        return value;
    }

    /// <summary>되돌릴 때 받아들일 수 있는 값인지. 기록 파일에서 읽은 값을 그대로 쓰지 않기 위해 검사한다.</summary>
    public static bool IsValid(byte[] value) => value.Length == Length && value[0] is 0x02 or 0x03 or 0x06 or 0x07;
}
