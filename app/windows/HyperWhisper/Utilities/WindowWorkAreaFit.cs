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
// It is applied when the window opens, again on a display change, and on a
// restore from minimised if a display change came while it was minimised. It is
// NOT a resize or maximise feature and it does not scale the UI.
//
// A window the user can resize (the code-built Models and API keys windows)
// keeps the size the user dragged it to on a later refit; only an axis that no
// longer fits the work area is shrunk.
//
// The pages already scroll: every page in the main window's content row sits in
// a ScrollViewer, and the caption and status-bar rows are fixed-size rows outside
// it, so a shorter window gives the slack to the scroller and keeps both bars.
//
// OnboardingWindow has its own copy of this policy (with a floor and a drag
// re-clamp); it predates this helper and is left as it is.

using System.Runtime.InteropServices;
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
    /// The size is the wanted size (the design size, or for a resizable window
    /// the size the user chose, see <see cref="WantedLength"/>), capped to the
    /// work area on each axis. A position that is set is then moved the least
    /// distance that puts the whole window inside the work area; a NaN position
    /// is left for WPF.
    /// </summary>
    internal static Placement Fit(double wantedWidth, double wantedHeight, Rect workArea, double left, double top)
    {
        var width = Math.Min(wantedWidth, workArea.Width);
        var height = Math.Min(wantedHeight, workArea.Height);

        return new Placement(
            double.IsNaN(left) ? double.NaN : Clamp(left, workArea.Left, workArea.Right - width),
            double.IsNaN(top) ? double.NaN : Clamp(top, workArea.Top, workArea.Bottom - height),
            width,
            height);
    }

    /// <summary>
    /// The first placement, made in SourceInitialized from the rectangle WPF's
    /// SetupInitialState has already given the HWND (<paramref name="shownBounds"/>,
    /// in DIP). That rectangle has the design size, and for CenterScreen /
    /// CenterOwner it is centred with the design size, so at 175% its top is
    /// above the work area. The size is capped, a centred window keeps its centre
    /// (so CenterScreen stays centred in the work area), and the result is clamped
    /// into the work area. A Manual window keeps its top-left corner.
    /// </summary>
    internal static Placement FirstPlacement(double designWidth, double designHeight, Rect workArea, Rect shownBounds, bool keepCentre)
    {
        var width = Math.Min(designWidth, workArea.Width);
        var height = Math.Min(designHeight, workArea.Height);
        var left = keepCentre ? shownBounds.Left + (shownBounds.Width - width) / 2 : shownBounds.Left;
        var top = keepCentre ? shownBounds.Top + (shownBounds.Height - height) / 2 : shownBounds.Top;
        return Fit(width, height, workArea, left, top);
    }

    /// <summary>
    /// The length a refit starts from on one axis, before the work-area cap.
    /// A window the user cannot resize always starts from its design length, so
    /// it grows back to the design wherever it fits again. A resizable window
    /// starts from its current length when the user has changed it since the last
    /// fit, so a work-area change never undoes a resize; it is only capped.
    /// </summary>
    internal static double WantedLength(double design, double current, double lastFitted, bool userCanResize)
    {
        if (!userCanResize) return design;
        if (double.IsNaN(current) || current <= 0 || double.IsNaN(lastFitted)) return design;
        return Math.Abs(current - lastFitted) > 0.5 ? current : design;
    }

    /// <summary>
    /// Holds a refit that arrived while the window was not Normal. A minimised
    /// or maximised window's Left/Top are its restore bounds and are not moved
    /// then; the refit runs once, when the window is restored to Normal (the
    /// tray's Show + WindowState = Normal, or a taskbar click).
    /// </summary>
    internal sealed class RestoreRefit
    {
        private bool _pending;

        internal bool IsPending => _pending;

        /// <summary>A refit is due. True when it may run now.</summary>
        internal bool Request(WindowState state)
        {
            if (state == WindowState.Normal) return true;
            _pending = true;
            return false;
        }

        /// <summary>The window changed state. True when a held refit must run now.</summary>
        internal bool StateChanged(WindowState state)
        {
            if (state != WindowState.Normal || !_pending) return false;
            _pending = false;
            return true;
        }
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

        // The size this fitter last gave the window, so a refit can tell a size
        // the user dragged to from one it set itself. NaN until the first fit.
        private double _lastFittedWidth = double.NaN;
        private double _lastFittedHeight = double.NaN;

        // A refit that came while the window was minimised or maximised.
        private readonly RestoreRefit _restoreRefit = new();

        internal Fitter(Window window)
        {
            _window = window;
            _designWidth = window.Width;
            _designHeight = window.Height;
            _designMinWidth = window.MinWidth;
            _designMinHeight = window.MinHeight;

            // WPF's Window.Show runs CreateSourceWindow, which creates the HWND
            // hidden, then SetupInitialState sizes it AND applies
            // WindowStartupLocation with the design Width/Height (one SetWindowPos),
            // then raises SourceInitialized, and only after that does ShowHelper
            // call ShowWindow. So SourceInitialized is too late to change what WPF
            // centres, but early enough to move the HWND before it is visible:
            // PlaceFirst re-places it there from the rectangle WPF chose.
            // Loaded stays as a safety net for anything that moved it since.
            window.SourceInitialized += OnSourceInitialized;
            window.Loaded += (_, _) => Apply();
            window.DpiChanged += (_, _) => Schedule();
            window.StateChanged += OnStateChanged;
            window.Closed += OnClosed;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (!PlaceFirst()) Apply();

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

        private void OnStateChanged(object? sender, EventArgs e)
        {
            if (_restoreRefit.StateChanged(_window.WindowState)) Schedule();
        }

        private bool UserCanResize =>
            _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

        /// <summary>
        /// The first placement, in SourceInitialized, while the HWND is still
        /// hidden. It reads the rectangle SetupInitialState gave the HWND (not
        /// Window.Left/Top, which need not reflect a computed startup location
        /// yet), and the work area of the monitor that HWND is on (the monitor WPF
        /// chose), and moves the HWND once with SetWindowPos so the first frame
        /// shown is already inside the work area. Returns false when it could not
        /// read the HWND, and the caller falls back to <see cref="Apply"/>.
        /// </summary>
        private bool PlaceFirst()
        {
            try
            {
                if (_window.WindowState != WindowState.Normal) return false;
                // SizeToContent windows are re-centred by WPF after the first
                // layout; none of the fitted windows use it.
                if (_window.SizeToContent != SizeToContent.Manual) return false;

                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;

                var (scaleX, scaleY) = DpiScale(_window);
                var workArea = CurrentWorkAreaDip(_window);
                if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0) return false;

                var shown = new Rect(rect.Left / scaleX, rect.Top / scaleY,
                    (rect.Right - rect.Left) / scaleX, (rect.Bottom - rect.Top) / scaleY);
                var place = FirstPlacement(_designWidth, _designHeight, workArea, shown,
                    keepCentre: _window.WindowStartupLocation != WindowStartupLocation.Manual);

                // WPF's own properties first, so its layout and its later
                // SetWindowPos calls agree with the HWND; the floor comes down
                // before Width, or WPF coerces Width back up to it.
                _window.MinWidth = Math.Min(_designMinWidth, workArea.Width);
                _window.MinHeight = Math.Min(_designMinHeight, workArea.Height);
                _window.Width = place.Width;
                _window.Height = place.Height;
                _window.Left = place.Left;
                _window.Top = place.Top;

                // Then the HWND itself, in physical pixels, so it is right before
                // ShowWindow whatever WPF deferred to its first layout pass.
                SetWindowPos(hwnd, IntPtr.Zero,
                    (int)Math.Round(place.Left * scaleX), (int)Math.Round(place.Top * scaleY),
                    (int)Math.Round(place.Width * scaleX), (int)Math.Round(place.Height * scaleY),
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

                _lastFittedWidth = place.Width;
                _lastFittedHeight = place.Height;
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"WindowWorkAreaFit: could not place {_window.GetType().Name} at open: {ex.Message}");
                return false;
            }
        }

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
                // are its restore bounds; neither is ours to move now. The refit
                // is remembered and runs when the window is restored to Normal.
                if (!_restoreRefit.Request(_window.WindowState)) return;

                var workArea = CurrentWorkAreaDip(_window);
                if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0) return;

                var currentWidth = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
                var currentHeight = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
                var resizable = UserCanResize;
                var fit = Fit(
                    WantedLength(_designWidth, currentWidth, _lastFittedWidth, resizable),
                    WantedLength(_designHeight, currentHeight, _lastFittedHeight, resizable),
                    workArea, _window.Left, _window.Top);

                // The floor has to come down first, or WPF coerces Width back up to
                // it. It goes back to the design floor when the window fits again.
                _window.MinWidth = Math.Min(_designMinWidth, workArea.Width);
                _window.MinHeight = Math.Min(_designMinHeight, workArea.Height);

                if (Math.Abs(currentWidth - fit.Width) > 0.5) _window.Width = fit.Width;
                if (Math.Abs(currentHeight - fit.Height) > 0.5) _window.Height = fit.Height;
                _lastFittedWidth = fit.Width;
                _lastFittedHeight = fit.Height;

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
        var (scaleX, scaleY) = DpiScale(window);

        return new Rect(area.Left / scaleX, area.Top / scaleY, area.Width / scaleX, area.Height / scaleY);
    }

    private static (double X, double Y) DpiScale(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0, dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
