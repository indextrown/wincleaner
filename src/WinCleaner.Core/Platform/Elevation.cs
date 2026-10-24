using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace WinCleaner.Core.Platform;

public static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated { get; } = CheckElevated();

    /// <summary>
    /// 현재 프로그램을 관리자 권한으로 다시 실행한다. 사용자가 UAC 창에서 취소하면 false.
    /// </summary>
    public static bool TryRestartElevated()
    {
        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");

        // 개발용 RunAsInvoker 설정이 새 프로세스로 넘어가면 다시 일반 권한으로 뜬다.
        Environment.SetEnvironmentVariable("__COMPAT_LAYER", null);

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
