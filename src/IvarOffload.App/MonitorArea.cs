using System.Runtime.InteropServices;
using System.Windows;

namespace IvarOffload.App;

/// <summary>
/// The work area (the screen without the taskbar) of the monitor under the mouse pointer, where a new window should
/// open. WPF's SystemParameters.WorkArea is always the primary monitor, and WindowStartupLocation.CenterScreen lays a
/// window out at the primary monitor's scaling even when it puts it on another one: on a 4K primary at 150 % and a
/// 1080p second screen at 100 %, the window opened half again too tall there, its title bar off-screen.
/// </summary>
internal readonly partial struct MonitorArea
{
    /// <summary>The work area in physical pixels.</summary>
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>The monitor's scaling: 1.0 at 100 %, 1.5 at 150 %.</summary>
    public double Scale { get; init; }

    /// <summary>The work area's size in WPF units at the monitor's own scaling.</summary>
    public Size SizeInUnits => new(Width / Scale, Height / Scale);

    /// <summary>The monitor under the mouse pointer; null when it can't be read (the caller keeps WPF's defaults).</summary>
    public static MonitorArea? UnderPointer()
    {
        if (!GetCursorPos(out var point)) return null;
        nint monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info) || GetDpiForMonitor(monitor, 0, out uint dpi, out _) != 0 || dpi == 0)
            return null;
        var work = info.Work;
        return new MonitorArea
        {
            Left = work.Left, Top = work.Top, Width = work.Right - work.Left, Height = work.Bottom - work.Top, Scale = dpi / 96.0,
        };
    }

    /// <summary>The scaling WPF uses for window positions before the window exists (the primary monitor's at start).</summary>
    public static double SystemScale()
    {
        uint dpi = GetDpiForSystem();
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    /// <summary>dpiType 0 = the effective DPI (the monitor's scaling setting).</summary>
    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
