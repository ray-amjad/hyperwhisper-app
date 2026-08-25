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
    public string Word { get => _word; set => Set(ref _word, value); }
    public string Replacement { get => _replacement; set => Set(ref _replacement, value); }
    public VocabularyItem? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value) && value is not null) { Word = value.Word; Replacement = value.Replacement ?? string.Empty; } }
    }
    public string TransferPath { get => _transferPath; set => Set(ref _transferPath, value); }
    public UiStatus Status { get; } = new();
    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try { Items.Clear(); foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item); Status.Success($"{Items.Count} term(s)"); }
        catch (Exception) { Status.Failure("vocabulary.load_failed", "Could not load vocabulary."); }
    }
    public Task AddAsync(CancellationToken cancellationToken = default) => SaveAsync(cancellationToken);
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Word)) { Status.Failure("vocabulary.word_required", "Enter a word or phrase."); return; }
        var item = Selected ?? new VocabularyItem { SortOrder = Items.Count };
        item.Word = Word.Trim(); item.Replacement = string.IsNullOrWhiteSpace(Replacement) ? null : Replacement.Trim();
        try { _ = await _repository.UpsertAsync(item, cancellationToken); await RefreshAsync(cancellationToken); Selected = null; Word = string.Empty; Replacement = string.Empty; Status.Success("Vocabulary saved"); }
        catch (Exception) { Status.Failure("vocabulary.add_failed", "Could not add the vocabulary item."); }
    }
    public async Task DeleteAsync(VocabularyItem? item, CancellationToken cancellationToken = default)
    {
        if (item == null) { Status.Failure("vocabulary.no_selection", "Select a vocabulary item to delete."); return; }
        try { if (await _repository.DeleteAsync(item.Id, cancellationToken)) { Items.Remove(item); Status.Success("Vocabulary deleted"); } else Status.Failure("vocabulary.not_found", "The vocabulary item no longer exists."); }
        catch (Exception) { Status.Failure("vocabulary.delete_failed", "Could not delete the vocabulary item."); }
    }

    public async Task ImportAsync(CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(TransferPath)) { Status.Failure("vocabulary.path_required", "Choose a vocabulary file."); return; }
        try
        {
            var lines = await File.ReadAllLinesAsync(TransferPath, cancellationToken);
            var items = lines.Select((line, index) => line.Split('\t', 2) switch
            {
                var fields => new VocabularyItem { Word = fields[0].Trim(), Replacement = fields.Length > 1 ? fields[1].Trim() : null, SortOrder = index }
            }).Where(item => item.Word.Length > 0);
            var count = await _repository.MergeAsync(items, cancellationToken);
            await RefreshAsync(cancellationToken);
            Status.Success($"Imported {count} term(s); duplicates merged");
        }
        catch (Exception) { Status.Failure("vocabulary.import_failed", "Could not import vocabulary."); }
    }

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(TransferPath)) { Status.Failure("vocabulary.path_required", "Choose an export file."); return; }
        try
        {
            var lines = (await _repository.ListAsync(cancellationToken)).Select(item => $"{item.Word}\t{item.Replacement ?? string.Empty}");
            await File.WriteAllLinesAsync(TransferPath, lines, cancellationToken);
            Status.Success("Vocabulary exported");
        }
        catch (Exception) { Status.Failure("vocabulary.export_failed", "Could not export vocabulary."); }
    }
}
