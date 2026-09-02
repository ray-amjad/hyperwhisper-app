using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.ViewModels.Onboarding;
using HyperWhisper.Views.Pages.Onboarding;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// The first run onboarding window. Owns the flow model for the lifetime of the
/// window and drives a Frame from its step. Holds no policy: every branch it
/// makes is a lookup from OnboardingStep to a page instance.
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int DwmSystemBackdropTypeMica = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// The macOS sheet's 760 x 580 plus this window's own 44px caption row. It is the
    /// size every step is laid out against and the LARGEST this window is ever shown
    /// at; see <see cref="ClampToWorkArea"/> for what happens when it does not fit.
    /// </summary>
    private const double DesignWidth = 760;
    private const double DesignHeight = 624;

    /// <summary>
    /// The floor the clamp will not go below, and the same numbers as the XAML's
    /// MinWidth/MinHeight. Below roughly this the footer's four columns start
    /// colliding, and no real Windows work area is this small - a 1024 x 600 netbook
    /// at 200% is still 512 x 260 DIP, which is the worst case that exists.
    /// </summary>
    private const double FloorWidth = 480;
    // The floor is a backstop against an IMPOSSIBLE work area, not a design minimum,
    // so it has to sit below every real one. 1366x768 at 200% - the smallest
    // supported combination - is a 683x348 DIP work area, and a 360 floor would
    // have won there and pushed the window 24 physical pixels off the screen.
    // 44 caption + 56 footer leaves the scrolling stage 220 DIP at this size.
    private const double FloorHeight = 320;

    private readonly OnboardingFlowViewModel _flow;
    private readonly Dictionary<OnboardingStep, Page> _pages = new();

    /// <summary>
    /// True once the flow reached a terminal decision of its own, so Closing knows
    /// not to treat the close as "Set Up Later" a second time.
    /// </summary>
    private bool _flowResolved;

    /// <summary>
    /// The flow model is constructed exactly once, by whoever opens the window, and
    /// lives for the window's lifetime. Mirroring macOS's @autoclosure makeModel: a
    /// page must never build its own.
    /// </summary>
    public OnboardingWindow(OnboardingFlowViewModel flow)
    {
        _flow = flow ?? throw new ArgumentNullException(nameof(flow));

        InitializeComponent();

        DataContext = _flow;
        _flow.PropertyChanged += OnFlowPropertyChanged;

        // A transparent CompositionTarget is what makes the Mica backdrop visible,
        // and pairs with Background="Transparent" on the Window. Same pairing as
        // MainWindow.
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;

            // Before WindowStartupLocation="CenterOwner" runs, so it centres the
            // size we are actually going to show.
            ClampToWorkArea();
            ApplyMacStyleWindowBackdrop();
        };

        // A window dragged onto a smaller monitor, or a scale change while the flow
        // is open, has to be clamped again. NoResize means the user cannot rescue it.
        DpiChanged += (_, _) => ClampToWorkArea();
        LocationChanged += (_, _) => ClampToWorkArea();

        Loaded += (_, _) =>
        {
            ThemeService.Instance.ThemeChanged += OnThemeChanged;
            ApplyMacStyleWindowBackdrop();
            ShowStep(_flow.Step);

            // Again after CenterOwner has positioned it: centring on an owner that
            // is itself near an edge can push a window that FITS partly off screen.
            ClampToWorkArea();
        };

        // The analogue of macOS's NSApplication.didBecomeActiveNotification handler
        // (OnboardingView.swift:121-124). Coming back from Windows Settings or from
        // the app's own shortcut editor has to refresh both rows on the permissions
        // step, or they hold state the user has just changed.
        Activated += (_, _) =>
        {
            _flow.RefreshPermissions();
            _flow.RefreshShortcutRegistration();
        };

        Closing += OnWindowClosing;

        Closed += (_, _) =>
        {
            ThemeService.Instance.ThemeChanged -= OnThemeChanged;
            _flow.PropertyChanged -= OnFlowPropertyChanged;
        };

        // Mirrors macOS's .interactiveDismissDisabled(). The only two exits are the
        // footer's "Set Up Later" and, on the last step, "Done Onboarding"; Escape
        // must not throw away a half finished setup by accident.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                e.Handled = true;
        };
    }

    // =========================================================================
    // FITTING THE SCREEN
    // =========================================================================

    /// <summary>Re-entrancy guard: this method moves the window, and moving it raises
    /// <see cref="Window.LocationChanged"/>, which calls it again.</summary>
    private bool _clamping;

    /// <summary>
    /// Keep the window inside the work area of the monitor it is on.
    ///
    /// 760 x 624 mirrors the macOS sheet and is right on a desktop. It is not right
    /// on a 1366 x 768 laptop at 150% scaling, where the whole work area is about
    /// 910 x 464 DIP: the designed height is a third taller than the screen, and
    /// because this window is <c>ResizeMode="NoResize"</c> with a custom caption, a
    /// user whose Continue button is below the bottom edge has no way to reach it -
    /// not by dragging, not by keyboard, not by maximizing.
    ///
    /// This is deliberately NOT a redesign. The designed width is kept whenever it
    /// fits, nothing is re-laid out, and the slack is taken by the piece already
    /// built to take it: <c>OnboardingStage</c> is a ScrollViewer whose content grid
    /// tracks the viewport height, so a shorter window simply scrolls a step that no
    /// longer fits and still centres one that does. The three fixed bands - caption,
    /// hairline and the footer - are outside that scroller and therefore stay on
    /// screen at every size, which is the whole point of having put them there.
    ///
    /// The monitor comes from the window's own handle rather than
    /// <c>SystemParameters.WorkArea</c>, which is the primary display only: this
    /// window is centred on its owner, and the owner is wherever the user left the
    /// app.
    /// </summary>
    private void ClampToWorkArea()
    {
        if (_clamping) return;

        try
        {
            _clamping = true;

            var (workWidth, workHeight, workLeft, workTop) = CurrentWorkAreaDip();
            if (workWidth <= 0 || workHeight <= 0)
                return;

            var (width, height) = FitToWorkArea(workWidth, workHeight);

            if (Math.Abs(Width - width) > 0.5) Width = width;
            if (Math.Abs(Height - height) > 0.5) Height = height;

            // A window that FITS can still be positioned off the edge, because
            // CenterOwner centres on the owner and not on the screen.
            if (!double.IsNaN(Left) && !double.IsNaN(Top))
            {
                var left = Math.Min(Math.Max(Left, workLeft), workLeft + workWidth - width);
                var top = Math.Min(Math.Max(Top, workTop), workTop + workHeight - height);

                if (Math.Abs(Left - left) > 0.5) Left = left;
                if (Math.Abs(Top - top) > 0.5) Top = top;
            }
        }
        catch (Exception ex)
        {
            // Never let a display query stop first run from opening.
            LoggingService.Debug($"OnboardingWindow: could not clamp to the work area: {ex.Message}");
        }
        finally
        {
            _clamping = false;
        }
    }

    /// <summary>
    /// The whole of the sizing policy, as one pure function of the work area, so the
    /// smoke suite can pin 150% and 200% on a small laptop without a display.
    ///
    /// Never larger than the design size, never larger than the work area, never
    /// smaller than the floor. The floor wins over the work area on purpose: a window
    /// squeezed to nothing is worse than one that overhangs, and no work area that
    /// small exists on Windows.
    /// </summary>
    internal static (double Width, double Height) FitToWorkArea(double workWidth, double workHeight) =>
        (Math.Max(FloorWidth, Math.Min(DesignWidth, workWidth)),
         Math.Max(FloorHeight, Math.Min(DesignHeight, workHeight)));

    /// <summary>
    /// This window's monitor's work area, in device-independent pixels, which is what
    /// <see cref="Window.Width"/> and <see cref="Window.Left"/> are measured in.
    /// Falls back to the primary display before the handle exists.
    /// </summary>
    private (double Width, double Height, double Left, double Top) CurrentWorkAreaDip()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            var primary = SystemParameters.WorkArea; // already DIP
            return (primary.Width, primary.Height, primary.Left, primary.Top);
        }

        // Fully qualified: GlobalUsings pulls in System.Windows.Forms, and
        // System.Windows.Controls has a Screen-free namespace of its own.
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        var area = screen.WorkingArea; // physical pixels
        return (area.Width / scaleX, area.Height / scaleY, area.Left / scaleX, area.Top / scaleY);
    }

    // =========================================================================
    // STEP NAVIGATION
    // =========================================================================

    private void OnFlowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingFlowViewModel.Step))
            ShowStep(_flow.Step);
    }

    /// <summary>
    /// Maps a step to its page and navigates. The switch is the whole of this
    /// window's knowledge of the flow: it decides nothing, it only renders what the
    /// model already moved to.
    /// </summary>
    private void ShowStep(OnboardingStep step)
    {
        if (!_pages.TryGetValue(step, out var page))
        {
            page = step switch
            {
                OnboardingStep.Welcome => new WelcomeStepPage(),
                OnboardingStep.Permissions => new PermissionsStepPage(),
                OnboardingStep.Source => new SourceStepPage(),
                OnboardingStep.Configure => new ConfigureStepPage(),
                OnboardingStep.Setup => new SetupStepPage(),
                OnboardingStep.Microphone => new MicrophoneStepPage(),
                OnboardingStep.TryIt => new TryItStepPage(),
                _ => new DoneStepPage()
            };

            // The window owns the model; every page just reads it.
            page.DataContext = _flow;
            _pages[step] = page;
        }

        StepFrame.Navigate(page);
    }

    // =========================================================================
    // FOOTER ACTIONS
    // =========================================================================

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_flow.Step == OnboardingSteps.Last)
        {
            // Explicit completion: the staged source becomes production state.
            // A refused write leaves the window open over a flow that has NOT
            // been marked complete and still holds its restore point, so the
            // user can press Done again or fall back to Set Up Later.
            if (!_flow.Complete())
            {
                ReportFailedSourceApply();
                return;
            }

            _flowResolved = true;
            Close();
            return;
        }

        if (!_flow.Advance())
            ReportFailedSourceApply();
    }

    /// <summary>
    /// The apply-side mirror of <see cref="ReportUnrestoredState"/>. Advance() and
    /// Complete() return false for two very different reasons - a closed gate,
    /// which the disabled button already explains, and a database that refused the
    /// one production write this flow makes before completion, which nothing
    /// explains. Only the second is worth a box.
    /// </summary>
    private void ReportFailedSourceApply()
    {
        if (!_flow.SourceApplyFailed)
            return;

        if (App.IsSessionEnding)
        {
            LoggingService.Warn(
                "OnboardingWindow: the staged source could not be written, but the OS is ending the "
                + "session so the report is logged rather than shown");
            return;
        }

        // Fully qualified: GlobalUsings pulls in System.Windows.Forms, which has a
        // MessageBox of its own.
        System.Windows.MessageBox.Show(
            this,
            Loc.S("onboarding.apply.mode.failed"),
            Loc.S("errors.unhandled.title"),
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    private void SetUpLaterButton_Click(object sender, RoutedEventArgs e) => DeferAndClose();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DeferAndClose();

    /// <summary>
    /// The two EXPLICIT exits: the footer's "Set Up Later" and the caption X. The
    /// user has decided not to set up now, so anything already written is rolled
    /// back and first run is closed for good, which is what macOS's
    /// <c>deferSetup()</c> does (it reaches the same <c>markOnboardingCompleted()</c>
    /// as <c>complete()</c>, clearing both of its flags).
    ///
    /// A close that is NOT a decision goes through <see cref="OnWindowClosing"/> to
    /// <c>AbandonSetup()</c> instead, which leaves OnboardingPending set.
    /// </summary>
    private void DeferAndClose()
    {
        _flow.DeferSetup();
        _flowResolved = true;
        ReportUnrestoredState();
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Alt+F4, the taskbar, tray Quit and an OS shutdown all land here without
        // the user having chosen anything. Roll back exactly as "Set Up Later"
        // does, but do NOT mark first run complete: an interrupted first run has to
        // be re-offered on the next launch, and a PC that restarts for an update
        // mid-flow must not drop a brand-new user into an unconfigured app forever.
        if (!_flowResolved)
        {
            _flow.AbandonSetup();
            _flowResolved = true;

            // AND REPORT, exactly as the explicit exits do. The rollback these
            // four routes run is the SAME rollback, and it can lose the same
            // pre-onboarding API key: the flow overwrote it to test a new one and
            // Credential Manager then refused both attempts to put it back. The
            // earlier reasoning - "a modal box would block OS shutdown" - is true
            // of exactly one of the four, and App.IsSessionEnding is what
            // distinguishes it. Alt+F4 and the taskbar leave the app running (the
            // flow ends with ReturnToHome), so there is nothing a dialog can
            // block there.
            ReportUnrestoredState();
        }

        _flow.Cleanup();
    }

    /// <summary>
    /// A reversible write the flow made and then could not put back is a real
    /// loss. Say so, naming what, instead of closing over the top of it.
    ///
    /// Two sinks can refuse: Windows Credential Manager (per provider) and the
    /// Modes database (the default Mode row). Both keep their restore point on
    /// failure, so both are still recoverable; the user has to be told which.
    /// </summary>
    private void ReportUnrestoredState()
    {
        var lost = _flow.UnrestoredProviderKeys;
        if (lost.Count == 0 && !_flow.ModeRestoreFailed)
            return;

        // The single exception. See App.IsSessionEnding.
        if (App.IsSessionEnding)
        {
            LoggingService.Warn(
                "OnboardingWindow: rollback left state unrestored, but the OS is ending the session "
                + "so the report is logged rather than shown");
            return;
        }

        var lines = new List<string>();

        if (_flow.ModeRestoreFailed)
            lines.Add(Loc.S("onboarding.restore.mode.failed"));

        if (lost.Count > 0)
        {
            var providers = string.Join(", ", lost.Select(p => p.GetDisplayName()));
            lines.Add($"{Loc.S("onboarding.setup.provider.saveFailed")}\n\n{providers}");
        }

        // Fully qualified: GlobalUsings pulls in System.Windows.Forms, which has a
        // MessageBox of its own.
        System.Windows.MessageBox.Show(
            this,
            string.Join("\n\n", lines),
            Loc.S("errors.unhandled.title"),
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    // =========================================================================
    // BACKDROP
    // Copied from MainWindow so the two windows are the same object to look at.
    // =========================================================================

    private void OnThemeChanged(object? sender, bool isDarkMode) =>
        Dispatcher.Invoke(ApplyMacStyleWindowBackdrop);

    private void ApplyMacStyleWindowBackdrop()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var darkMode = ThemeService.Instance.IsDarkMode ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, Marshal.SizeOf<int>());

            var cornerPreference = DwmWindowCornerPreferenceRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, Marshal.SizeOf<int>());

            var backdropType = DwmSystemBackdropTypeMica;
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, Marshal.SizeOf<int>());
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"OnboardingWindow: Native backdrop unavailable: {ex.Message}");
        }
    }
}
