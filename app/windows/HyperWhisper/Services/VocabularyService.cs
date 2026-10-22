using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.PortableApplication.Persistence;
using VocabularyWords = HyperWhisper.PortableApplication.Persistence.VocabularyWords;

namespace HyperWhisper.Services;

/// <summary>
/// VOCABULARY SERVICE
///
/// Manages global custom vocabulary (word + optional replacement).
/// Stored in SQLite database at %LOCALAPPDATA%\HyperWhisper\hyperwhisper.db.
/// Mirrors macOS behavior (global list, not per-mode).
/// </summary>
public class VocabularyService
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static VocabularyService? _instance;
    private static readonly object _lock = new();

    public static VocabularyService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new VocabularyService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // EVENTS
    // =========================================================================

    /// <summary>Event fired when vocabulary changes (add/delete/update).</summary>
    public event EventHandler? VocabularyChanged;

    /// <summary>
    /// Raises <see cref="VocabularyChanged"/>. Used by services that mutate the
    /// vocabulary table directly (e.g. BackupService merge) so the UI refreshes.
    /// </summary>
    public void NotifyVocabularyChanged()
    {
        VocabularyChanged?.Invoke(this, EventArgs.Empty);
    }

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private VocabularyService()
    {
        // EF Core database is initialized by DatabaseInitializer at app startup
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>Returns all vocabulary items sorted by word then sort order.</summary>
    public List<VocabularyItem> GetAll()
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();
            return context.VocabularyItems
                .OrderBy(v => v.Word)
                .ThenBy(v => v.SortOrder)
                .ToList();
        }
    }

    /// <summary>
    /// Returns the list of vocabulary words for prompt boosting (no replacements).
    /// Empty/whitespace words are filtered out.
    /// </summary>
    public List<string> GetVocabularyWords(int? maxCount = null)
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();

            var query = context.VocabularyItems
                .Where(v => string.IsNullOrWhiteSpace(v.Replacement))
                .Select(v => v.Word.Trim())
                .Where(w => w != null && w != "")
                .Distinct();

            if (maxCount.HasValue)
            {
                query = query.Take(maxCount.Value);
            }

            return query.ToList();
        }
    }

    /// <summary>
    /// Adds a new vocabulary item.
    /// Returns false if the word already exists (case-insensitive) or is empty.
    /// </summary>
    public bool TryAdd(string word, string? replacement, out string? error, Guid? excludeId = null, string? source = "manual")
    {
        error = null;
        if (string.IsNullOrWhiteSpace(word))
        {
            error = "Word cannot be empty.";
            return false;
        }

        var trimmedWord = word.Trim();

        // A word is one line (issue #1777). Callers split a multi-line value with
        // VocabularyWords.SplitLines first. The replacement may keep its line breaks:
        // a multi-line snippet is allowed.
        if (VocabularyWords.HasLineBreak(trimmedWord))
        {
            error = "A word must be one line.";
            return false;
        }

        var trimmedReplacement = string.IsNullOrWhiteSpace(replacement)
            ? null
            : replacement!.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source)
            ? "manual"
            : source.Trim();

        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                // Case-insensitive duplicate check (exclude item being edited).
                // Compared in memory: SQLite lower() folds ASCII only, so "Øresund"
                // would never match itself. Same key as BackupService's merge.
                var key = trimmedWord.ToLowerInvariant();
                var query = context.VocabularyItems.AsQueryable();
                if (excludeId.HasValue)
                    query = query.Where(v => v.Id != excludeId.Value);
                var exists = query
                    .Select(v => v.Word)
                    .AsEnumerable()
                    .Any(w => (w ?? string.Empty).Trim().ToLowerInvariant() == key);

                if (exists)
                {
                    error = "That word already exists.";
                    return false;
                }

                // Get next sort order
                var maxSortOrder = context.VocabularyItems.Any()
                    ? context.VocabularyItems.Max(v => v.SortOrder)
                    : -1;

                var item = new VocabularyItem
                {
                    Id = Guid.NewGuid(),
                    Word = trimmedWord,
                    Replacement = trimmedReplacement,
                    SortOrder = maxSortOrder + 1,
                    CreatedDate = DateTime.UtcNow,
                    Source = normalizedSource
                };

                context.VocabularyItems.Add(item);
                context.SaveChanges();

                LoggingService.Info($"VocabularyService: Added vocabulary '{trimmedWord}' (replacement={(!string.IsNullOrEmpty(trimmedReplacement)).ToString()})");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error("VocabularyService: Database error adding item", ex);
                error = "Database error occurred";
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Error("VocabularyService: Unexpected error adding item", ex);
                error = "An error occurred";
                return false;
            }
        }

        // Fire event outside lock to prevent deadlock
        VocabularyChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Deletes an item by ID. Returns true if deleted.</summary>
    public bool Delete(Guid id)
    {
        string? deletedWord = null;

        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                var item = context.VocabularyItems.Find(id);
                if (item == null)
                {
                    LoggingService.Warn($"VocabularyService: Item {id} not found");
                    return false;
                }

                deletedWord = item.Word; // Capture for logging
                context.VocabularyItems.Remove(item);
                context.SaveChanges();

                LoggingService.Info($"VocabularyService: Deleted vocabulary '{deletedWord}'");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error($"VocabularyService: Database error deleting {id}", ex);
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"VocabularyService: Unexpected error deleting {id}", ex);
                return false;
            }
        }

        // Fire event outside lock to prevent deadlock
        VocabularyChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // =========================================================================
    // IMPORT / EXPORT (#2409)
    // =========================================================================

    /// <summary>
    /// Reads a CSV or TXT vocabulary file (the extension picks the format) and merges it into
    /// the list with the shared rules in <see cref="VocabularyTransfer.MergeInto"/>: new words are
    /// appended in file order; a listed word keeps its spelling, position and source, and takes
    /// the file's replacement only when the file gives one. A file that cannot be read or is not
    /// UTF-8 fails before anything is written.
    /// </summary>
    public Result<VocabularyImportResult> ImportFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A file path is required.", nameof(filePath));

        VocabularyTransferParse parsed;
        try
        {
            var text = VocabularyTransfer.Decode(File.ReadAllBytes(filePath));
            parsed = VocabularyTransfer.Read(VocabularyTransfer.FormatForPath(filePath), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VocabularyTransferException or NotSupportedException)
        {
            LoggingService.Warn($"VocabularyService: Could not read the vocabulary file ({ex.GetType().Name})");
            return Result<VocabularyImportResult>.Failure("The file could not be read as UTF-8 text.", ex);
        }
        return Import(parsed);
    }

    /// <summary>Merges already-parsed rows; see <see cref="ImportFile"/>.</summary>
    public Result<VocabularyImportResult> Import(VocabularyTransferParse parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        VocabularyImportResult result;
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();
                result = VocabularyTransfer.MergeInto(context, parsed.Entries, parsed.Skipped);
                LoggingService.Info($"VocabularyService: Imported vocabulary (added={result.Added}, updated={result.Updated}, skipped={result.Skipped})");
            }
            catch (Exception ex)
            {
                LoggingService.Error("VocabularyService: Vocabulary import failed", ex);
                return Result<VocabularyImportResult>.Failure("The vocabulary could not be saved.", ex);
            }
        }

        // Outside the lock, as the other writers do; the page, the keyword banner and the
        // prompt/phonetic consumers all reload from this event.
        if (result.Added > 0 || result.Updated > 0)
            VocabularyChanged?.Invoke(this, EventArgs.Empty);
        return Result<VocabularyImportResult>.Success(result);
    }

    /// <summary>
    /// Writes the list in the page's display order (<see cref="GetAll"/>) as CSV, or as TXT when
    /// the path ends in .txt. The file is written beside its target and moved over it, so a
    /// failed export leaves any earlier file whole.
    /// </summary>
    public Result ExportFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A file path is required.", nameof(filePath));

        var temporary = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var entries = VocabularyTransfer.ToEntries(GetAll());
            var text = VocabularyTransfer.Write(VocabularyTransfer.FormatForPath(filePath), entries);
            File.WriteAllBytes(temporary, VocabularyTransfer.Encode(text));
            File.Move(temporary, filePath, overwrite: true);
            LoggingService.Info($"VocabularyService: Exported {entries.Count} vocabulary terms");
            return Result.Success();
        }
        catch (Exception ex)
        {
            try { File.Delete(temporary); } catch (Exception) { /* best effort */ }
            LoggingService.Error("VocabularyService: Vocabulary export failed", ex);
            return Result.Failure("The vocabulary could not be exported.", ex);
        }
    }

}
