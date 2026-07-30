using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cdmw.ArchiveLite.App.Infrastructure;

/// <summary>
/// Keeps a maximized borderless window inside the monitor's work area.
/// </summary>
/// <remarks>
/// A window with <see cref="WindowStyle.None"/> is maximized by Windows to the full monitor
/// rectangle rather than to the area the taskbar leaves free, so its bottom edge - here the status
/// bar and the paging row above it - ends up underneath the taskbar. Answering WM_GETMINMAXINFO with
/// the work area of the monitor the window is on restores the normal behaviour, and doing it per
/// monitor rather than from the primary one keeps it right on a multi-monitor desktop.
/// </remarks>
public static class MaximizedWindowBounds
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.RemoveHook(OnWindowMessage);
            source.AddHook(OnWindowMessage);
        }
    }

    private static IntPtr OnWindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // The position is expressed relative to the monitor rather than the desktop, so a monitor
        // sitting left of or above the primary one needs the difference rather than the raw origin.
        info.MaximizedPosition = new NativePoint
        {
            X = monitorInfo.Work.Left - monitorInfo.Monitor.Left,
            Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top,
        };
        info.MaximizedSize = new NativePoint
        {
            X = monitorInfo.Work.Right - monitorInfo.Work.Left,
            Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top,
        };
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaximizedSize;
        public NativePoint MaximizedPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }
}
