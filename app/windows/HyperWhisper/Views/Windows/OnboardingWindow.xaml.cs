using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

            ApplyMacStyleWindowBackdrop();
        };

        Loaded += (_, _) =>
        {
            ThemeService.Instance.ThemeChanged += OnThemeChanged;
            ApplyMacStyleWindowBackdrop();
            ShowStep(_flow.Step);
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
            _flow.Complete();
            _flowResolved = true;
            Close();
            return;
        }

        _flow.Advance();
    }

    private void SetUpLaterButton_Click(object sender, RoutedEventArgs e) => DeferAndClose();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DeferAndClose();

    /// <summary>
    /// Anything already written is rolled back first, so the app is left exactly as
    /// it was before the window opened. OnboardingPending is deliberately NOT
    /// cleared: an interrupted first run has to be re-offered on the next launch.
    /// </summary>
    private void DeferAndClose()
    {
        _flow.DeferSetup();
        _flowResolved = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Alt+F4, the taskbar, or a shutdown all land here. Treat any of them as
        // "Set Up Later" rather than as a silent completion: DeferSetup is
        // idempotent, so a second call after the footer already ran does nothing.
        if (!_flowResolved)
        {
            _flow.DeferSetup();
            _flowResolved = true;
        }

        _flow.Cleanup();
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
