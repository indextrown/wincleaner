using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinCleaner.App.Views;

/// <summary>
/// 화면 오른쪽 위에 떠 있는 오버레이 창. 클릭이 통과하고, 포커스를 빼앗지 않고, 작업 표시줄과 Alt+Tab에 나오지 않는다.
/// 게임이 창을 덮어 올라오는 경우가 있어 보이는 동안 2초마다 맨 위로 다시 올린다.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private const double ScreenMargin = 16;

    private readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(2) };

    public OverlayWindow()
    {
        InitializeComponent();
        _keepOnTop.Tick += (_, _) => BringToTop();
        SizeChanged += (_, _) => PlaceTopRight();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                PlaceTopRight();
                _keepOnTop.Start();
            }
            else
            {
                _keepOnTop.Stop();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate);
    }

    private void PlaceTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - ScreenMargin;
        Top = area.Top + ScreenMargin;
    }

    private void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
