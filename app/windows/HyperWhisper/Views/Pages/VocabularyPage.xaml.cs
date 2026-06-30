using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Pages;

public partial class VocabularyPage : Page
{
    private readonly VocabularyService _vocabularyService;
    private ObservableCollection<VocabularyItemRow> _items = new();
    private const int MaxKeywords = 100;

    private Guid? _pendingDeleteId;
    private Guid? _editingId;
    private string? _editingSource;
    private bool _showingReplacementField;
    private readonly bool _compactSettingsLayout;

    public VocabularyPage()
        : this(compactSettingsLayout: false)
    {
    }

    public VocabularyPage(bool compactSettingsLayout)
    {
        _compactSettingsLayout = compactSettingsLayout;
        InitializeComponent();
        _vocabularyService = VocabularyService.Instance;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootStack.MaxWidth = _compactSettingsLayout ? 560 : 900;
        _vocabularyService.VocabularyChanged -= OnVocabularyChanged;
        _vocabularyService.VocabularyChanged += OnVocabularyChanged;
        RefreshList();
        UpdateActionChipsEnabled();
        WordBox.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vocabularyService.VocabularyChanged -= OnVocabularyChanged;
    }

    private void OnVocabularyChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RefreshList);

    private void RefreshList()
    {
        var items = _vocabularyService.GetAll();

        // Filter out pending delete item during edit
        if (_pendingDeleteId.HasValue)
            items = items.Where(i => i.Id != _pendingDeleteId.Value).ToList();

        _items = new ObservableCollection<VocabularyItemRow>(items.Select(i => new VocabularyItemRow(i)));
        VocabularyList.ItemsSource = _items;

        var isEmpty = items.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        HeaderRow.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        VocabularyList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

        // Use total count (including hidden item) for the warning
        var totalCount = _vocabularyService.GetAll().Count;
        if (totalCount > MaxKeywords)
        {
            WarningBanner.Visibility = Visibility.Visible;
            WarningText.Text = $"You have {totalCount} vocabulary items. Only the first {MaxKeywords} will be sent for boosting.";
        }
        else
        {
            WarningBanner.Visibility = Visibility.Collapsed;
        }
    }

    // Focus-scoped: only fires while WordBox has keyboard focus. See
    // ShortcutsSettingsPage.xaml.cs for the reference PreviewKeyDown pattern.
    private void WordBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (e.Key == Key.Enter && ctrl)
        {
            e.Handled = true;
            OpenReplacementField();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddVocabularyItem();
        }
        else if (e.Key == Key.Escape && _showingReplacementField)
        {
            e.Handled = true;
            CancelReplacementField();
        }
    }

    private void ReplacementBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (e.Key == Key.Enter && ctrl)
        {
            e.Handled = true;
            AddVocabularyItem();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelReplacementField();
        }
    }

    private void WordBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        WordPlaceholder.Visibility = string.IsNullOrEmpty(WordBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateActionChipsEnabled();
    }

    private void ReplacementBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ReplacementPlaceholder.Visibility = string.IsNullOrEmpty(ReplacementBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateActionChipsEnabled();
    }

    private void UpdateActionChipsEnabled()
    {
        var hasWord = !string.IsNullOrEmpty(WordBox.Text);
        AddWordChip.IsEnabled = hasWord;
        ReplaceWithChip.IsEnabled = hasWord && !_showingReplacementField;
        ReplaceChip.IsEnabled = hasWord && !string.IsNullOrEmpty(ReplacementBox.Text);
    }

    private void AddWordChip_Click(object sender, RoutedEventArgs e) => AddVocabularyItem();

    private void ReplaceWithChip_Click(object sender, RoutedEventArgs e) => OpenReplacementField();

    private void ReplaceChip_Click(object sender, RoutedEventArgs e) => AddVocabularyItem();

    private void OpenReplacementField()
    {
        if (string.IsNullOrEmpty(WordBox.Text)) return;
        _showingReplacementField = true;
        ReplacementBorder.Visibility = Visibility.Visible;
        UpdateActionChipsEnabled();
        Dispatcher.BeginInvoke(new Action(() => ReplacementBox.Focus()), DispatcherPriority.Render);
    }

    private void CancelReplacementField()
    {
        _showingReplacementField = false;
        ReplacementBox.Text = string.Empty;
        ReplacementBorder.Visibility = Visibility.Collapsed;
        UpdateActionChipsEnabled();
        WordBox.Focus();
    }

    private void AddVocabularyItem()
    {
        var word = WordBox.Text;
        if (string.IsNullOrEmpty(word)) return;
        var replacement = string.IsNullOrEmpty(ReplacementBox.Text) ? null : ReplacementBox.Text;

        var source = _editingId.HasValue ? _editingSource : "manual";
        if (!_vocabularyService.TryAdd(word, replacement, out var error, excludeId: _editingId, source: source))
        {
            ErrorText.Text = error ?? "Unable to add word.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Delete old item only after new item is successfully saved
        if (_pendingDeleteId.HasValue)
        {
            _vocabularyService.Delete(_pendingDeleteId.Value);
            _pendingDeleteId = null;
            _editingId = null;
            _editingSource = null;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        WordBox.Text = string.Empty;
        ReplacementBox.Text = string.Empty;
        _showingReplacementField = false;
        ReplacementBorder.Visibility = Visibility.Collapsed;
        ResetEditMode();
        UpdateActionChipsEnabled();
        WordBox.Focus();
        RefreshList();
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not Guid id) return;

        var item = _vocabularyService.GetAll().FirstOrDefault(v => v.Id == id);
        if (item == null) return;

        _pendingDeleteId = id;
        _editingId = id;
        _editingSource = item.Source;

        WordBox.Text = item.Word;
        ReplacementBox.Text = item.Replacement ?? "";

        // Auto-open the replacement field if the item being edited already
        // has a replacement, mirroring macOS.
        if (!string.IsNullOrEmpty(item.Replacement))
        {
            _showingReplacementField = true;
            ReplacementBorder.Visibility = Visibility.Visible;
        }
        else
        {
            _showingReplacementField = false;
            ReplacementBorder.Visibility = Visibility.Collapsed;
        }

        AddWordChipLabel.Text = Loc.S("common.update");
        CancelButton.Visibility = Visibility.Visible;

        RefreshList();
        UpdateActionChipsEnabled();
        InputSection.BringIntoView();
        WordBox.Focus();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingDeleteId = null;
        _editingId = null;
        _editingSource = null;
        WordBox.Text = string.Empty;
        ReplacementBox.Text = string.Empty;
        _showingReplacementField = false;
        ReplacementBorder.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        ResetEditMode();
        UpdateActionChipsEnabled();
        WordBox.Focus();
        RefreshList();
    }

    private void ResetEditMode()
    {
        AddWordChipLabel.Text = Loc.S("vocabulary.action.addWord");
        CancelButton.Visibility = Visibility.Collapsed;
        if (!_editingId.HasValue)
        {
            _editingSource = null;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not Guid id) return;

        _vocabularyService.Delete(id);
        RefreshList();
    }

    private sealed class VocabularyItemRow
    {
        public VocabularyItemRow(VocabularyItem item)
        {
            Id = item.Id;
            Word = item.Word;
            Replacement = item.Replacement;
            Source = item.Source;
            IsSourceVisible = !string.IsNullOrWhiteSpace(Source);
            SourceBadgeText = string.Equals(Source, "auto-learn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Source, "auto-learned", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Source, "autoLearned", StringComparison.OrdinalIgnoreCase)
                    ? Loc.S("vocabulary.autoLearned.badge")
                    : string.Equals(Source, "manual", StringComparison.OrdinalIgnoreCase)
                        ? Loc.S("vocabulary.manual.badge")
                    : Source ?? "";
        }

        public Guid Id { get; }
        public string Word { get; }
        public string? Replacement { get; }
        public string? Source { get; }
        public bool IsSourceVisible { get; }
        public string SourceBadgeText { get; }
    }
}
