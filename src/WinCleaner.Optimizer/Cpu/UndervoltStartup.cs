using System.Globalization;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace WinCleaner.Optimizer.Cpu;

/// <param name="IsRegistered">작업 스케줄러에 자동 적용 작업이 있는지.</param>
/// <param name="IsEnabled">작업이 켜져 있는지. 비정상 종료를 감지하면 작업이 스스로 꺼진다.</param>
/// <param name="Millivolts">작업이 적용하는 값.</param>
/// <param name="LastRun">마지막 실행 결과 (상태 파일 한 줄). 없으면 null.</param>
public sealed record UndervoltStartupState(bool IsRegistered, bool IsEnabled, int? Millivolts, string? LastRun);

/// <summary>로그인할 때 전압 오프셋을 다시 적용하는 작업. 테스트에서 실제 작업 스케줄러를 건드리지 않도록 분리했다.</summary>
public interface IUndervoltStartup
{
    Task<UndervoltStartupState> ReadAsync(CancellationToken ct);

    Task RegisterAsync(int millivolts, CancellationToken ct);

    Task UnregisterAsync(CancellationToken ct);
}

/// <summary>
/// 전압 오프셋은 재부팅하면 0으로 돌아가므로, Windows에 로그인할 때와 절전에서 깨어날 때 다시 적용하는 작업을 등록한다.
/// <list type="bullet">
/// <item>지금 사용자로, 관리자 권한(가장 높은 권한)으로 실행한다. 암호는 저장하지 않는다.</item>
/// <item>스크립트는 파일로 두지 않고 작업 정의 안에 넣는다(-EncodedCommand). 누가 파일을 바꿔 관리자 권한으로 실행되게 할 수 없다.</item>
/// <item>이번 부팅 전에 비정상 종료(이벤트 41·6008)가 있었으면 적용하지 않고 작업을 끈다. 전압이 너무 낮아 블루스크린이 났을 때
///   재부팅할 때마다 다시 같은 값이 걸리는 것을 막는다.</item>
/// </list>
/// </summary>
public sealed class ScheduledTaskUndervoltStartup : IUndervoltStartup
{
    public const string TaskName = "WinCleaner Undervolt";

    public static string StatusPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinCleaner", "undervolt-status.txt");

    public static string PowerShellPath { get; } = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public async Task<UndervoltStartupState> ReadAsync(CancellationToken ct)
    {
        var (exitCode, output) = await ConsoleProcess.RunAsync("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML", null, ct);
        var lastRun = ReadStatus();
        return exitCode != 0 ? new UndervoltStartupState(false, false, null, lastRun) : ParseTaskXml(output, lastRun);
    }

    public async Task RegisterAsync(int millivolts, CancellationToken ct)
    {
        var xml = BuildTaskXml(WindowsIdentity.GetCurrent().Name, millivolts);
        var path = Path.Combine(Path.GetTempPath(), $"wincleaner-undervolt-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(path, xml, Encoding.Unicode, ct);
            var (exitCode, output) = await ConsoleProcess.RunAsync("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{path}\" /F", null, ct);
            if (exitCode != 0)
                throw new InvalidOperationException($"자동 적용 작업을 만들지 못했습니다: {output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    public async Task UnregisterAsync(CancellationToken ct)
    {
        var (exitCode, output) = await ConsoleProcess.RunAsync("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", null, ct);
        // 이미 없으면 성공으로 본다.
        if (exitCode != 0 && (await ReadAsync(ct)).IsRegistered)
            throw new InvalidOperationException($"자동 적용 작업을 지우지 못했습니다: {output}");
    }

    public static UndervoltStartupState ParseTaskXml(string xml, string? lastRun)
    {
        var offset = Regex.Match(xml, @"offset=(-?\d+)mV");
        var settings = Regex.Match(xml, @"<Settings>.*?</Settings>", RegexOptions.Singleline).Value;
        var enabled = !Regex.IsMatch(settings, @"<Enabled>\s*false\s*</Enabled>", RegexOptions.IgnoreCase);
        return new UndervoltStartupState(true, enabled,
            offset.Success ? int.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture) : null, lastRun);
    }

    /// <summary>작업 스케줄러 XML. 로그인 30초 뒤, 그리고 절전에서 깨어날 때 실행한다.</summary>
    public static string BuildTaskXml(string userId, int millivolts)
    {
        XtuUndervolt.Validate(millivolts);
        var user = SecurityElement.Escape(userId);
        // 작업 스케줄러는 PATH에서 powershell.exe를 찾지 못해 0x80070002(파일 없음)로 실패했다 (2026-11-07). 전체 경로를 쓴다.
        var powershell = SecurityElement.Escape(PowerShellPath);
        var arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {XtuUndervolt.EncodeCommand(BuildStartupScript(millivolts))}";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>WinCleaner</Author>
                <Description>WinCleaner: 로그인할 때와 절전에서 깨어날 때 CPU 전압 오프셋(offset={millivolts}mV)을 다시 적용합니다. 비정상 종료가 있었으면 적용하지 않고 스스로 꺼집니다.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT30S</Delay>
                </LogonTrigger>
                <EventTrigger>
                  <Enabled>true</Enabled>
                  <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
                  <Delay>PT10S</Delay>
                </EventTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{powershell}</Command>
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>작업이 실행하는 스크립트: 비정상 종료 확인 → 적용 → 상태 파일 기록.</summary>
    public static string BuildStartupScript(int millivolts)
    {
        XtuUndervolt.Validate(millivolts);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $offset = {{millivolts.ToString(CultureInfo.InvariantCulture)}}
            $status = '{{StatusPath}}'
            function Save($text) {
                New-Item -ItemType Directory -Force -Path (Split-Path $status) | Out-Null
                ('{0} {1}' -f (Get-Date -Format 's'), $text) | Set-Content -Path $status -Encoding UTF8
            }
            try {
                # 이번 부팅 직전에 블루스크린·강제 종료가 있었으면 전압이 너무 낮았을 수 있다. 적용하지 않고 작업을 끈다.
                $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
                $crash = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 41, 6008; StartTime = $boot } -MaxEvents 1 -ErrorAction SilentlyContinue
                if ($crash) {
                    Disable-ScheduledTask -TaskName '{{TaskName}}' | Out-Null
                    Save 'skipped-crash'
                    return
                }
                $result = & {
            {{XtuUndervolt.ToolBody}}
                }
                if ("$result" -notmatch 'RESULT') { throw "결과 없음: $result" }
                Save "applied $("$result" -replace '^.*RESULT ', '')"
            } catch {
                Save ('error ' + $_.Exception.Message)
            }
            """;
    }

    /// <summary>상태 파일 한 줄. 예: <c>2026-11-08T09:01:00 applied core=-125 cache=-125</c>.</summary>
    private static string? ReadStatus()
    {
        try
        {
            return File.Exists(StatusPath) ? File.ReadAllText(StatusPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
