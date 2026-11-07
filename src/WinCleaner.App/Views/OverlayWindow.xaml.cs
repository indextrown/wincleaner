using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinCleaner.App.Services;
using WinCleaner.App.ViewModels;

namespace WinCleaner.App.Views;

/// <summary>
/// 화면 구석에 떠 있는 오버레이 창. 클릭이 통과하고, 포커스를 빼앗지 않고, 작업 표시줄과 Alt+Tab에 나오지 않는다.
/// 위치와 여백은 설정을 따르고, 게임이 창을 덮어 올라오는 경우가 있어 보이는 동안 2초마다 맨 위로 다시 올린다.
/// 전체 창 모드 게임은 작업 표시줄까지 덮으므로 작업 영역이 아니라 화면 전체를 기준으로 놓는다.
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

    private readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(2) };

    public OverlayWindow()
    {
        InitializeComponent();
        _keepOnTop.Tick += (_, _) => BringToTop();
        SizeChanged += (_, _) => Place();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Place();
                _keepOnTop.Start();
            }
            else
            {
                _keepOnTop.Stop();
            }
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old)
                old.PropertyChanged -= OnSettingChanged;
            if (e.NewValue is INotifyPropertyChanged now)
                now.PropertyChanged += OnSettingChanged;
        };
    }

    /// <summary>화면 크기(w×h)에서 설정 위치·여백에 맞는 왼쪽 위 좌표.</summary>
    public static Point PlaceOnScreen(OverlayPosition position, double marginX, double marginY, double width, double height,
        double screenWidth, double screenHeight) => position switch
    {
        OverlayPosition.TopLeft => new Point(marginX, marginY),
        OverlayPosition.TopCenter => new Point((screenWidth - width) / 2, marginY),
        OverlayPosition.BottomLeft => new Point(marginX, screenHeight - height - marginY),
        OverlayPosition.BottomRight => new Point(screenWidth - width - marginX, screenHeight - height - marginY),
        _ => new Point(screenWidth - width - marginX, marginY),
    };

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayViewModel.Position) or nameof(OverlayViewModel.MarginX) or nameof(OverlayViewModel.MarginY))
            Place();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate);
    }

    private void Place()
    {
        if (DataContext is not OverlayViewModel settings || !IsVisible)
            return;
        var point = PlaceOnScreen(settings.Position, settings.MarginX, settings.MarginY, ActualWidth, ActualHeight,
            SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        Left = point.X;
        Top = point.Y;
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
