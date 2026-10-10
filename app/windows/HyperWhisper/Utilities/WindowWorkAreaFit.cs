// FITTING A FIXED-SIZE WINDOW TO ITS MONITOR'S WORK AREA (issue #1500)
//
// The main window is a fixed 1000 x 680 DIP, and it cannot be resized or
// maximised. On a 1920 x 1080 display at 175% the work area is only about
// 1097 x 569 DIP, so CenterScreen put the window's top at y=-97 physical pixels:
// the caption row with the close button was above the screen and the status bar
// was below the taskbar, and nothing the user could do would bring them back.
//
// The rule (Ray, 2026-10-08): keep the design size wherever it fits; where it
// does not, shrink the window to the work area and let the page content scroll.
// It is applied when the window opens and again on a display change. It is NOT
// a resize or maximise feature and it does not scale the UI.
//
// The pages already scroll: every page in the main window's content row sits in
// a ScrollViewer, and the caption and status-bar rows are fixed-size rows outside
// it, so a shorter window gives the slack to the scroller and keeps both bars.
//
// OnboardingWindow has its own copy of this policy (with a floor and a drag
// re-clamp); it predates this helper and is left as it is.

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using HyperWhisper.Services;

namespace HyperWhisper.Utilities;

internal static class WindowWorkAreaFit
{
    /// <summary>
    /// Where a window goes, in DIP. <see cref="Left"/> and <see cref="Top"/> are
    /// NaN when the window has no position yet and WPF's startup location should
    /// place it.
    /// </summary>
    internal readonly record struct Placement(double Left, double Top, double Width, double Height);

    /// <summary>
    /// The whole sizing policy as one pure function, so the smoke suite can pin
    /// 150% and 175% without a display.
    ///
    /// The size is the design size, capped to the work area on each axis. A
    /// position that is set is then moved the least distance that puts the whole
    /// window inside the work area; a NaN position is left for WPF.
    /// </summary>
    internal static Placement Fit(double designWidth, double designHeight, Rect workArea, double left, double top)
    {
        var width = Math.Min(designWidth, workArea.Width);
        var height = Math.Min(designHeight, workArea.Height);

        return new Placement(
            double.IsNaN(left) ? double.NaN : Clamp(left, workArea.Left, workArea.Right - width),
            double.IsNaN(top) ? double.NaN : Clamp(top, workArea.Top, workArea.Bottom - height),
            width,
            height);
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(value, max));

    /// <summary>
    /// Keeps <paramref name="window"/> inside the work area of the monitor it is
    /// on, from the moment it gets a handle until it closes. Call it once, from the
    /// constructor, after its Width and Height are set.
    /// </summary>
    internal static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (double.IsNaN(window.Width) || double.IsNaN(window.Height))
            throw new ArgumentException("The window needs a design Width and Height to fit.", nameof(window));

        _ = new Fitter(window);
    }

    private sealed class Fitter
    {
        private readonly Window _window;
        private readonly double _designWidth;
        private readonly double _designHeight;
        private readonly double _designMinWidth;
        private readonly double _designMinHeight;
        private bool _subscribed;
        private readonly object _pendingLock = new();
        private bool _pending;

        internal Fitter(Window window)
        {
            _window = window;
            _designWidth = window.Width;
            _designHeight = window.Height;
            _designMinWidth = window.MinWidth;
            _designMinHeight = window.MinHeight;

            // SourceInitialized runs before WPF applies WindowStartupLocation, so
            // CenterScreen / CenterOwner centre the size that is actually shown.
            // Loaded runs after it, and catches a centred window that still hangs
            // over an edge (CenterOwner on an owner near the edge).
            window.SourceInitialized += OnSourceInitialized;
            window.Loaded += (_, _) => Apply();
            window.DpiChanged += (_, _) => Schedule();
            window.Closed += OnClosed;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            Apply();

            // Static events root their handler, so they are taken only once the
            // window is really shown and released when it closes. A window that is
            // built and never shown (the smoke suite builds several) holds nothing.
            //
            // Two sources, because a display change arrives in two shapes:
            //  * DisplaySettingsChanged: resolution, scale, a monitor added or
            //    removed. The app is system-DPI aware (per-monitor awareness is
            //    issue #1501), so a scale change raises no DpiChanged on the window.
            //  * SystemParameters "WorkArea": the taskbar moved, resized or toggled
            //    auto-hide, which changes the work area and nothing else.
            // DpiChanged is wired as well, for when #1501 lands.
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            _subscribed = true;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (!_subscribed) return;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            _subscribed = false;
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Schedule();

        private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                Schedule();
        }

        /// <summary>
        /// One display change raises several of these events, and
        /// DisplaySettingsChanged can arrive off the UI thread. They are coalesced
        /// into one pass on the window's dispatcher, at Background priority so the
        /// system metrics have settled by the time it runs.
        /// </summary>
        private void Schedule()
        {
            var dispatcher = _window.Dispatcher;
            if (dispatcher.HasShutdownStarted) return;

            lock (_pendingLock)
            {
                if (_pending) return;
                _pending = true;
            }

            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                lock (_pendingLock) _pending = false;
                Apply();
            }));
        }

        private void Apply()
        {
            try
            {
                // A maximised window already fits, and Left/Top of a minimised one
                // are its restore bounds; neither is ours to move.
                if (_window.WindowState != WindowState.Normal) return;

                var workArea = CurrentWorkAreaDip(_window);
                if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0) return;

                var fit = Fit(_designWidth, _designHeight, workArea, _window.Left, _window.Top);

                // The floor has to come down first, or WPF coerces Width back up to
                // it. It goes back to the design floor when the window fits again.
                _window.MinWidth = Math.Min(_designMinWidth, fit.Width);
                _window.MinHeight = Math.Min(_designMinHeight, fit.Height);

                if (Math.Abs(_window.Width - fit.Width) > 0.5) _window.Width = fit.Width;
                if (Math.Abs(_window.Height - fit.Height) > 0.5) _window.Height = fit.Height;

                if (!double.IsNaN(fit.Left) && Math.Abs(_window.Left - fit.Left) > 0.5) _window.Left = fit.Left;
                if (!double.IsNaN(fit.Top) && Math.Abs(_window.Top - fit.Top) > 0.5) _window.Top = fit.Top;
            }
            catch (Exception ex)
            {
                // A display query must never stop a window from opening.
                LoggingService.Debug($"WindowWorkAreaFit: could not fit {_window.GetType().Name} to the work area: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The work area of the monitor <paramref name="window"/> is on, in DIP (the
    /// unit of <see cref="Window.Left"/> and <see cref="Window.Width"/>). Falls
    /// back to the primary display before the window has a handle.
    /// </summary>
    private static Rect CurrentWorkAreaDip(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return SystemParameters.WorkArea; // already DIP, primary display only

        // Fully qualified: the project uses WinForms alongside WPF.
        var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea; // physical pixels
        var dpi = VisualTreeHelper.GetDpi(window);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        return new Rect(area.Left / scaleX, area.Top / scaleY, area.Width / scaleX, area.Height / scaleY);
    }
}
