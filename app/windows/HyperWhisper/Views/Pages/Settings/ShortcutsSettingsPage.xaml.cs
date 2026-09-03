// SHORTCUTS SETTINGS PAGE
// Handles global shortcuts and push-to-talk configuration.

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Views.Controls;

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
        ToggleShortcutBox.DisplayText = settings.ToggleShortcut.ToDisplayString();
        CancelShortcutBox.DisplayText = settings.CancelShortcut.ToDisplayString();
        ChangeModeShortcutBox.DisplayText = settings.ChangeModeShortcut.ToDisplayString();
        StreamingShortcutBox.DisplayText = settings.StreamingShortcut.ToDisplayString();

        SetPushToTalkModeSelection(settings.PushToTalk.Mode);
        SetPushToTalkModifierSelection(settings.PushToTalk.Modifier);
        PushToTalkCustomBox.DisplayText = settings.PushToTalk.CustomShortcut?.ToDisplayString()
            ?? Localization.Loc.S("settings.shortcuts.pushToTalk.unassigned");
        PushToTalkDoublePressBox.IsChecked = settings.PushToTalk.DoublePressLock;
    }

    /// <summary>
    /// A recorder captured a chord. It has already rejected single bare modifiers
    /// and duplicates against the other three global shortcuts, so all that is left
    /// is deciding where this role's value goes.
    ///
    /// The switch is what it always was; only the capture, the validation and the
    /// error rendering moved out, into ShortcutRecorderBox, so the onboarding
    /// Permissions step could host a recorder without a second copy of the rules.
    /// </summary>
    private void ShortcutBox_Captured(object sender, ShortcutCapturedEventArgs e)
    {
        switch (e.Role)
        {
            case "Toggle":
                _settingsService.ToggleShortcut = e.Shortcut;
                break;
            case "Cancel":
                _settingsService.CancelShortcut = e.Shortcut;
                break;
            case "ChangeMode":
                _settingsService.ChangeModeShortcut = e.Shortcut;
                break;
            case "Streaming":
                _settingsService.StreamingShortcut = e.Shortcut;
                break;
            case "PushToTalkCustom":
                UpdatePushToTalkSetting(p =>
                {
                    p.CustomShortcut = e.Shortcut;
                    p.Mode = PushToTalkMode.Custom;
                });
                SetPushToTalkModeSelection(PushToTalkMode.Custom);
                UpdatePushToTalkVisibility();
                break;
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
            ToggleShortcutBox.DisplayText = _settingsService.ToggleShortcut.ToDisplayString();
            migrated = true;
        }

        // Check Cancel shortcut
        if (_settingsService.CancelShortcut.IsSingleBareModifier)
        {
            LoggingService.Warn($"Cancel shortcut is a single bare modifier ({_settingsService.CancelShortcut.ToDisplayString()}). Auto-migrating to default.");
            _settingsService.CancelShortcut = KeyboardShortcut.FromPersistedString("Esc");
            CancelShortcutBox.DisplayText = _settingsService.CancelShortcut.ToDisplayString();
            migrated = true;
        }

        // Check ChangeMode shortcut
        if (_settingsService.ChangeModeShortcut.IsSingleBareModifier)
        {
            LoggingService.Warn($"ChangeMode shortcut is a single bare modifier ({_settingsService.ChangeModeShortcut.ToDisplayString()}). Auto-migrating to default.");
            _settingsService.ChangeModeShortcut = KeyboardShortcut.FromPersistedString("Ctrl+Shift+.");
            ChangeModeShortcutBox.DisplayText = _settingsService.ChangeModeShortcut.ToDisplayString();
            migrated = true;
        }

        if (_settingsService.StreamingShortcut.IsSingleBareModifier)
        {
            LoggingService.Warn($"Streaming shortcut is a single bare modifier ({_settingsService.StreamingShortcut.ToDisplayString()}). Auto-migrating to default.");
            _settingsService.StreamingShortcut = KeyboardShortcut.FromPersistedString("Ctrl+Shift+Space");
            StreamingShortcutBox.DisplayText = _settingsService.StreamingShortcut.ToDisplayString();
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
