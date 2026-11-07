using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinCleaner.Optimizer.Cpu;

/// <summary>CPU 코어·캐시 전압 오프셋(mV). 0이면 공장 설정, 음수면 그만큼 낮춘 것.</summary>
public sealed record UndervoltState(int CoreMillivolts, int CacheMillivolts)
{
    public bool IsDefault => CoreMillivolts == 0 && CacheMillivolts == 0;
}

/// <summary>CPU 전압 낮추기(언더볼팅). 테스트에서 실제 하드웨어를 건드리지 않도록 분리했다.</summary>
public interface IUndervolt
{
    /// <summary>이 PC에 인텔 튜닝 서비스와 라이브러리가 있는지.</summary>
    bool IsAvailable { get; }

    Task<UndervoltState> ReadAsync(CancellationToken ct);

    /// <summary>코어와 캐시를 같은 값으로 낮춘다. 바로 적용되고, 재부팅하면 0으로 돌아간다.</summary>
    Task<UndervoltState> ApplyAsync(int millivolts, CancellationToken ct);
}

/// <summary>
/// Intel XTU 서비스로 전압 오프셋을 읽고 바꾼다 (사양서 3.3 <c>cpu.undervolt</c>).
/// Control Center 2.0이 함께 설치한 IntelOverclockingSDK.dll은 .NET Framework 2.0 어셈블리라
/// 이 앱(.NET 10)에서 직접 불러올 수 없어, Windows PowerShell 5.1을 거쳐 부른다.
/// 코어(컨트롤 34)와 캐시(79)는 8세대 인텔 CPU에서 같은 값이어야 효과가 있어 늘 함께 바꾼다.
/// </summary>
public sealed class XtuUndervolt : IUndervolt
{
    public const int MinMillivolts = -150;
    public const int MaxMillivolts = 0;

    public static string SdkFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Hotkey");

    public static string ServicePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "Intel(R) Extreme Tuning Utility", "XtuService.exe");

    public bool IsAvailable => File.Exists(Path.Combine(SdkFolder, "IntelOverclockingSDK.dll")) && File.Exists(ServicePath);

    public Task<UndervoltState> ReadAsync(CancellationToken ct) => RunAsync(null, ct);

    public Task<UndervoltState> ApplyAsync(int millivolts, CancellationToken ct)
    {
        Validate(millivolts);
        return RunAsync(millivolts, ct);
    }

    public static void Validate(int millivolts)
    {
        if (millivolts is < MinMillivolts or > MaxMillivolts)
            throw new ArgumentOutOfRangeException(nameof(millivolts), $"전압 오프셋은 {MinMillivolts}~{MaxMillivolts}mV 사이여야 합니다.");
    }

    /// <summary>값을 읽고(<paramref name="millivolts"/>가 있으면 먼저 바꾸고) <c>RESULT core=.. cache=..</c>를 출력하는 PowerShell 스크립트.</summary>
    public static string BuildToolScript(int? millivolts)
    {
        if (millivolts is { } value)
            Validate(value);
        var offset = millivolts?.ToString(CultureInfo.InvariantCulture) ?? "$null";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $offset = {{offset}}
            try {
            {{ToolBody}}
            } catch {
                'ERROR ' + $_.Exception.Message
            }
            """;
    }

    /// <summary>스크립트 출력에서 결과를 읽는다.</summary>
    public static UndervoltState ParseResult(string output)
    {
        var match = Regex.Match(output, @"RESULT core=(-?\d+) cache=(-?\d+)");
        if (match.Success)
            return new UndervoltState(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));

        var error = Regex.Match(output, @"ERROR (.+)");
        throw new InvalidOperationException(error.Success
            ? $"인텔 튜닝 서비스 호출에 실패했습니다: {error.Groups[1].Value.Trim()}"
            : $"인텔 튜닝 서비스에서 결과를 받지 못했습니다. {output}".Trim());
    }

    public static string EncodeCommand(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// 인텔 SDK를 불러와 값을 바꾸고 읽는 부분. <c>$offset</c>이 null이면 읽기만 한다.
    /// 캐시 값은 GetTuningControlByID가 0을 돌려주는 경우가 있어(2026-11-07 확인) GetControl로 읽는다.
    /// </summary>
    internal static string ToolBody => $$"""
            Set-Location '{{SdkFolder}}'
            [void][Reflection.Assembly]::LoadFrom('{{Path.Combine(SdkFolder, "IntelOverclockingSDK.dll")}}')
            $tuning = [Intel.Overclocking.SDK.Tuning.TuningLibrary]::Instance
            $tuning.Initialize()
            if ($null -ne $offset) {
                if ($offset -gt {{MaxMillivolts}} -or $offset -lt {{MinMillivolts}}) { throw "범위를 벗어난 값: $offset" }
                $proposals = New-Object 'System.Collections.Generic.List[Intel.Overclocking.SDK.Tuning.ClientTuningProposal]'
                foreach ($id in 34, 79) {
                    $proposal = New-Object Intel.Overclocking.SDK.Tuning.ClientTuningProposal
                    $proposal.Id = [uint32]$id
                    $proposal.Value = [decimal]$offset
                    $proposals.Add($proposal)
                }
                $reboot = $false
                if (-not $tuning.Tune($proposals, [ref]$reboot)) { throw '인텔 튜닝 서비스가 값을 받아들이지 않았습니다.' }
                Start-Sleep -Milliseconds 500
            }
            'RESULT core={0} cache={1}' -f [int]$tuning.GetControl([uint32]34).ActiveValue, [int]$tuning.GetControl([uint32]79).ActiveValue
        """;

    private static async Task<UndervoltState> RunAsync(int? millivolts, CancellationToken ct)
    {
        var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {EncodeCommand(BuildToolScript(millivolts))}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var (_, output) = await ConsoleProcess.RunAsync(@"WindowsPowerShell\v1.0\powershell.exe", arguments, null, timeout.Token);
        var state = ParseResult(output);
        if (millivolts is { } expected && (state.CoreMillivolts != expected || state.CacheMillivolts != expected))
            throw new InvalidOperationException($"적용 후 다시 읽은 값이 다릅니다 (코어 {state.CoreMillivolts}mV, 캐시 {state.CacheMillivolts}mV).");
        return state;
    }
}
