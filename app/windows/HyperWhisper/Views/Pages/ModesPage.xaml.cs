using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Utilities;
using HyperWhisper.Views.Windows;

namespace HyperWhisper.Views.Pages;

public partial class ModesPage : Page
{
    private bool _isHandlingSelection;
    private EventHandler<Mode>? _modeChangedHandler;
    private EventHandler<Mode>? _modeSelectedHandler;

    public ModesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadModes();

        // Subscribe to mode events for real-time updates from tray menu, keyboard shortcuts, etc.
        _modeChangedHandler = (s, mode) => Dispatcher.Invoke(LoadModes);
        _modeSelectedHandler = (s, mode) => Dispatcher.Invoke(() =>
        {
            // Update selection without full reload
            _isHandlingSelection = true;
            var modeToSelect = ModeListBox.ItemsSource?.Cast<Mode>()
                .FirstOrDefault(m => m.Id == mode.Id);
            if (modeToSelect != null)
            {
                ModeListBox.SelectedItem = modeToSelect;
            }
            _isHandlingSelection = false;
        });

        ModeService.Instance.ModeChanged += _modeChangedHandler;
        ModeService.Instance.ModeSelected += _modeSelectedHandler;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unsubscribe to prevent memory leaks
        if (_modeChangedHandler != null)
        {
            ModeService.Instance.ModeChanged -= _modeChangedHandler;
            _modeChangedHandler = null;
        }
        if (_modeSelectedHandler != null)
        {
            ModeService.Instance.ModeSelected -= _modeSelectedHandler;
            _modeSelectedHandler = null;
        }
    }

    private void LoadModes()
    {
        var modes = ModeService.Instance.GetAllModes();
        ModeListBox.ItemsSource = modes;

        var selectedMode = ModeService.Instance.GetSelectedMode();
        if (selectedMode != null)
        {
            var modeToSelect = modes.FirstOrDefault(m => m.Id == selectedMode.Id);
            if (modeToSelect != null)
            {
                _isHandlingSelection = true;
                ModeListBox.SelectedItem = modeToSelect;
                _isHandlingSelection = false;
            }
        }
    }

    private void ModeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isHandlingSelection) return;

        if (ModeListBox.SelectedItem is Mode selectedMode)
        {
            // Set as default (active) mode
            ModeService.Instance.SetSelectedMode(selectedMode.Id);
            
            // Refresh list to update "Default" badge
            LoadModes();
        }
    }

    private void EditMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton button && button.Tag is Mode mode)
        {
            // Stop selection change from happening if it bubbles up (though button handles click)

            var editor = new ModeEditorWindow(mode)
            {
                Owner = Window.GetWindow(this)
            };

            var result = editor.ShowDialog();

            if (result == true)
            {
                LoadModes();
            }
        }
    }

    private void CreateMode_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ModeEditorWindow(isCreateMode: true)
        {
            Owner = Window.GetWindow(this)
        };

        if (editor.ShowDialog() == true)
        {
            LoadModes();
        }
    }

    /// <summary>
    /// Called when a mode card is loaded. Populates provider/model display info dynamically.
    /// </summary>
    private void ModeCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not Mode mode)
            return;

        // Find child elements using the visual tree
        var cloudText = FindChildByName<TextBlock>(border, "CloudProviderText");
        var localIcon = FindChildByName<TextBlock>(border, "LocalModelIcon");
        var localText = FindChildByName<TextBlock>(border, "LocalModelText");

        if (mode.ProviderType == "cloud")
        {
            if (cloudText != null)
            {
                var provider = CloudTranscriptionProviderExtensions.FromIdentifier(mode.CloudProvider);
                var providerName = provider == CloudTranscriptionProvider.HyperWhisperCloud
                    ? "HyperWhisper"
                    : provider.GetDisplayName();

                if (provider == CloudTranscriptionProvider.HyperWhisperCloud)
                {
                    cloudText.Text = providerName;
                }
                else
                {
                    var model = CloudTranscriptionModels.GetById(mode.CloudTranscriptionModel, provider);
                    cloudText.Text = model != null
                        ? $"{providerName} · {model.DisplayName}"
                        : providerName;
                }
            }
        }
        else
        {
            // Local provider
            // Check for ARM64 - local transcription not available
            if (!PlatformHelper.SupportsLocalTranscription)
            {
                if (localIcon != null)
                {
                    localIcon.Text = "\uE7BA"; // Warning icon
                    localIcon.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["WarningBrush"];
                }
                if (localText != null)
                {
                    localText.Text = Loc.S("modes.arm64.notAvailable");
                    localText.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["WarningBrush"];
                }
                return;
            }

            string displayName;
            bool isDownloaded;

            if (mode.LocalEngine == "parakeet")
            {
                var parakeetModel = ParakeetModelInfo.AllModels
                    .FirstOrDefault(m => m.Id == mode.LocalParakeetModel);
                displayName = parakeetModel?.DisplayName ?? "Unknown";
                var parakeetService = new ParakeetModelService();
                isDownloaded = parakeetModel != null && parakeetService.IsModelDownloaded(parakeetModel);
            }
            else
            {
                var whisperModel = WhisperModelInfo.AllModels
                    .FirstOrDefault(m => m.Type == mode.ModelType);
                displayName = whisperModel?.DisplayName ??
                    (string.IsNullOrEmpty(mode.ModelType)
                        ? "Unknown"
                        : char.ToUpper(mode.ModelType[0]) + mode.ModelType[1..]);
                var whisperService = new WhisperModelService();
                isDownloaded = whisperModel != null && whisperService.IsModelDownloaded(whisperModel);
            }

            if (localText != null)
                localText.Text = displayName;

            if (localIcon != null && localText != null)
            {
                var brush = isDownloaded
                    ? (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["SuccessBrush"]
                    : (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["WarningBrush"];

                localIcon.Foreground = brush;
                localText.Foreground = brush;
            }
        }
    }

    /// <summary>
    /// Recursively finds a child element by name in the visual tree.
    /// </summary>
    private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T element && element.Name == name)
                return element;

            var result = FindChildByName<T>(child, name);
            if (result != null)
                return result;
        }
        return null;
    }
}