using System.Runtime.InteropServices;
using System.Text;

namespace ZellerWpfSpike;

/// <summary>
/// 用 Win32 观察本进程里的顶层窗口，用来看清 SDK 弹出的窗口是什么、在不在最前面、有没有 Owner。
/// </summary>
internal static class NativeWindows
{
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const uint GwOwner = 4;
    private const long WsExTopmost = 0x00000008L;

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static IReadOnlyList<WindowInfo> ListVisibleTopLevel(int processId)
    {
        var result = new List<WindowInfo>();
        EnumWindows(
            (handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var owningProcess);
                if (owningProcess != (uint)processId || !IsWindowVisible(handle))
                {
                    return true;
                }

                var className = new StringBuilder(256);
                GetClassName(handle, className, className.Capacity);
                var title = new StringBuilder(256);
                GetWindowText(handle, title, title.Capacity);
                GetWindowRect(handle, out var rect);
                var topmost = (GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExTopmost) != 0;
                result.Add(new WindowInfo(
                    handle,
                    className.ToString(),
                    title.ToString(),
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top,
                    topmost,
                    GetWindow(handle, GwOwner)));
                return true;
            },
            IntPtr.Zero);
        return result;
    }

    /// <summary>把一个顶层窗口的 Owner 设为指定窗口；有 Owner 的窗口始终显示在 Owner 之上。</summary>
    public static bool TrySetOwner(IntPtr window, IntPtr owner, out int error)
    {
        Marshal.SetLastPInvokeError(0);
        SetWindowLongPtr(window, GwlpHwndParent, owner);
        error = Marshal.GetLastPInvokeError();
        return error == 0;
    }
}

internal sealed record WindowInfo(
    IntPtr Handle,
    string ClassName,
    string Title,
    int Left,
    int Top,
    int Width,
    int Height,
    bool Topmost,
    IntPtr Owner)
{
    public string Describe(IntPtr mainWindow)
    {
        var owner = Owner == IntPtr.Zero ? "无" : Owner == mainWindow ? "主窗口" : $"0x{Owner.ToInt64():X}";
        return $"0x{Handle.ToInt64():X} 类={ClassName} 标题=\"{Title}\" 位置={Left},{Top} 大小={Width}x{Height} 置顶={Topmost} Owner={owner}";
    }
}
