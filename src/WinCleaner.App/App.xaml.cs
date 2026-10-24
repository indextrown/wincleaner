using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinCleaner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 원격 접속용 가상 디스플레이 등 일부 그래픽 환경에서는 하드웨어 렌더링이 실패해 창이 하얗게 나온다.
        // 화면이 단순한 도구라 소프트웨어 렌더링으로도 충분하다.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "WinCleaner 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
