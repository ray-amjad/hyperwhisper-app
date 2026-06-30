// SHORTCUTS SETTINGS PAGE
// Handles global shortcuts and push-to-talk configuration.

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Pages.Settings;

public partial class ShortcutsSettingsPage : Page
{
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private ViewModels.MainViewModel? _mainViewModel;

    public ShortcutsSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel != null)
        {
            _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        }

        _mainViewModel = Window.GetWindow(this)?.DataContext as ViewModels.MainViewModel;
        if (_mainViewModel != null)
        {
            _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        }

        LoadShortcutSettings();
        MigrateModifierOnlyShortcuts(); // Auto-fix bad shortcuts
        UpdateConflictBanner();
        UpdatePushToTalkVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel != null)
        {
            _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
            _mainViewModel = null;
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.MainViewModel.HasShortcutConflicts)
            or nameof(ViewModels.MainViewModel.ShortcutConflictMessage))
        {
            Dispatcher.Invoke(UpdateConflictBanner);
        }
    }

    private void UpdateConflictBanner()
    {
        if (Window.GetWindow(this)?.DataContext is ViewModels.MainViewModel vm && vm.HasShortcutConflicts)
        {
            ShortcutConflictBanner.Visibility = Visibility.Visible;
            ShortcutConflictText.Text = vm.ShortcutConflictMessage;
        }
        else
        {
            ShortcutConflictBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadShortcutSettings()
    {
        var settings = _settingsService;
        ToggleShortcutBox.Text = settings.ToggleShortcut.ToDisplayString();
        CancelShortcutBox.Text = settings.CancelShortcut.ToDisplayString();
        ChangeModeShortcutBox.Text = settings.ChangeModeShortcut.ToDisplayString();
        StreamingShortcutBox.Text = settings.StreamingShortcut.ToDisplayString();

        SetPushToTalkModeSelection(settings.PushToTalk.Mode);
        SetPushToTalkModifierSelection(settings.PushToTalk.Modifier);
        PushToTalkCustomBox.Text = settings.PushToTalk.CustomShortcut?.ToDisplayString()
            ?? Localization.Loc.S("settings.shortcuts.pushToTalk.unassigned");
        PushToTalkDoublePressBox.IsChecked = settings.PushToTalk.DoublePressLock;
    }

    private void ShortcutBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not WpfTextBox textBox) return;

        var shortcut = BuildShortcutFromKeyEvent(e);
        if (shortcut == null) return;

        string role = textBox.Tag as string ?? "";

        // VALIDATE: Reject unsafe single bare modifiers, but allow intentional
        // multi-modifier chords such as Ctrl+Win.
        if (shortcut.IsSingleBareModifier)
        {
            string errorMsg = "Single modifier shortcuts such as Ctrl, Alt, Shift, or Win are not supported. Use a key with modifiers or a multi-modifier shortcut such as Ctrl+Win.";
            ShowValidationError(role, errorMsg);
            LoggingService.Debug($"Rejected single-modifier shortcut for {role}: {shortcut}");
            return; // Don't save
        }

        // VALIDATE: Check for duplicates
        string? validationError = ShortcutValidationService.ValidateDuplicate(
            shortcut, role,
            _settingsService.ToggleShortcut,
            _settingsService.CancelShortcut,
            _settingsService.ChangeModeShortcut,
            _settingsService.StreamingShortcut
        );

        if (validationError != null)
        {
            ShowValidationError(role, validationError);
            LoggingService.Warn($"Shortcut validation failed for {role}: {validationError}");
            return; // Don't save
        }

        ClearValidationError(role);

        // SAVE: No issues, proceed
        switch (role)
        {
            case "Toggle":
                _settingsService.ToggleShortcut = shortcut;
                textBox.Text = shortcut.ToDisplayString();
                break;
            case "Cancel":
                _settingsService.CancelShortcut = shortcut;
                textBox.Text = shortcut.ToDisplayString();
                break;
            case "ChangeMode":
                _settingsService.ChangeModeShortcut = shortcut;
                textBox.Text = shortcut.ToDisplayString();
                break;
            case "Streaming":
                _settingsService.StreamingShortcut = shortcut;
                textBox.Text = shortcut.ToDisplayString();
                break;
            case "PushToTalkCustom":
                UpdatePushToTalkSetting(p =>
                {
                    p.CustomShortcut = shortcut;
                    p.Mode = PushToTalkMode.Custom;
                });
                PushToTalkCustomBox.Text = shortcut.ToDisplayString();
                SetPushToTalkModeSelection(PushToTalkMode.Custom);
                UpdatePushToTalkVisibility();
                break;
        }
    }

    private void ShortcutBox_PreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        // Keep the capture field from leaking Win-key releases to WPF text input.
        // The global hook still controls runtime shortcut suppression.
        e.Handled = true;
    }

    private KeyboardShortcut? BuildShortcutFromKeyEvent(WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return null;

        var shortcut = new KeyboardShortcut
        {
            Control = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || key is Key.LeftCtrl or Key.RightCtrl,
            Alt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) || key is Key.LeftAlt or Key.RightAlt,
            Shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) || key is Key.LeftShift or Key.RightShift,
            Win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin) || key is Key.LWin or Key.RWin
        };

        if (!IsModifierKey(key))
        {
            shortcut.Key = key;
        }

        return shortcut;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    // =========================================================================
    // VALIDATION ERROR DISPLAY
    // Shows inline error messages when shortcuts conflict.
    // =========================================================================

    private void ShowValidationError(string role, string errorMessage)
    {
        WpfTextBlock? errorTextBlock = role switch
        {
            "Toggle" => ToggleErrorText,
            "Cancel" => CancelErrorText,
            "ChangeMode" => ChangeModeErrorText,
            "Streaming" => StreamingErrorText,
            _ => null
        };

        if (errorTextBlock != null)
        {
            errorTextBlock.Text = errorMessage;
            errorTextBlock.Visibility = Visibility.Visible;
        }

        // Optional: red border on textbox
        WpfTextBox? textBox = role switch
        {
            "Toggle" => ToggleShortcutBox,
            "Cancel" => CancelShortcutBox,
            "ChangeMode" => ChangeModeShortcutBox,
            "Streaming" => StreamingShortcutBox,
            _ => null
        };
        if (textBox != null)
        {
            textBox.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
            textBox.BorderThickness = new Thickness(2);
        }
    }

    private void ClearValidationError(string role)
    {
        WpfTextBlock? errorTextBlock = role switch
        {
            "Toggle" => ToggleErrorText,
            "Cancel" => CancelErrorText,
            "ChangeMode" => ChangeModeErrorText,
            "Streaming" => StreamingErrorText,
            _ => null
        };

        if (errorTextBlock != null)
        {
            errorTextBlock.Visibility = Visibility.Collapsed;
            errorTextBlock.Text = "";
        }

        WpfTextBox? textBox = role switch
        {
            "Toggle" => ToggleShortcutBox,
            "Cancel" => CancelShortcutBox,
            "ChangeMode" => ChangeModeShortcutBox,
            "Streaming" => StreamingShortcutBox,
            _ => null
        };
        if (textBox != null)
        {
            textBox.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);
            textBox.ClearValue(System.Windows.Controls.Border.BorderThicknessProperty);
        }
    }

    /// <summary>
    /// Detects and auto-migrates unsafe single-modifier shortcuts back to defaults.
    /// Intentional multi-modifier chords such as Ctrl+Alt and Ctrl+Win are valid.
    /// </summary>
    private void MigrateModifierOnlyShortcuts()
    {
        bool migrated = false;

        // Check Toggle shortcut
        if (_settingsService.ToggleShortcut.IsSingleBareModifier)
        {
            LoggingService.Warn($"Toggle shortcut is a single bare modifier ({_settingsService.ToggleShortcut.ToDisplayString()}). Auto-migrating to default.");
            _settingsService.ToggleShortcut = KeyboardShortcut.FromPersistedString("Ctrl+Alt");
            ToggleShortcutBox.Text = _settingsService.ToggleShortcut.ToDisplayString();
            migrated = true;
        }

        // Check Cancel shortcut
        if (_settingsService.CancelShortcut.IsSingleBareModifier)
        {
            LoggingService.Warn($"Cancel shortcut is a single bare modifier ({_settingsService.CancelShortcut.ToDisplayString()}). Auto-migrating to default.");
            _settingsService.CancelShortcut = KeyboardShortcut.FromPersistedString("Esc");
            CancelShortcutBox.Text = _settingsService.CancelShortcut.ToDisplayString();
            migrated = true;
        }

        // Check ChangeMode shortcut
        if (_settingsService.ChangeModeShortcut.IsSingleBareModifier)
        {
            LoggingService.Warn($"ChangeMode shortcut is a single bare modifier ({_settingsService.ChangeModeShortcut.ToDisplayString()}). Auto-migrating to default.");
            _settingsService.ChangeModeShortcut = KeyboardShortcut.FromPersistedString("Ctrl+Shift+.");
            ChangeModeShortcutBox.Text = _settingsService.ChangeModeShortcut.ToDisplayString();
            migrated = true;
        }

        if (_settingsService.StreamingShortcut.IsSingleBareModifier)
        {
            LoggingService.Warn($"Streaming shortcut is a single bare modifier ({_settingsService.StreamingShortcut.ToDisplayString()}). Auto-migrating to default.");
            _settingsService.StreamingShortcut = KeyboardShortcut.FromPersistedString("Ctrl+Shift+Space");
            StreamingShortcutBox.Text = _settingsService.StreamingShortcut.ToDisplayString();
            migrated = true;
        }

        if (migrated)
        {
            LoggingService.Info("Auto-migrated single-modifier shortcuts to defaults");
            // Note: Settings are automatically saved by the property setters
        }
    }

    private void PushToTalkModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var mode = GetSelectedPushToTalkMode();
        UpdatePushToTalkSetting(p => p.Mode = mode);
        UpdatePushToTalkVisibility();
    }

    private void PushToTalkModifierBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var modifier = (PushToTalkModifierBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "LeftAlt";
        UpdatePushToTalkSetting(p => p.Modifier = modifier);
    }

    private void PushToTalkDoublePressBox_Checked(object sender, RoutedEventArgs e)
    {
        UpdatePushToTalkSetting(p => p.DoublePressLock = true);
    }

    private void PushToTalkDoublePressBox_Unchecked(object sender, RoutedEventArgs e)
    {
        UpdatePushToTalkSetting(p => p.DoublePressLock = false);
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.ResetShortcutsToDefaults();
        LoadShortcutSettings();
        UpdatePushToTalkVisibility();
    }

    private void UpdatePushToTalkVisibility()
    {
        var mode = GetSelectedPushToTalkMode();
        var modifierVisibility = mode == PushToTalkMode.Modifier ? Visibility.Visible : Visibility.Collapsed;
        var customVisibility = mode == PushToTalkMode.Custom ? Visibility.Visible : Visibility.Collapsed;

        PushToTalkModifierRow.Visibility = modifierVisibility;
        PushToTalkModifierBox.Visibility = modifierVisibility;
        PushToTalkCustomRow.Visibility = customVisibility;
        PushToTalkCustomBox.Visibility = customVisibility;
        PushToTalkDoublePressPanel.Visibility = mode == PushToTalkMode.Modifier
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private PushToTalkMode GetSelectedPushToTalkMode()
    {
        var tag = (PushToTalkModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Disabled";
        return Enum.TryParse<PushToTalkMode>(tag, out var mode) ? mode : PushToTalkMode.Disabled;
    }

    private void SetPushToTalkModeSelection(PushToTalkMode mode)
    {
        foreach (ComboBoxItem item in PushToTalkModeBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                PushToTalkModeBox.SelectedItem = item;
                break;
            }
        }
    }

    private void SetPushToTalkModifierSelection(string modifier)
    {
        foreach (ComboBoxItem item in PushToTalkModifierBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), modifier, StringComparison.OrdinalIgnoreCase))
            {
                PushToTalkModifierBox.SelectedItem = item;
                return;
            }
        }
        PushToTalkModifierBox.SelectedIndex = 0;
    }

    private void UpdatePushToTalkSetting(Action<PushToTalkSettings> mutator)
    {
        var current = _settingsService.PushToTalk;
        var next = new PushToTalkSettings
        {
            Mode = current.Mode,
            Modifier = current.Modifier,
            DoublePressLock = current.DoublePressLock,
            CustomShortcut = current.CustomShortcut?.Clone()
        };
        mutator(next);
        _settingsService.PushToTalk = next;
    }
}
