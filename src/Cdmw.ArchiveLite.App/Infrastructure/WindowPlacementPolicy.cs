namespace Cdmw.ArchiveLite.App.Infrastructure;

/// <summary>
/// A rectangle in physical screen pixels, the one coordinate space every monitor agrees on.
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsUsable => Width > 0 && Height > 0;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public long IntersectionArea(PixelRect other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return width <= 0 || height <= 0 ? 0L : (long)width * height;
    }

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>One monitor's full rectangle and the part of it the taskbar leaves free.</summary>
public readonly record struct MonitorArea(PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

/// <summary>
/// Decides where a remembered window should reappear.
/// </summary>
/// <remarks>
/// Saved placement used to be kept in WPF's device-independent units, which belong to whichever
/// monitor the window happened to be on, and was restored before the window had a monitor at all --
/// so the units were reinterpreted against the primary display's scale. Between a 100% and a 150%
/// monitor that inflated the window by half its width, which is how a remembered 1920-wide window
/// came back 2880 wide and hung off both edges of the screen.
///
/// Physical pixels are the fix: they mean the same thing on every display, so the only work left is
/// making sure the remembered rectangle still lands somewhere the user can reach. That has to hold
/// when a monitor has been unplugged, rearranged, rescaled or set to a different resolution since
/// the window was last closed, so this is kept free of Win32 and of WPF and is decided purely from
/// the rectangles.
/// </remarks>
public static class WindowPlacementPolicy
{
    public static PixelRect Resolve(PixelRect saved, IReadOnlyList<MonitorArea> monitors)
    {
        if (monitors is null || monitors.Count == 0)
        {
            // Nothing to validate against; the caller's own defaults are better than a guess.
            return saved;
        }
        if (!saved.IsUsable)
        {
            return CenterOn(saved, Primary(monitors).WorkArea);
        }

        var visible = MostOverlapped(saved, monitors);
        if (visible is null)
        {
            // The display it was left on is gone or has moved out from under it. Recovering to a
            // corner of the primary would be technically on-screen but reads as a glitch; centring
            // is what the user would have got on a first run.
            return CenterOn(saved, Primary(monitors).WorkArea);
        }

        return FitWithin(saved, visible.Value.WorkArea);
    }

    /// <summary>Shrinks and shifts a rectangle until it sits wholly inside a work area.</summary>
    public static PixelRect FitWithin(PixelRect rectangle, PixelRect workArea)
    {
        // A window remembered from a larger or higher-resolution display has to give up size before
        // position, or clamping the origin would only push it off the opposite edge.
        var width = Math.Max(1, Math.Min(rectangle.Width, workArea.Width));
        var height = Math.Max(1, Math.Min(rectangle.Height, workArea.Height));
        var left = Math.Clamp(rectangle.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var top = Math.Clamp(rectangle.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new PixelRect(left, top, width, height);
    }

    private static PixelRect CenterOn(PixelRect rectangle, PixelRect workArea)
    {
        var width = Math.Max(1, Math.Min(rectangle.IsUsable ? rectangle.Width : workArea.Width, workArea.Width));
        var height = Math.Max(1, Math.Min(rectangle.IsUsable ? rectangle.Height : workArea.Height, workArea.Height));
        return new PixelRect(
            workArea.Left + ((workArea.Width - width) / 2),
            workArea.Top + ((workArea.Height - height) / 2),
            width,
            height);
    }

    private static MonitorArea? MostOverlapped(PixelRect saved, IReadOnlyList<MonitorArea> monitors)
    {
        MonitorArea? best = null;
        var bestArea = 0L;
        foreach (var monitor in monitors)
        {
            var area = saved.IntersectionArea(monitor.WorkArea);
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }
        if (best is not null)
        {
            return best;
        }

        // A window can overlap no work area and still be somewhere sensible -- parked under a
        // taskbar, or on a monitor whose work area the title bar alone misses.
        var centerX = saved.Left + (saved.Width / 2);
        var centerY = saved.Top + (saved.Height / 2);
        foreach (var monitor in monitors)
        {
            if (monitor.Bounds.Contains(centerX, centerY))
            {
                return monitor;
            }
        }
        return null;
    }

    private static MonitorArea Primary(IReadOnlyList<MonitorArea> monitors)
    {
        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }
        return monitors[0];
    }
}
