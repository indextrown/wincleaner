using System.Runtime.InteropServices;

namespace WinCleaner.Memory;

/// <summary>원클릭 정리에서 건드리면 안 되는 "지금 쓰는" 프로그램을 알려 준다. 테스트에서 실제 창을 보지 않도록 분리했다.</summary>
public interface IForegroundApps
{
    /// <summary>정리하지 않을 프로세스 ID: 전체 화면 프로그램(게임 등), WinCleaner로 오기 직전에 쓰던 프로그램, WinCleaner 자신.</summary>
    IReadOnlySet<int> ProcessesToKeep();
}

/// <summary>
/// 게임을 정리 대상에서 빼기 위해 두 가지를 본다.
/// 1) 모니터 전체를 덮는 창의 프로그램(전체 화면·테두리 없는 창 모드 게임).
/// 2) WinCleaner로 오기 직전에 앞에 있던 프로그램(창 모드 게임). 사용자가 게임 중에 Alt+Tab으로 와서 누를 때를 위한 것이다.
/// 2)를 알려면 앞 창이 바뀔 때마다 기록해야 하므로, UI 스레드에서 <see cref="Start"/>를 불러 둔다.
/// </summary>
public sealed partial class ForegroundApps : IForegroundApps, IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint MonitorDefaultToNearest = 0x0002;

    // 작업 표시줄, 바탕 화면, 시작 메뉴, Alt+Tab 창은 "쓰던 프로그램"이 아니다. 바탕 화면은 모니터 전체를 덮기도 한다.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
        "MultitaskingViewFrame", "TaskSwitcherWnd", "XamlExplorerHostIslandWindow", "ForegroundStaging",
    };

    private readonly int _self = Environment.ProcessId;
    private WinEventProc? _onForeground;
    private nint _hook;
    private int _lastOther;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumWindowsProc(nint window, nint parameter);

    /// <summary>앞 창이 바뀌는 것을 기록하기 시작한다. 메시지 루프가 있는 UI 스레드에서 불러야 한다.</summary>
    public void Start()
    {
        if (_hook != 0)
            return;

        // 처음에는 지금 앞에 있는 창을 기록해 둔다.
        Remember(GetForegroundWindow());
        _onForeground = (_, _, window, _, _, _, _) => Remember(window);
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0,
            Marshal.GetFunctionPointerForDelegate(_onForeground), 0, 0, WinEventSkipOwnProcess);
    }

    public IReadOnlySet<int> ProcessesToKeep()
    {
        var keep = new HashSet<int> { _self };
        if (Volatile.Read(ref _lastOther) is var last and > 0)
            keep.Add(last);
        keep.UnionWith(FullScreenProcesses());
        return keep;
    }

    /// <summary>모니터 전체를 덮는 보이는 창을 가진 프로세스.</summary>
    public static IReadOnlySet<int> FullScreenProcesses()
    {
        var result = new HashSet<int>();
        EnumWindowsProc callback = (window, _) =>
        {
            if (IsFullScreen(window) && GetWindowThreadProcessId(window, out var processId) != 0)
                result.Add(processId);
            return 1;
        };

        EnumWindows(Marshal.GetFunctionPointerForDelegate(callback), 0);
        GC.KeepAlive(callback);
        return result;
    }

    public void Dispose()
    {
        if (_hook != 0)
            UnhookWinEvent(_hook);
        _hook = 0;
        _onForeground = null;
    }

    private void Remember(nint window)
    {
        if (window == 0 || IsShell(window) || GetWindowThreadProcessId(window, out var processId) == 0)
            return;
        if (processId != _self)
            Volatile.Write(ref _lastOther, processId);
    }

    private static bool IsFullScreen(nint window)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || IsShell(window) || !GetWindowRect(window, out var rect))
            return false;

        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref info))
            return false;

        var monitor = info.Monitor;
        return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
    }

    private static bool IsShell(nint window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 && ShellClasses.Contains(new string(buffer, 0, length));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, nint callback,
        uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(nint callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint window, [Out] char[] buffer, int maxCount);
}
