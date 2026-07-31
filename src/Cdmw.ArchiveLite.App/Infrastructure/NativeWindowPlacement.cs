using System.Runtime.InteropServices;

namespace Cdmw.ArchiveLite.App.Infrastructure;

/// <summary>
/// Reads and writes a window's restored rectangle in physical pixels.
/// </summary>
/// <remarks>
/// WPF's own <c>Left</c>, <c>Top</c>, <c>Width</c> and <c>Height</c> are device-independent units
/// belonging to the display the window is on, so they cannot be compared across a mixed-DPI desktop
/// or stored and reused on another one. Windows keeps the same figures in physical pixels, which
/// mean the same thing everywhere, and hands back the restored rectangle even while the window is
/// maximized -- exactly what has to be remembered.
/// </remarks>
internal static class NativeWindowPlacement
{
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;
    private const int ShowMinimized = 2;

    public static (PixelRect Restored, bool IsMaximized)? Capture(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(window, ref placement))
        {
            return null;
        }
        // A minimized window remembers what it was before it was minimized, which is what should be
        // restored -- reopening minimized would leave the user with no window at all.
        var isMaximized = placement.ShowCommand == ShowMaximized
            || (placement.ShowCommand == ShowMinimized && (placement.Flags & WindowPlacementRestoreToMaximized) != 0);
        return (WorkspaceToScreen(ToRect(placement.NormalPosition)), isMaximized);
    }

    /// <summary>
    /// Puts a window back at a remembered rectangle, and makes the rectangle stick when the move
    /// crosses displays of different scale.
    /// </summary>
    /// <remarks>
    /// Moving a window onto a monitor at another scale makes Windows deliver WM_DPICHANGED, and it
    /// does so from inside the call that moves it -- pumping the queue afterwards is too late,
    /// because the resize has already happened. The suggested rectangle that arrives with it is the
    /// old one scaled by the ratio between the two displays, which is right when the user drags a
    /// window across the boundary and wrong here: the remembered rectangle is already expressed for
    /// the display it is going to. So the size that was just asked for comes back divided by the
    /// scale difference -- 1920 wide onto a 100% monitor from a 150% one becomes 1280.
    ///
    /// Asking a second time settles it, but only once the window has finished changing DPI: an
    /// immediate retry is rescaled exactly as the first attempt was. The caller re-asserts from the
    /// message loop, where the change has been delivered and the window already belongs to the
    /// target display, so the rectangle is then taken as given. See <see cref="Matches"/>.
    /// </remarks>
    public static bool Apply(IntPtr window, PixelRect restored, bool isMaximized)
    {
        if (window == IntPtr.Zero || !restored.IsUsable)
        {
            return false;
        }
        return Place(window, restored, isMaximized);
    }

    /// <summary>Whether a window currently occupies exactly this rectangle.</summary>
    public static bool Matches(IntPtr window, PixelRect expected) =>
        window != IntPtr.Zero
        && GetWindowRect(window, out var actual)
        && ToRect(actual) == expected;

    private static bool Place(IntPtr window, PixelRect restored, bool isMaximized)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(window, ref placement))
        {
            return false;
        }
        placement.NormalPosition = FromRect(ScreenToWorkspace(restored));
        placement.ShowCommand = isMaximized ? ShowMaximized : ShowNormal;
        return SetWindowPlacement(window, ref placement);
    }

    public static IReadOnlyList<MonitorArea> Monitors()
    {
        var monitors = new List<MonitorArea>(2);
        bool Collect(IntPtr monitor, IntPtr context, ref NativeRect rectangle, IntPtr data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                monitors.Add(new MonitorArea(
                    ToRect(info.Monitor),
                    ToRect(info.Work),
                    (info.Flags & MonitorPrimary) != 0));
            }
            return true;
        }
        return EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero)
            ? monitors
            : Array.Empty<MonitorArea>();
    }

    /// <summary>
    /// WINDOWPLACEMENT states the restored rectangle in workspace coordinates -- screen coordinates
    /// less the primary monitor's work-area origin. The two are the same under the usual bottom
    /// taskbar and differ by its thickness when it sits at the top or the left, which is enough to
    /// walk a window across the desktop over successive runs if it is ignored.
    /// </summary>
    private static PixelRect WorkspaceToScreen(PixelRect rectangle)
    {
        var origin = PrimaryWorkAreaOrigin();
        return rectangle with { Left = rectangle.Left + origin.X, Top = rectangle.Top + origin.Y };
    }

    private static PixelRect ScreenToWorkspace(PixelRect rectangle)
    {
        var origin = PrimaryWorkAreaOrigin();
        return rectangle with { Left = rectangle.Left - origin.X, Top = rectangle.Top - origin.Y };
    }

    private static (int X, int Y) PrimaryWorkAreaOrigin()
    {
        foreach (var monitor in Monitors())
        {
            if (monitor.IsPrimary)
            {
                return (monitor.WorkArea.Left, monitor.WorkArea.Top);
            }
        }
        return (0, 0);
    }

    private static PixelRect ToRect(NativeRect rectangle) =>
        PixelRect.FromEdges(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    private static NativeRect FromRect(PixelRect rectangle) => new()
    {
        Left = rectangle.Left,
        Top = rectangle.Top,
        Right = rectangle.Right,
        Bottom = rectangle.Bottom,
    };

    private const int MonitorPrimary = 0x00000001;
    private const int WindowPlacementRestoreToMaximized = 0x0002;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr context, ref NativeRect rectangle, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

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
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinimizedPosition;
        public NativePoint MaximizedPosition;
        public NativeRect NormalPosition;
    }
}
