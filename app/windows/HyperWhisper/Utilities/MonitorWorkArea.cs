// WORK AREAS AND POSITIONS UNDER PER-MONITOR DPI (issue #1501)
//
// The app is Per-Monitor V2 DPI aware (app.manifest). Each window then has the
// DPI of the monitor it is on, and WPF measures that window's Left, Top, Width
// and Height in DIP at THAT DPI: physical pixels / the window's own scale. There
// is no single DIP coordinate system any more.
//
// SystemParameters.WorkArea does not follow that. WPF converts the primary work
// area with the system DPI the process started at, so after a live scale change
// (100% -> 150%, no sign-out) or for a window on a monitor with another scale it
// is in the wrong unit, and a toast placed with it lands off screen.
//
// Everything here works from physical pixels instead: the monitor's work area
// from GetMonitorInfo, divided by that monitor's own DPI (GetDpiForMonitor), and a
// position computed in that monitor's DIP is rebased into the window's DIP before
// it is set. The conversions are pure functions so the smoke suite can pin them
// without a display.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HyperWhisper.Services;
using Point = System.Windows.Point;

namespace HyperWhisper.Utilities;

internal static class MonitorWorkArea
{
    /// <summary>A DPI scale factor per axis: 1.0 is 96 DPI, 1.5 is 144 DPI.</summary>
    internal readonly record struct Scale(double X, double Y)
    {
        internal static readonly Scale Identity = new(1.0, 1.0);

        internal static Scale FromDpi(double dpiX, double dpiY) => new(Sanitize(dpiX / 96.0), Sanitize(dpiY / 96.0));

        internal Scale Sanitized() => new(Sanitize(X), Sanitize(Y));

        private static double Sanitize(double s) => s > 0 && !double.IsInfinity(s) ? s : 1.0;
    }

    /// <summary>A monitor: its work area in physical pixels, and its DPI scale.</summary>
    internal readonly record struct MonitorArea(Rect WorkAreaPixels, Scale Scale)
    {
        /// <summary>The work area in this monitor's own DIP.</summary>
        internal Rect WorkAreaDip => ToDip(WorkAreaPixels, Scale);
    }

    // ------------------------------------------------------------------
    // Pure conversions
    // ------------------------------------------------------------------

    /// <summary>A rectangle in physical pixels, in DIP at <paramref name="scale"/>.</summary>
    internal static Rect ToDip(Rect pixels, Scale scale)
    {
        var s = scale.Sanitized();
        return new Rect(pixels.Left / s.X, pixels.Top / s.Y, pixels.Width / s.X, pixels.Height / s.Y);
    }

    /// <summary>
    /// A point in DIP at <paramref name="from"/>, in DIP at <paramref name="to"/>:
    /// the same physical pixel. A position worked out in the target monitor's DIP
    /// becomes the Left/Top of a window whose own DPI is still another monitor's.
    /// </summary>
    internal static Point Rebase(Point dip, Scale from, Scale to)
    {
        var f = from.Sanitized();
        var t = to.Sanitized();
        return new Point(dip.X * f.X / t.X, dip.Y * f.Y / t.Y);
    }

    // ------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------

    /// <summary>The DPI scale WPF uses for <paramref name="window"/>'s Left/Top/Width/Height.</summary>
    internal static Scale WindowScale(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Scale(dpi.DpiScaleX, dpi.DpiScaleY).Sanitized();
    }

    /// <summary>The primary monitor, or null if Windows would not say.</summary>
    internal static MonitorArea? Primary() =>
        FromMonitor(MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY));

    /// <summary>The monitor <paramref name="window"/> is on (nearest), or null before it has a handle.</summary>
    internal static MonitorArea? OfWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd == IntPtr.Zero ? null : FromMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
    }

    /// <summary>
    /// The work area of the monitor <paramref name="window"/> is on, in the
    /// window's own DIP (the unit of its Left and Width). Before the window has a
    /// handle: the primary monitor's work area in its own DIP.
    /// </summary>
    internal static Rect WorkAreaDip(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return Primary()?.WorkAreaDip ?? SystemParameters.WorkArea;

        var monitor = FromMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        if (monitor is null) return SystemParameters.WorkArea;

        // The window's scale, not the monitor's: they differ for the moment a
        // window straddles two monitors, and Left/Top are in the window's.
        return ToDip(monitor.Value.WorkAreaPixels, WindowScale(window));
    }

    // ------------------------------------------------------------------
    // Placing a window
    // ------------------------------------------------------------------

    /// <summary>
    /// Moves <paramref name="window"/> to the top-left corner <paramref name="place"/>
    /// returns. <paramref name="place"/> gets the target monitor's work area in that
    /// monitor's DIP, and answers in the same DIP; the window's Width and Height are
    /// DIP too and are the same at any scale, so a placement written against
    /// <c>SystemParameters.WorkArea</c> keeps its arithmetic.
    ///
    /// The window gets a handle first, so its DPI is known. If the move takes it
    /// onto a monitor with another DPI, WPF rescales it during the move
    /// (WM_DPICHANGED) and may shift it; the placement then runs again at the new
    /// DPI, which lands it exactly.
    /// </summary>
    internal static void Place(Window window, Func<MonitorArea?> target, Func<Rect, Point> place)
    {
        try
        {
            new WindowInteropHelper(window).EnsureHandle();

            for (var pass = 0; pass < 3; pass++)
            {
                var monitor = target();
                if (monitor is null)
                {
                    // Windows would not describe a monitor: the old behaviour.
                    var p = place(SystemParameters.WorkArea);
                    window.Left = p.X;
                    window.Top = p.Y;
                    return;
                }

                var before = WindowScale(window);
                var at = Rebase(place(monitor.Value.WorkAreaDip), monitor.Value.Scale, before);
                // A window never placed yet has NaN Left/Top, and NaN compares false.
                if (!(Math.Abs(window.Left - at.X) <= 0.5)) window.Left = at.X;
                if (!(Math.Abs(window.Top - at.Y) <= 0.5)) window.Top = at.Y;

                if (WindowScale(window) == before) return;
            }
        }
        catch (Exception ex)
        {
            // A display query must never stop a toast or an overlay from showing.
            LoggingService.Debug($"MonitorWorkArea: could not place {window.GetType().Name}: {ex.Message}");
        }
    }

    private static MonitorArea? FromMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        var work = new Rect(info.rcWork.Left, info.rcWork.Top,
            Math.Max(0, info.rcWork.Right - info.rcWork.Left), Math.Max(0, info.rcWork.Bottom - info.rcWork.Top));
        if (work.Width <= 0 || work.Height <= 0) return null;

        // MDT_EFFECTIVE_DPI: the scale the user picked for that monitor, which is
        // the DPI a per-monitor aware window on it is given.
        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0
            ? Scale.FromDpi(dpiX, dpiY)
            : Scale.Identity;
        return new MonitorArea(work, scale);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
