using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HyperWhisper.Services;
using HyperWhisper.ViewModels;

namespace HyperWhisper.Views.Pages;

/// <summary>
/// HISTORY PAGE CODE-BEHIND
///
/// Handles UI events and wires up the HistoryViewModel.
/// Most logic is in the ViewModel - this file handles:
/// - DataContext initialization
/// - Selection change events
/// - Date filter ComboBox events
/// - Cleanup on unload
/// </summary>
public partial class HistoryPage : Page
{
    private HistoryViewModel? _viewModel;

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Create and set the ViewModel
        _viewModel = new HistoryViewModel();
        DataContext = _viewModel;

        // Initialize the ViewModel (loads transcripts)
        await _viewModel.OnNavigatedToAsync();

        LoggingService.Info("HistoryPage: Loaded");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Cleanup the ViewModel
        _viewModel?.Cleanup();
        _viewModel = null;

        LoggingService.Info("HistoryPage: Unloaded");
    }

    /// <summary>
    /// Handles transcript list selection changes.
    /// Updates the ViewModel with the current selection.
    ///
    /// SELECTION FIX: Now that the ListBox is directly bound to TranscriptViewModel items
    /// (via CollectionViewSource with grouping), the SelectedItems collection contains
    /// the actual TranscriptViewModel objects, enabling proper individual selection.
    /// </summary>
    private void TranscriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null) return;

        // Get selected TranscriptViewModel items from the ListBox
        var selectedItems = TranscriptList.SelectedItems
            .Cast<TranscriptViewModel>()
            .ToList();

        // Update the ViewModel with the new selection
        _viewModel.UpdateSelection(selectedItems);
    }

    /// <summary>
    /// Handles date filter ComboBox selection changes.
    /// </summary>
    private void DateFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is not WpfComboBox comboBox) return;
        if (comboBox.SelectedItem is not ComboBoxItem selectedItem) return;

        var filterTag = selectedItem.Tag?.ToString();
        var filter = filterTag switch
        {
            "Today" => DateFilter.Today,
            "ThisWeek" => DateFilter.ThisWeek,
            "ThisMonth" => DateFilter.ThisMonth,
            _ => DateFilter.All
        };

        _viewModel.DateFilter = filter;
    }

    /// <summary>
    /// Commits a seek when the user finishes dragging the playback thumb.
    /// The two-way binding already updates PlaybackPositionSeconds live, but
    /// calling SeekCommand here guarantees the NAudio reader repositions even
    /// if the binding path was short-circuited.
    /// </summary>
    private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is not Slider slider) return;

        if (_viewModel.SeekCommand.CanExecute(slider.Value))
        {
            _viewModel.SeekCommand.Execute(slider.Value);
        }
    }

    /// <summary>
    /// Handles click-to-seek on the track (IsMoveToPointEnabled jumps the value
    /// but doesn't fire DragCompleted). PreviewMouseUp commits the seek after
    /// the click has settled on the new value.
    /// </summary>
    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is not Slider slider) return;

        if (_viewModel.SeekCommand.CanExecute(slider.Value))
        {
            _viewModel.SeekCommand.Execute(slider.Value);
        }
    }
}
