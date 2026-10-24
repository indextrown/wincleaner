using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinCleaner.Optimizer;

/// <summary>powercfg·defrag 같은 Windows 콘솔 프로그램을 창 없이 실행한다.</summary>
internal static partial class ConsoleProcess
{
    static ConsoleProcess()
    {
        // 콘솔 프로그램은 OEM 코드 페이지(한국어 Windows는 949)로 출력한다.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>System32의 프로그램을 실행하고 출력 줄을 넘겨준다. 취소되면 프로세스를 끝낸다.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string program, string arguments, Action<string>? onLine, CancellationToken ct)
    {
        var encoding = Encoding.GetEncoding((int)GetOEMCP());
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, program), arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{program}을(를) 실행하지 못했습니다.");
        await using var registration = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        var output = new StringBuilder();
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // ReadLine은 진행률 표시에 쓰는 \r도 줄 끝으로 본다.
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            output.AppendLine(line);
            onLine?.Invoke(line.Trim());
        }

        output.Append(await errors);
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (process.ExitCode, output.ToString().Trim());
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetOEMCP();
}
