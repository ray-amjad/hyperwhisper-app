using System.Collections.ObjectModel;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Data.Entities;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class VocabularyViewModel : ViewModelBase
{
    private readonly VocabularyRepository _repository;
    private string _word = string.Empty;
    private string _replacement = string.Empty;
    private VocabularyItem? _selected;
    private string _transferPath = string.Empty;
    public VocabularyViewModel(VocabularyRepository repository)
    {
        _repository = repository;
        AddCommand = new AsyncCommand(_ => SaveAsync());
        DeleteCommand = new AsyncCommand(item => DeleteAsync(item as VocabularyItem), item => item is VocabularyItem);
        ImportCommand = new AsyncCommand(_ => ImportAsync());
        ExportCommand = new AsyncCommand(_ => ExportAsync());
    }
    public ObservableCollection<VocabularyItem> Items { get; } = new();
    public string Word { get => _word; set { if (Set(ref _word, value)) ClearSubmitError(); } }
    public string Replacement { get => _replacement; set { if (Set(ref _replacement, value)) ClearSubmitError(); } }
    public VocabularyItem? Selected
    {
        get => _selected;
        set
        {
            // Loading a row always reloads the pill, even the row already selected: a failed
            // save leaves Selected on it, and editing it again restores its word (#634).
            if (!Set(ref _selected, value) && value is null) return;
            ClearSubmitError();
            if (value is not null) { Word = value.Word; Replacement = value.Replacement ?? string.Empty; }
        }
    }
    public string TransferPath { get => _transferPath; set => Set(ref _transferPath, value); }

    /// <summary>
    /// Only the first hundred terms reach a provider's keyword boost, so the page shows the same
    /// banner Windows shows once the list is longer than that. The constant is the Windows
    /// VocabularyPage.MaxKeywords.
    /// </summary>
    public const int MaxKeywords = 100;
    public bool HasKeywordLimitWarning => Items.Count > MaxKeywords;
    public string KeywordLimitMessage =>
        $"You have {Items.Count} vocabulary items. Only the first {MaxKeywords} will be sent for boosting.";

    /// <summary>The inline error line under the input pill; blank while nothing has failed.</summary>
    public string InputError => Status.HasError ? Status.Message : string.Empty;
    public bool HasInputError => Status.HasError;

    public UiStatus Status { get; } = new();
    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try { Items.Clear(); foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item); Status.Success(Plural.Count(Items.Count, "term", "terms")); }
        catch (Exception) { Status.Failure("vocabulary.load_failed", "Could not load vocabulary."); }
        NotifyDerived();
    }

    private void NotifyDerived()
    {
        Notify(nameof(HasKeywordLimitWarning));
        Notify(nameof(KeywordLimitMessage));
        Notify(nameof(InputError));
        Notify(nameof(HasInputError));
    }
    /// <summary>
    /// Issue #634: a submit error describes the text that was submitted, so it goes once that
    /// text is edited or another row is loaded into the pill — what Windows does
    /// (VocabularyPage.xaml.cs ClearError). Load, delete and transfer errors stay.
    /// </summary>
    private void ClearSubmitError()
    {
        if (Status.ErrorCode is not ("vocabulary.word_required" or "vocabulary.duplicate" or "vocabulary.add_failed")) return;
        Status.Clear();
        NotifyDerived();
    }
    public Task AddAsync(CancellationToken cancellationToken = default) => SaveAsync(cancellationToken);
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        TransferMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Word)) { Status.Failure("vocabulary.word_required", "Enter a word or phrase."); NotifyDerived(); return; }
        // Issue #1777: a pasted multi-line text is one word per non-empty line. The first line
        // takes the row being edited (if any); every line carries the same replacement, which
        // may itself span lines.
        var lines = VocabularyWords.SplitLines(Word);
        var replacement = string.IsNullOrWhiteSpace(Replacement) ? null : Replacement.Trim();
        try
        {
            // Issue #633: the store merges a word it already holds (any case), which moved the row,
            // wiped its replacement or rewrote its spelling. A word that another row holds, or that
            // an earlier pasted line holds, is refused, nothing is written, and the input stays.
            // The row being edited may keep or re-case its own word. Import still merges.
            var taken = new HashSet<string>(
                (await _repository.ListAsync(cancellationToken)).Where(item => item.Id != Selected?.Id).Select(item => item.Word.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (lines.FirstOrDefault(line => !taken.Add(line)) is { } duplicate)
            {
                Status.Failure("vocabulary.duplicate", $"\"{duplicate}\" is already in your vocabulary.");
                NotifyDerived();
                return;
            }
            for (var index = 0; index < lines.Count; index++)
            {
                var item = index == 0 && Selected is { } selected ? selected : new VocabularyItem { SortOrder = Items.Count + index };
                item.Word = lines[index]; item.Replacement = replacement;
                _ = await _repository.UpsertAsync(item, cancellationToken);
            }
            await RefreshAsync(cancellationToken); Selected = null; Word = string.Empty; Replacement = string.Empty; Status.Success("Vocabulary saved");
        }
        catch (Exception) { Status.Failure("vocabulary.add_failed", "Could not add the vocabulary item."); }
        NotifyDerived();
    }
    public async Task DeleteAsync(VocabularyItem? item, CancellationToken cancellationToken = default)
    {
        TransferMessage = string.Empty;
        if (item == null) { Status.Failure("vocabulary.no_selection", "Select a vocabulary item to delete."); return; }
        try { if (await _repository.DeleteAsync(item.Id, cancellationToken)) { Items.Remove(item); Status.Success("Vocabulary deleted"); } else Status.Failure("vocabulary.not_found", "The vocabulary item no longer exists."); }
        catch (Exception) { Status.Failure("vocabulary.delete_failed", "Could not delete the vocabulary item."); }
        NotifyDerived();
    }

    /// <summary>
    /// The page's localizer, set by the head: it turns a catalog key into the head's text, or
    /// null when the key is missing. Unset, the English text is used.
    /// </summary>
    public Func<string, string?>? Localize { get; set; }

    private string _transferMessage = string.Empty;

    /// <summary>The result line of the last import or export; blank until one succeeds (#2409).</summary>
    public string TransferMessage
    {
        get => _transferMessage;
        private set { if (Set(ref _transferMessage, value)) Notify(nameof(HasTransferMessage)); }
    }

    public bool HasTransferMessage => TransferMessage.Length > 0;

    /// <summary>What the last successful import did; null before one.</summary>
    public VocabularyImportResult? LastImport { get; private set; }

    private string Text(string key, string english, params object?[] arguments)
    {
        var template = Localize?.Invoke(key);
        if (string.IsNullOrEmpty(template)) template = english;
        try { return arguments.Length == 0 ? template : string.Format(System.Globalization.CultureInfo.CurrentCulture, template, arguments); }
        catch (FormatException) { return string.Format(System.Globalization.CultureInfo.InvariantCulture, english, arguments); }
    }

    /// <summary>
    /// Imports a CSV or TXT file (#2409; the extension picks the format) and merges it into the
    /// list: new words are appended in file order, a listed word keeps its spelling and position
    /// and takes the file's replacement only when the file gives one. A file that is unreadable or
    /// not UTF-8 shows the error line and writes nothing.
    /// </summary>
    public async Task ImportAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        path ??= TransferPath;
        TransferMessage = string.Empty;
        if (!Path.IsPathFullyQualified(path)) { Status.Failure("vocabulary.path_required", "Choose a vocabulary file."); NotifyDerived(); return; }
        VocabularyTransferParse parsed;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            parsed = VocabularyTransfer.Read(VocabularyTransfer.FormatForPath(path), VocabularyTransfer.Decode(bytes));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or VocabularyTransferException)
        {
            Status.Failure("vocabulary.import_unreadable", Text("vocabulary.transfer.importFailed", "Could not read this file. Choose a UTF-8 CSV or TXT file."));
            NotifyDerived();
            return;
        }
        try
        {
            var result = await _repository.ImportAsync(parsed, cancellationToken);
            await RefreshAsync(cancellationToken);
            LastImport = result;
            Status.Success("Vocabulary imported");
            TransferMessage = Text("vocabulary.transfer.imported",
                "Vocabulary imported. Added: {0}, updated: {1}, skipped (over 1000 characters): {2}.",
                result.Added, result.Updated, result.Skipped);
        }
        catch (Exception) { Status.Failure("vocabulary.import_failed", Text("vocabulary.transfer.importFailed", "Could not read this file. Choose a UTF-8 CSV or TXT file.")); }
        NotifyDerived();
    }

    /// <summary>
    /// Exports the list in the page's order as CSV, or as TXT when the path ends in .txt (#2409).
    /// The file is written beside its target and moved over it, so a failed write leaves any
    /// earlier file whole.
    /// </summary>
    public async Task ExportAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        path ??= TransferPath;
        TransferMessage = string.Empty;
        if (!Path.IsPathFullyQualified(path)) { Status.Failure("vocabulary.path_required", "Choose an export file."); NotifyDerived(); return; }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var entries = VocabularyTransfer.ToEntries(await _repository.ListAsync(cancellationToken));
            var text = VocabularyTransfer.Write(VocabularyTransfer.FormatForPath(path), entries);
            await File.WriteAllBytesAsync(temporary, VocabularyTransfer.Encode(text), cancellationToken);
            File.Move(temporary, path, overwrite: true);
            Status.Success("Vocabulary exported");
            TransferMessage = Text("vocabulary.transfer.exported", "Vocabulary exported to {0}.", Path.GetFileName(path));
        }
        catch (Exception)
        {
            try { File.Delete(temporary); } catch (Exception) { /* best effort */ }
            Status.Failure("vocabulary.export_failed", Text("vocabulary.transfer.exportFailed", "Could not export the vocabulary."));
        }
        NotifyDerived();
    }
}
