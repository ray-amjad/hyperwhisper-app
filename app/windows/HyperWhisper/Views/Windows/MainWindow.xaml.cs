using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.Onboarding;
using HyperWhisper.Services.Platform;
using HyperWhisper.ViewModels;
using HyperWhisper.Views.Pages;
using HyperWhisper.Views.Pages.Settings;
using WinForms = System.Windows.Forms;

namespace HyperWhisper.Views.Windows;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int DwmSystemBackdropTypeMica = 2;

    private readonly MainViewModel _viewModel;
    private RecordingOverlayWindow? _recordingOverlay;
    private FileTranscriptionProgressWindow? _fileProgressWindow;
    private ErrorToastWindow? _errorToast;
    private ModeChangeToastWindow? _modeToast;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ToolStripMenuItem? _recordingMenu;
    private System.Windows.Forms.ToolStripMenuItem? _microphoneMenu;
    private System.Windows.Forms.ToolStripMenuItem? _modeMenu;
    private System.Windows.Forms.ToolStripMenuItem? _fileTranscriptionMenu;
    private System.Windows.Forms.ToolStripMenuItem? _checkForUpdatesMenu;
    private System.Windows.Forms.ToolStripMenuItem? _runSetupAgainMenu;
    private bool _isCheckingForUpdatesFromTray;
    private bool _shutdownStarted;

    /// <summary>
    /// True for exactly as long as the onboarding window is up. The tray is built
    /// in this constructor, i.e. BEFORE the modal ever opens, so its recording
    /// items are clickable behind it; this flag is what keeps a competing
    /// recording from being started from there.
    /// </summary>
    private bool _isOnboardingOpen;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = (MainViewModel)DataContext;
        _viewModel.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(MainViewModel.CurrentPage)) NavigateToPage(_viewModel.CurrentPage); };

        // RECORDING OVERLAY EVENTS
        // Show/hide overlay based on ViewModel state and ShowRecordingWindow setting
        _viewModel.ShowOverlayRequested += (s, e) => Dispatcher.Invoke(() =>
        {
            // Only show overlay if ShowRecordingWindow setting is enabled
            if (SettingsService.Instance.ShowRecordingWindow)
            {
                EnsureOverlayCreated();
                _recordingOverlay!.SetModeName(_viewModel.CurrentMode?.Name ?? "Default");
                _recordingOverlay.ShowRecording();
            }
        });
        _viewModel.ShowStreamingOverlayRequested += (s, providerName) => Dispatcher.Invoke(() =>
        {
            if (SettingsService.Instance.ShowRecordingWindow)
            {
                EnsureOverlayCreated();
                _recordingOverlay!.ShowStreaming(providerName);
            }
        });
        _viewModel.StreamingConnectionStateChanged += (s, state) => Dispatcher.Invoke(() =>
            _recordingOverlay?.UpdateStreamingConnectionState(state));
        _viewModel.HideOverlayRequested += (s, e) => Dispatcher.Invoke(() => _recordingOverlay?.Hide());
        _viewModel.ShowTranscribingRequested += (s, e) => Dispatcher.Invoke(() =>
        {
            // Show transcribing state even if recording window was hidden
            // (feedback is still useful during processing)
            if (SettingsService.Instance.ShowRecordingWindow)
            {
                _recordingOverlay?.ShowTranscribing();
            }
        });
        _viewModel.ShowSuccessRequested += (s, e) => Dispatcher.Invoke(() =>
        {
            if (SettingsService.Instance.ShowRecordingWindow)
            {
                _recordingOverlay?.ShowSuccess();
            }
        });
        _viewModel.ShowCopiedRequested += (s, e) => Dispatcher.Invoke(() =>
        {
            if (SettingsService.Instance.ShowRecordingWindow)
            {
                _recordingOverlay?.ShowCopied();
            }
        });
        _viewModel.ShowStatusRequested += (s, msg) => Dispatcher.Invoke(() =>
        {
            if (SettingsService.Instance.ShowRecordingWindow)
            {
                _recordingOverlay?.ShowStatus(msg);
            }
        });
        _viewModel.AudioLevelChanged += (s, level) => Dispatcher.Invoke(() => _recordingOverlay?.UpdateAudioLevel(level));

        // CANCEL CONFIRMATION EVENTS
        // Wire up ViewModel cancel confirmation requests to overlay UI
        _viewModel.ShowCancelConfirmationRequested += (s, e) => Dispatcher.Invoke(() => _recordingOverlay?.ShowCancelConfirmation());
        _viewModel.HideCancelConfirmationRequested += (s, e) => Dispatcher.Invoke(() => _recordingOverlay?.HideCancelConfirmation());

        // ERROR TOAST EVENTS
        // Show error toast when recording/transcription errors occur (matches macOS InlineErrorToast)
        _viewModel.ShowErrorToastRequested += (s, args) => Dispatcher.Invoke(() => ShowErrorToast(args));

        // MODE CHANGE TOAST EVENTS
        // Show pill toast when user cycles modes via shortcut during recording (matches macOS ModeChangeToast)
        _viewModel.ShowModeToastRequested += (s, modeName) => Dispatcher.Invoke(() => ShowModeToast(modeName));

        // FILE TRANSCRIPTION PROGRESS EVENTS
        // Wire up ViewModel file progress requests to progress window
        _viewModel.ShowFileProgressRequested += (s, args) => Dispatcher.Invoke(() =>
        {
            EnsureFileProgressWindowCreated();
            _fileProgressWindow!.ShowProgress(args.FileName, args.OnCancel);
        });
        _viewModel.HideFileProgressRequested += (s, e) => Dispatcher.Invoke(() =>
        {
            _fileProgressWindow?.Dismiss();
        });
        _viewModel.UpdateFileProgressRequested += (s, progress) => Dispatcher.Invoke(() =>
        {
            // Smart animation duration based on progress delta
            float progressDelta = Math.Abs(progress - (_fileProgressWindow?.CurrentProgress ?? 0f));

            double duration = progressDelta switch
            {
                < 0.05f => 0.3,  // Small jumps: quick (0.3s)
                < 0.20f => 1.0,  // Medium jumps: moderate (1s)
                _ => 60.0        // Large jumps (transcribing): slow (60s)
            };

            _fileProgressWindow?.AnimateProgress(progress, duration);
        });

        InitializeSystemTray();
        LicenseManager.Instance.LicenseStatusChanged += OnLicenseStatusChanged;

        // SINGLE-INSTANCE: Listen for broadcast from a second instance trying to launch
        SourceInitialized += (s, e) =>
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(SingleInstanceWndProc);
            if (source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            ApplyMacStyleWindowBackdrop();
        };

        // STARTUP BEHAVIOR
        // If LaunchMinimized is enabled, hide the window after initialization
        Loaded += async (s, e) =>
        {
            await _viewModel.OnNavigatedToAsync();
            NavigateToPage(_viewModel.CurrentPage);
            UpdateSidebarLicenseState();
            ThemeService.Instance.ThemeChanged += OnThemeChanged;
            ApplyMacStyleWindowBackdrop();

            // FIRST RUN ONBOARDING
            // Resolved here, and deliberately in this position:
            //   * AFTER OnNavigatedToAsync, because that is what populates
            //     AudioDevices and registers the global hotkeys. The Microphone
            //     step reads the first and the Permissions step's shortcut row
            //     reads the second, and KeyboardShortcutService needs this window
            //     to own an HWND or the row reports Unknown forever.
            //   * BEFORE the LaunchMinimized hide, mirroring the macOS ordering
            //     comment at hyperwhisperApp.swift:845-847. Hiding first would
            //     leave a first run user with LaunchMinimized on staring at an
            //     ownerless modal over an empty desktop.
            if (App.ShouldShowOnboarding)
            {
                ShowOnboarding();
            }

            // Hide window if LaunchMinimized is enabled
            if (SettingsService.Instance.LaunchMinimized && !App.ShouldShowOnboarding)
            {
                Hide();
                _notifyIcon?.ShowBalloonTip(2000, "HyperWhisper", $"Running in background. Press {_viewModel.HotkeyText} to record.", System.Windows.Forms.ToolTipIcon.Info);
                LoggingService.Info("MainWindow: Started minimized to system tray");
            }
        };

        // FOREGROUND KEEPALIVE
        // Run a periodic /warmup ping while the app is the active window so
        // the pooled HTTP/2 connection to HyperWhisper Cloud stays warm
        // across long idle gaps. Pause when the app loses focus to avoid
        // background traffic. Note: WPF's Deactivated also fires when an
        // in-app sub-window (e.g., Settings) takes focus — acceptable, the
        // ticker just resumes on the next Activated.
        Activated += (s, e) => _viewModel.StartCloudKeepalive();
        Deactivated += (s, e) => _viewModel.StopCloudKeepalive();

        // WINDOW CLOSING BEHAVIOR
        // Respects MinimizeToTray setting:
        // - When enabled: Cancel close and hide to system tray
        // - When disabled: Allow window to close and exit the application
        Closing += async (s, e) =>
        {
            if (_shutdownStarted)
                return;

            if (SettingsService.Instance.MinimizeToTray && !UpdateService.IsUpdateShutdownRequested)
            {
                // Minimize to tray instead of closing
                e.Cancel = true;
                Hide();
                _notifyIcon?.ShowBalloonTip(2000, "HyperWhisper", $"Running in background. Press {_viewModel.HotkeyText} to record.", System.Windows.Forms.ToolTipIcon.Info);
                return;
            }

            e.Cancel = true;
            await ShutdownAsync();
        };
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownStarted)
            return;

        _shutdownStarted = true;

        try
        {
            await _viewModel.CleanupAsync();
            _recordingOverlay?.Close();
            _fileProgressWindow?.Close();
            _modeToast?.Close();
            _notifyIcon?.Dispose();
            LicenseManager.Instance.LicenseStatusChanged -= OnLicenseStatusChanged;
            ThemeService.Instance.ThemeChanged -= OnThemeChanged;
        }
        catch (Exception ex)
        {
            LoggingService.Error("MainWindow: Shutdown cleanup failed", ex);
        }
        finally
        {
            WpfApplication.Current.Shutdown();
        }
    }

    private async void QuitFromTrayAsync(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            await ShutdownAsync();
            return;
        }

        var shutdownTask = await Dispatcher.InvokeAsync(ShutdownAsync);
        await shutdownTask;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

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
            LoggingService.Debug($"MainWindow: Native backdrop unavailable: {ex.Message}");
        }
    }

    private void OnThemeChanged(object? sender, bool isDarkMode) => Dispatcher.Invoke(ApplyMacStyleWindowBackdrop);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void NavigateToPage(MainViewModel.NavigationPage page, string? settingsSection = null)
    {
        Page? p = page switch
        {
            MainViewModel.NavigationPage.Home => new HomePage(),
            MainViewModel.NavigationPage.Modes => new ModesPage(),
            MainViewModel.NavigationPage.Vocabulary => new VocabularyPage(),
            MainViewModel.NavigationPage.Streaming => new StreamingSettingsPage(),
            MainViewModel.NavigationPage.ModelLibrary => new ModelsSettingsPage(),
            MainViewModel.NavigationPage.History => new HistoryPage(),
            MainViewModel.NavigationPage.Settings => string.IsNullOrWhiteSpace(settingsSection) ? new SettingsPage() : new SettingsPage(settingsSection),
            _ => new HomePage()
        };
        if (p is HomePage) p.DataContext = _viewModel;
        ContentFrame.Navigate(p);
    }

    private void OnLicenseStatusChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(UpdateSidebarLicenseState);

    private void UpdateSidebarLicenseState()
    {
        var isLicensed = LicenseManager.Instance.LicenseStatus == LicenseStatus.Active;
        LicensedSidebarCard.Visibility = isLicensed ? Visibility.Visible : Visibility.Collapsed;
        CloudSidebarActions.Visibility = isLicensed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloudCreditsSidebar_Click(object sender, RoutedEventArgs e)
    {
        // Open the combined Cloud account / credits + license activation panel.
        SettingsNavButton.IsChecked = true;
        _viewModel.CurrentPage = MainViewModel.NavigationPage.Settings;
        NavigateToPage(MainViewModel.NavigationPage.Settings, "License");
    }

    public void NavigateToSettingsSection(string sectionTag)
    {
        SettingsNavButton.IsChecked = true;
        _viewModel.CurrentPage = MainViewModel.NavigationPage.Settings;
        NavigateToPage(MainViewModel.NavigationPage.Settings, sectionTag);
    }

    /// <summary>
    /// Creates the recording overlay window if it doesn't exist and wires up all events.
    /// Consolidates overlay creation to ensure event handlers are only attached once.
    ///
    /// EVENT FLOW FOR CANCEL:
    /// 1. User presses Escape -> RecordingOverlayWindow.EscapePressed
    /// 2. MainWindow routes to ViewModel.HandleCancelRequestCommand
    /// 3. ViewModel checks duration:
    ///    - < 15s: Cancels immediately, fires HideOverlayRequested
    ///    - >= 15s: Shows confirmation, fires ShowCancelConfirmationRequested
    /// 4. If confirmation shown:
    ///    - User presses Escape/No: CancelDismissed -> ViewModel.HandleCancelRequest (dismisses)
    ///    - User presses Enter/Yes: CancelConfirmed -> ViewModel.ConfirmCancelRecordingCommand
    /// </summary>
    private void EnsureOverlayCreated()
    {
        if (_recordingOverlay != null) return;

        _recordingOverlay = new RecordingOverlayWindow();

        // STOP BUTTON: Stop recording and transcribe
        _recordingOverlay.StopClicked += async (s, e) =>
            await _viewModel.StopRecordingAndTranscribeCommand.ExecuteAsync(null);

        // ESCAPE KEY: Trigger cancel flow (may show confirmation if > 15s)
        _recordingOverlay.EscapePressed += async (s, e) =>
            await _viewModel.HandleCancelRequestCommand.ExecuteAsync(null);

        // CANCEL CONFIRMED: User clicked Yes or pressed Enter on confirmation
        _recordingOverlay.CancelConfirmed += async (s, e) =>
            await _viewModel.ConfirmCancelRecordingCommand.ExecuteAsync(null);

        // CANCEL DISMISSED: User clicked No or pressed Escape on confirmation
        // This triggers HandleCancelRequest again, which sees ShowingCancelConfirmation=true
        // and dismisses the confirmation (resumes recording)
        _recordingOverlay.CancelDismissed += async (s, e) =>
            await _viewModel.HandleCancelRequestCommand.ExecuteAsync(null);
    }

    private void EnsureFileProgressWindowCreated()
    {
        if (_fileProgressWindow != null) return;
        _fileProgressWindow = new FileTranscriptionProgressWindow();
    }

    /// <summary>
    /// Shows an error toast notification above the recording dialog.
    /// Matches macOS InlineErrorToast: pill-shaped, auto-dismissing with countdown.
    /// </summary>
    private void ShowErrorToast(ErrorToastEventArgs args)
    {
        // Dismiss any existing toast first
        _errorToast?.DismissImmediately();

        _errorToast = new ErrorToastWindow();
        _errorToast.SettingsRequested += (s, e) =>
        {
            Show();
            Activate();

            // Credential errors route to the Model Library with the API keys modal auto-opened
            // (mirrors macOS AppState.navigateToModelLibraryAPIKeys).
            if (args.OpenApiKeysManager)
            {
                _viewModel.ShouldOpenModelLibraryApiKeys = true;
                ModelLibraryNavButton.IsChecked = true;
                _viewModel.CurrentPage = MainViewModel.NavigationPage.ModelLibrary;
                NavigateToPage(MainViewModel.NavigationPage.ModelLibrary);
                return;
            }

            if (args.SettingsSection == "Models")
            {
                ModelLibraryNavButton.IsChecked = true;
                _viewModel.CurrentPage = MainViewModel.NavigationPage.ModelLibrary;
                NavigateToPage(MainViewModel.NavigationPage.ModelLibrary);
                return;
            }

            // Show main window and navigate to settings
            SettingsNavButton.IsChecked = true;
            _viewModel.CurrentPage = MainViewModel.NavigationPage.Settings;
            NavigateToPage(MainViewModel.NavigationPage.Settings, args.SettingsSection ?? "General");
        };
        _errorToast.Dismissed += (s, e) =>
        {
            _errorToast = null;
        };

        _errorToast.ShowError(args.Message, args.ShowSettingsButton, guidanceText: args.GuidanceText);
    }

    /// <summary>
    /// Shows a mode change toast notification above the recording dialog.
    /// Matches macOS ModeChangeToast: pill-shaped, auto-dismissing after 2 seconds.
    /// </summary>
    private void ShowModeToast(string modeName)
    {
        _modeToast?.DismissImmediately();
        _modeToast = new ModeChangeToastWindow();
        _modeToast.Dismissed += (s, e) => { _modeToast = null; };
        _modeToast.ShowMode(modeName);
    }

    // =========================================================================
    // FIRST RUN ONBOARDING
    // =========================================================================

    /// <summary>
    /// Builds the live flow, shows the onboarding window modally over this one,
    /// and releases everything it owns when it closes.
    ///
    /// Modal on purpose: the window is the port of a macOS sheet, and the flow
    /// stages writes against the same default Mode, credential store and input
    /// device the main window is showing. Two of them open at once would be two
    /// editors of one row.
    ///
    /// The delivery gate is raised for the whole lifetime of the window. The Try
    /// It step drives its own recorder and renders inline, so it never reaches a
    /// sink by itself; the gate is the backstop for the GLOBAL hotkey, which
    /// stays live behind the modal and would otherwise paste a test sentence into
    /// whatever the user had focused.
    /// </summary>
    private void ShowOnboarding()
    {
        if (_isOnboardingOpen)
            return;

        OnboardingLiveDependencies.LiveOnboarding? live = null;

        try
        {
            live = OnboardingLiveDependencies.CreateLive(
                // Deep-link to the Shortcuts section rather than to Settings in
                // general, so the row the user pressed the button about is the one
                // waiting for them. KNOWN LIMIT: this window is application modal,
                // so the shell behind it cannot be typed into until the flow ends.
                // The permissions row never gates Continue, and the shortcut is
                // re-read on every Activated, so the flow stays completable either
                // way.
                openShortcutSettings: () => Dispatcher.Invoke(() =>
                    NavigateToSettingsSection("Shortcuts")),
                returnToHome: () => Dispatcher.Invoke(() =>
                {
                    _viewModel.CurrentPage = MainViewModel.NavigationPage.Home;
                    NavigateToPage(MainViewModel.NavigationPage.Home);
                }));

            var window = new OnboardingWindow(live.Flow) { Owner = this };

            _isOnboardingOpen = true;
            TextDeliveryGate.SetSuppressed(true);
            RefreshRecordingMenu();
            RefreshFileTranscriptionMenu();

            LoggingService.Info("MainWindow: Showing the onboarding window");
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            // A first run flow that cannot open must never stop the app from
            // starting. The pending flag is left alone, so the next launch tries
            // again rather than silently skipping setup forever.
            LoggingService.Error("MainWindow: Failed to show the onboarding window", ex);
            SentryService.Capture(ex, "Failed to show the onboarding window");
        }
        finally
        {
            _isOnboardingOpen = false;
            TextDeliveryGate.SetSuppressed(false);

            // The flow model's own Cleanup() runs from the window's Closing; these
            // are the OS resources behind the seams (a COM device-notification
            // client, a capture stream, and event subscriptions on process
            // lifetime singletons) and outlive it if nobody disposes them.
            live?.DisposeResources();

            // Onboarding may have rewritten the default Mode and the selected
            // device, and it owns its own recorder, so re-read what the tray shows.
            RefreshRecordingMenu();
            RefreshMicrophoneMenu();
            RefreshModeMenu();
            RefreshFileTranscriptionMenu();
        }
    }

    /// <summary>
    /// The tray's "Run setup again". The main window is brought forward first:
    /// the onboarding window is shown with Owner = this, and a hidden owner would
    /// put a modal on screen with nothing behind it to return to.
    /// </summary>
    private void RunSetupAgainFromTray()
    {
        ShowMainWindow();
        ShowOnboarding();
    }

    private void InitializeSystemTray()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            _notifyIcon = new System.Windows.Forms.NotifyIcon { Text = "HyperWhisper", Visible = true };
            if (System.IO.File.Exists(iconPath)) _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            _recordingMenu = new System.Windows.Forms.ToolStripMenuItem();
            _recordingMenu.Click += (s, e) => Dispatcher.Invoke(ToggleRecordingFromTray);
            menu.Items.Add(_recordingMenu);
            menu.Items.Add("-");
            menu.Items.Add(Loc.S("menu.history"), null, (s, e) => Dispatcher.Invoke(() => ShowMainWindow(MainViewModel.NavigationPage.History)));
            menu.Items.Add(Loc.S("menu.settings"), null, (s, e) => Dispatcher.Invoke(() => ShowMainWindow(MainViewModel.NavigationPage.Settings)));

            // RUN SETUP AGAIN
            // macOS has no in-app re-run; Windows needs one because the only other
            // levers are a scratch HYPERWHISPER_WINDOWS_APPDATA_ROOT profile and
            // hand-editing settings.json with the app closed. A --onboarding switch
            // is NOT the answer: SingleInstanceGuard.TryAcquire() kills the second
            // instance before e.Args is ever inspected, so the flag would silently
            // do nothing whenever the app was already running.
            _runSetupAgainMenu = new System.Windows.Forms.ToolStripMenuItem(Loc.S("onboarding.menu.runAgain"));
            _runSetupAgainMenu.Click += (s, e) => Dispatcher.Invoke(RunSetupAgainFromTray);
            menu.Items.Add(_runSetupAgainMenu);
            menu.Items.Add("-");

            // MICROPHONE SUBMENU
            // Allows users to quickly switch microphones without opening the main window
            // Similar to macOS menu bar microphone selection
            _microphoneMenu = new System.Windows.Forms.ToolStripMenuItem(Loc.S("menu.microphone"));
            menu.Items.Add(_microphoneMenu);

            // MODE SUBMENU
            // Allows users to quickly switch transcription modes without opening the main window
            // Similar to macOS menu bar mode selection
            _modeMenu = new System.Windows.Forms.ToolStripMenuItem(Loc.S("menu.select.mode"));
            menu.Items.Add(_modeMenu);

            _fileTranscriptionMenu = new System.Windows.Forms.ToolStripMenuItem(Loc.S("menu.transcribe.file"));
            menu.Items.Add(_fileTranscriptionMenu);

            // Subscribe to audio device and mode changes to refresh the menus
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsRecording) ||
                    e.PropertyName == nameof(MainViewModel.IsTranscribing) ||
                    e.PropertyName == nameof(MainViewModel.IsModelLoading) ||
                    e.PropertyName == nameof(MainViewModel.SelectedAudioDevice) ||
                    e.PropertyName == nameof(MainViewModel.SelectedMode))
                {
                    Dispatcher.Invoke(RefreshRecordingMenu);
                    Dispatcher.Invoke(RefreshFileTranscriptionMenu);
                }

                if (e.PropertyName == nameof(MainViewModel.AudioDevices) ||
                    e.PropertyName == nameof(MainViewModel.SelectedAudioDevice))
                {
                    Dispatcher.Invoke(RefreshMicrophoneMenu);
                }
                else if (e.PropertyName == nameof(MainViewModel.Modes) ||
                         e.PropertyName == nameof(MainViewModel.SelectedMode))
                {
                    Dispatcher.Invoke(RefreshModeMenu);
                    Dispatcher.Invoke(RefreshFileTranscriptionMenu);
                }
            };

            // Refresh menus when context menu opens to ensure they're up-to-date
            menu.Opening += (s, e) =>
            {
                RefreshRecordingMenu();
                RefreshMicrophoneMenu();
                RefreshModeMenu();
                RefreshFileTranscriptionMenu();

                // Re-entering setup while it is already on screen would build a
                // second flow model over the same Mode row.
                if (_runSetupAgainMenu != null)
                    _runSetupAgainMenu.Enabled = !_isOnboardingOpen;
            };

            menu.Items.Add("-");
            menu.Items.Add(Loc.S("settings.resources.help.center"), null, (s, e) => OpenUrl("https://hyperwhisper.com/docs"));
            menu.Items.Add(Loc.S("settings.resources.contact.support"), null, (s, e) => OpenUrl("https://www.hyperwhisper.com/en/support"));
            menu.Items.Add(Loc.S("settings.resources.feedback"), null, (s, e) => OpenUrl("https://hyperwhisper.userjot.com"));
            menu.Items.Add("-");
            _checkForUpdatesMenu = new System.Windows.Forms.ToolStripMenuItem(Loc.S("settings.about.checkUpdates"));
            _checkForUpdatesMenu.Click += async (s, e) => await CheckForUpdatesFromTrayAsync();
            menu.Items.Add(_checkForUpdatesMenu);
            menu.Items.Add("-");
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";
            menu.Items.Add(new WinForms.ToolStripLabel(Loc.S("menu.version.label", version)) { Enabled = false });
            menu.Items.Add("-");
            menu.Items.Add(Loc.S("common.quit"), null, QuitFromTrayAsync);
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (s, e) => Dispatcher.Invoke(() => ShowMainWindow());
        }
        catch (Exception ex) { LoggingService.Warn($"Failed to init tray: {ex.Message}"); }
    }

    private void RefreshRecordingMenu()
    {
        if (_recordingMenu == null) return;

        _recordingMenu.Text = _viewModel.IsRecording
            ? Loc.S("menu.recording.stop")
            : Loc.S("menu.recording.toggle");
        // While onboarding is open the tray sits behind a modal that owns its own
        // recorder and stages the Mode the tray would record with, so no recording
        // may be started (or stopped) from here.
        _recordingMenu.Enabled = !_isOnboardingOpen &&
            (_viewModel.IsRecording ||
            (!_viewModel.IsTranscribing &&
             !_viewModel.IsModelLoading &&
             _viewModel.SelectedAudioDevice != null &&
             _viewModel.SelectedMode != null));
    }

    private async void ToggleRecordingFromTray()
    {
        // Belt and braces with RefreshRecordingMenu: a menu already open when the
        // onboarding window appeared still holds an enabled item.
        if (_isOnboardingOpen)
            return;

        if (_viewModel.IsTranscribing || _viewModel.IsModelLoading)
            return;

        if (_recordingMenu != null)
            _recordingMenu.Enabled = false;

        try
        {
            if (_viewModel.IsRecording)
                await _viewModel.StopRecordingAndTranscribeAsync();
            else
                await _viewModel.StartRecordingAsync();
        }
        finally
        {
            RefreshRecordingMenu();
        }
    }

    private async Task CheckForUpdatesFromTrayAsync()
    {
        if (_isCheckingForUpdatesFromTray)
            return;

        _isCheckingForUpdatesFromTray = true;
        var originalText = _checkForUpdatesMenu?.Text;

        if (_checkForUpdatesMenu != null)
        {
            _checkForUpdatesMenu.Enabled = false;
            _checkForUpdatesMenu.Text = Loc.S("settings.about.checkingForUpdates");
        }

        try
        {
            await UpdateService.CheckForUpdatesNow();
        }
        catch (Exception ex)
        {
            LoggingService.Error("MainWindow: Tray manual update check failed", ex);

            WpfMessageBox.Show(
                Loc.S("settings.about.updateCheckFailed.message", ex.Message),
                Loc.S("settings.about.updateCheckFailed.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (_checkForUpdatesMenu != null)
            {
                _checkForUpdatesMenu.Enabled = true;
                _checkForUpdatesMenu.Text = originalText ?? Loc.S("settings.about.checkUpdates");
            }

            _isCheckingForUpdatesFromTray = false;
        }
    }

    private void ShowMainWindow(MainViewModel.NavigationPage? page = null)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        if (page.HasValue)
        {
            _viewModel.CurrentPage = page.Value;
            NavigateToPage(page.Value);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Failed to open URL '{url}': {ex.Message}");
            System.Windows.MessageBox.Show(
                Loc.S("settings.general.support.openFailed", ex.Message),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Handles the WM_SHOWME broadcast from a second instance.
    /// Brings this window to the foreground so the user sees the already-running app.
    /// </summary>
    private IntPtr SingleInstanceWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)SingleInstanceGuard.WM_SHOWME)
        {
            WindowsSingleInstanceCoordinator.Instance.NotifyActivationRequested();
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Refreshes the microphone submenu with current available devices.
    /// Shows a checkmark (✓) next to the currently selected device.
    /// </summary>
    private void RefreshMicrophoneMenu()
    {
        if (_microphoneMenu == null) return;

        _microphoneMenu.DropDownItems.Clear();

        var devices = _viewModel.AudioDevices;
        var selectedDevice = _viewModel.SelectedAudioDevice;

        if (devices.Count == 0)
        {
            var noDevicesItem = new System.Windows.Forms.ToolStripMenuItem(Loc.S("menu.microphone.none"))
            {
                Enabled = false
            };
            _microphoneMenu.DropDownItems.Add(noDevicesItem);
            return;
        }

        foreach (var device in devices)
        {
            bool isSelected = selectedDevice != null && selectedDevice.DeviceNumber == device.DeviceNumber;

            var deviceItem = new System.Windows.Forms.ToolStripMenuItem(device.Name)
            {
                Checked = isSelected,
                Tag = device
            };

            deviceItem.Click += (s, e) =>
            {
                if (s is System.Windows.Forms.ToolStripMenuItem item && item.Tag is AudioDeviceService.AudioDevice dev)
                {
                    _viewModel.SelectedAudioDevice = dev;
                    LoggingService.Info($"System tray: Selected microphone '{dev.Name}'");
                }
            };

            _microphoneMenu.DropDownItems.Add(deviceItem);
        }
    }

    /// <summary>
    /// Refreshes the mode submenu with current available modes.
    /// Shows a checkmark next to the currently selected mode.
    /// </summary>
    private void RefreshModeMenu()
    {
        if (_modeMenu == null) return;

        _modeMenu.DropDownItems.Clear();

        var modes = _viewModel.Modes;
        var selectedMode = _viewModel.SelectedMode;

        if (modes.Count == 0)
        {
            var noModesItem = new System.Windows.Forms.ToolStripMenuItem(Loc.S("menu.mode.none"))
            {
                Enabled = false
            };
            _modeMenu.DropDownItems.Add(noModesItem);
            return;
        }

        foreach (var mode in modes)
        {
            bool isSelected = selectedMode != null && selectedMode.Id == mode.Id;

            var modeName = string.IsNullOrWhiteSpace(mode.Name) ? Loc.S("menu.mode.unnamed") : mode.Name;
            var modeItem = new System.Windows.Forms.ToolStripMenuItem(modeName)
            {
                Checked = isSelected,
                Tag = mode
            };

            modeItem.Click += (s, e) =>
            {
                if (s is System.Windows.Forms.ToolStripMenuItem item && item.Tag is Mode m)
                {
                    _viewModel.SelectedMode = m;
                    LoggingService.Info($"System tray: Selected mode '{modeName}'");
                }
            };

            _modeMenu.DropDownItems.Add(modeItem);
        }
    }

    /// <summary>
    /// Refreshes the file transcription submenu with all modes, matching the macOS menu bar.
    /// </summary>
    private void RefreshFileTranscriptionMenu()
    {
        if (_fileTranscriptionMenu == null) return;

        _fileTranscriptionMenu.DropDownItems.Clear();

        var modes = _viewModel.Modes;
        if (modes.Count == 0)
        {
            var noModesItem = new System.Windows.Forms.ToolStripMenuItem(Loc.S("menu.mode.none"))
            {
                Enabled = false
            };
            _fileTranscriptionMenu.DropDownItems.Add(noModesItem);
            return;
        }

        foreach (var mode in modes)
        {
            var modeName = string.IsNullOrWhiteSpace(mode.Name) ? Loc.S("menu.mode.unnamed") : mode.Name;
            var modeItem = new System.Windows.Forms.ToolStripMenuItem(modeName)
            {
                Enabled = !_viewModel.IsRecording && !_viewModel.IsTranscribing && !_viewModel.IsModelLoading && !_isOnboardingOpen,
                Tag = mode
            };

            modeItem.Click += async (s, e) =>
            {
                if (s is System.Windows.Forms.ToolStripMenuItem item && item.Tag is Mode m)
                {
                    await _viewModel.TranscribeFileWithModeAsync(m);
                }
            };

            _fileTranscriptionMenu.DropDownItems.Add(modeItem);
        }
    }
}
