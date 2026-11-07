using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WinCleaner.App.Services;

/// <summary>
/// 어느 창이 앞에 있어도(게임 중에도) 동작하는 단축키. 보이지 않는 메시지 전용 창으로 WM_HOTKEY를 받는다.
/// 다른 프로그램이 같은 조합을 먼저 쓰고 있으면 등록에 실패한다(<see cref="IsRegistered"/>).
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint KeyF10 = 0x79;

    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5743;

    private readonly HwndSource _source;
    private readonly Action _onPressed;

    public GlobalHotkey(uint modifiers, uint key, Action onPressed)
    {
        _onPressed = onPressed;
        // 부모를 HWND_MESSAGE(-3)로 두면 화면에 나타나지 않는 메시지 전용 창이 된다.
        _source = new HwndSource(new HwndSourceParameters("WinCleaner hotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _source.AddHook(Hook);
        IsRegistered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | ModNoRepeat, key);
    }

    public bool IsRegistered { get; }

    public void Dispose()
    {
        if (IsRegistered)
            UnregisterHotKey(_source.Handle, HotkeyId);
        _source.Dispose();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _onPressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
