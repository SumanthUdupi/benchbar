using System.Runtime.InteropServices;

namespace BenchBar.Tray;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointI { public int X, Y; }

    internal const int WmHotKey = 0x0312;
    internal const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    internal const uint VkU = 0x55;
    internal const int HwndMessage = -3;
    internal const uint MonitorDefaultToNearest = 2;
    internal const int GwlExStyle = -20;
    internal const int WsExToolWindow = 0x80;
    internal const int WsExNoActivate = 0x08000000;
    internal const uint SwpNoSize = 0x1, SwpNoZOrder = 0x4, SwpNoActivate = 0x10;
    internal const uint MonitorDefaultToPrimary = 1;
    internal const int MdtEffectiveDpi = 0;

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string cls, string? title);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static partial int SetWindowLong(IntPtr hwnd, int index, int value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out PointI point);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromPoint(PointI point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hwnd, int id);
}
