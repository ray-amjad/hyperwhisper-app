using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
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
    private bool _isCheckingForUpdatesFromTray;
    private bool _shutdownStarted;

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

            // Hide window if LaunchMinimized is enabled
            if (SettingsService.Instance.LaunchMinimized)
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
        TrialSidebarActions.Visibility = isLicensed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpgradeSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (!LicenseManager.Instance.OpenPurchasePage(out var errorMessage))
        {
            WpfMessageBox.Show(
                Loc.S("settings.general.support.openFailed", errorMessage ?? ""),
                Loc.S("common.error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void EnterLicenseSidebar_Click(object sender, RoutedEventArgs e)
    {
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
        _recordingMenu.Enabled = _viewModel.IsRecording ||
            (!_viewModel.IsTranscribing &&
             !_viewModel.IsModelLoading &&
             _viewModel.SelectedAudioDevice != null &&
             _viewModel.SelectedMode != null);
    }

    private async void ToggleRecordingFromTray()
    {
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
                Enabled = !_viewModel.IsRecording && !_viewModel.IsTranscribing && !_viewModel.IsModelLoading,
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
