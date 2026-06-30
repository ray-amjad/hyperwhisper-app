using System;

namespace HyperWhisper.Data.Entities;

/// <summary>
/// VOCABULARY ITEM
///
/// Represents a custom word/phrase with an optional replacement.
/// Mirrors the macOS Core Data Vocabulary entity for cross-platform parity.
/// Stored in the SQLite database at %LOCALAPPDATA%\HyperWhisper\hyperwhisper.db.
/// </summary>
public class VocabularyItem
{
    /// <summary>Unique identifier for the vocabulary entry.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Word or phrase to recognize/boost.</summary>
    public string Word { get; set; } = string.Empty;

    /// <summary>
    /// Optional replacement to apply after transcription/post-processing.
    /// If null/empty, the word is only used for context boosting.
    /// </summary>
    public string? Replacement { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>Sort order for stable ordering in lists.</summary>
    public int SortOrder { get; set; }

    /// <summary>Optional source marker, e.g. manual or auto-learn.</summary>
    public string? Source { get; set; }
}
