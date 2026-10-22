using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HyperWhisper.Data.Entities;
using VocabularyWords = HyperWhisper.PortableApplication.Persistence.VocabularyWords;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.PortableApplication.Persistence;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

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
        ClearError();
        UpdateActionChipsEnabled();
    }

    private void ReplacementBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ReplacementPlaceholder.Visibility = string.IsNullOrEmpty(ReplacementBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearError();
        UpdateActionChipsEnabled();
    }

    // A submit error describes the text that was submitted; once the user
    // edits that text or starts editing another row, it no longer applies.
    private void ClearError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
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

        // Issue #1777: a pasted multi-line text becomes one word per non-empty line.
        // Each line gets the usual empty and duplicate checks, and the replacement
        // (which may span lines) goes with every line. Blank text stays one entry,
        // so TryAdd still answers "Word cannot be empty."
        var lines = VocabularyWords.SplitLines(word);
        if (lines.Count == 0)
            lines = [word];
        var refused = new List<(string Line, string? Error)>();
        foreach (var line in lines)
        {
            if (!_vocabularyService.TryAdd(line, replacement, out var lineError, excludeId: _editingId, source: source))
                refused.Add((line, lineError));
        }

        if (refused.Count == lines.Count)
        {
            // Nothing was added: the field keeps its text, as before.
            ErrorText.Text = lines.Count == 1
                ? (refused.FirstOrDefault().Error ?? "Unable to add word.")
                : RefusedLinesMessage(refused);
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

        ClearError();
        WordBox.Text = string.Empty;
        ReplacementBox.Text = string.Empty;
        _showingReplacementField = false;
        ReplacementBorder.Visibility = Visibility.Collapsed;
        ResetEditMode();
        UpdateActionChipsEnabled();
        WordBox.Focus();
        RefreshList();

        // Some lines of a paste can be refused while others are added; name only the
        // refused ones. Set after the field is cleared, whose TextChanged hides the error.
        if (refused.Count > 0)
        {
            ErrorText.Text = RefusedLinesMessage(refused);
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private static string RefusedLinesMessage(List<(string Line, string? Error)> refused) =>
        $"Not added: {string.Join(", ", refused.Select(r => r.Line))}. {refused[0].Error ?? "Unable to add word."}";

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not Guid id) return;

        var item = _vocabularyService.GetAll().FirstOrDefault(v => v.Id == id);
        if (item == null) return;

        ClearError();
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
        ClearError();
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
        ClearError();
        RefreshList();
    }

    // =========================================================================
    // IMPORT / EXPORT (#2409)
    // =========================================================================

    // CSV and TXT, picked by the extension; the patterns are not translated.
    private static string ImportFilter() =>
        $"{Loc.S("vocabulary.transfer.fileType.any")}|*.csv;*.txt|{Loc.S("vocabulary.transfer.fileType.csv")}|*.csv|{Loc.S("vocabulary.transfer.fileType.txt")}|*.txt";

    private static string ExportFilter() =>
        $"{Loc.S("vocabulary.transfer.fileType.csv")}|*.csv|{Loc.S("vocabulary.transfer.fileType.txt")}|*.txt";

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.S("vocabulary.transfer.import.dialogTitle"),
            Filter = ImportFilter(),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        var filePath = dialog.FileName;
        SetTransferBusy(true);
        ShowTransferStatus(null);
        // The service raises VocabularyChanged, which reloads the list and the keyword banner.
        var result = await Task.Run(() => _vocabularyService.ImportFile(filePath));
        SetTransferBusy(false);

        if (result.IsFailure)
        {
            ShowTransferError(Loc.S("vocabulary.transfer.importFailed"));
            return;
        }
        var counts = result.Value!;
        RefreshList();
        ShowTransferStatus(Loc.S("vocabulary.transfer.imported", counts.Added, counts.Updated, counts.Skipped));
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.S("vocabulary.transfer.export.dialogTitle"),
            Filter = ExportFilter(),
            FileName = VocabularyTransfer.DefaultFileName,
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        var filePath = dialog.FileName;
        SetTransferBusy(true);
        ShowTransferStatus(null);
        var result = await Task.Run(() => _vocabularyService.ExportFile(filePath));
        SetTransferBusy(false);

        if (result.IsFailure)
        {
            ShowTransferError(Loc.S("vocabulary.transfer.exportFailed"));
            return;
        }
        ShowTransferStatus(Loc.S("vocabulary.transfer.exported", Path.GetFileName(filePath)));
    }

    private void SetTransferBusy(bool busy)
    {
        ImportButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
    }

    private void ShowTransferStatus(string? message)
    {
        TransferStatusText.Text = message ?? string.Empty;
        TransferStatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(message)) ClearError();
    }

    // The page's existing error line, as a refused add uses.
    private void ShowTransferError(string message)
    {
        ShowTransferStatus(null);
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
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
