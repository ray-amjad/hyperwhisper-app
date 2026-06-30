using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;

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

                // Case-insensitive duplicate check (exclude item being edited)
                var lowerWord = trimmedWord.ToLower();
                var query = context.VocabularyItems.Where(v => v.Word.ToLower() == lowerWord);
                if (excludeId.HasValue)
                    query = query.Where(v => v.Id != excludeId.Value);
                var exists = query.Any();

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

}
