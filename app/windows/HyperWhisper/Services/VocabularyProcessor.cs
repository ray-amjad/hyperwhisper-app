using System.Linq;
using System.Text.RegularExpressions;

namespace HyperWhisper.Services;

/// <summary>
/// VOCABULARY PROCESSOR
/// Applies replacement values from vocabulary after transcription/post-processing.
/// Mirrors macOS behavior: used after AI post-processing when enabled.
/// </summary>
public class VocabularyProcessor
{
    private Dictionary<string, Regex>? _regexCache;
    private string _lastVocabFingerprint = "";

    /// <summary>
    /// Applies replacements to the provided text using global vocabulary entries.
    /// Only entries with a non-empty replacement are applied.
    /// Compiled Regex patterns are cached and rebuilt when vocabulary changes.
    /// </summary>
    public string ApplyReplacements(string text)
    {
        var vocab = VocabularyService.Instance.GetAll()
            .Where(v => !string.IsNullOrWhiteSpace(v.Word) && !string.IsNullOrWhiteSpace(v.Replacement))
            .ToList();

        if (vocab.Count == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Rebuild cache if vocabulary changed (fingerprint includes all words+replacements)
        var vocabFingerprint = string.Join("|", vocab.Select(v => $"{v.Word}={v.Replacement}"));
        if (_regexCache == null || _lastVocabFingerprint != vocabFingerprint)
        {
            _regexCache = new Dictionary<string, Regex>();
            foreach (var entry in vocab)
            {
                var word = entry.Word.Trim();
                if (word.Length > 0 && !_regexCache.ContainsKey(word))
                {
                    var pattern = $@"\b{Regex.Escape(word)}\b";
                    _regexCache[word] = new Regex(pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                }
            }
            _lastVocabFingerprint = vocabFingerprint;
        }

        var processed = text;

        foreach (var entry in vocab)
        {
            var word = entry.Word.Trim();
            var replacement = entry.Replacement!.Trim();

            if (word.Length == 0 || replacement.Length == 0) continue;

            if (_regexCache.TryGetValue(word, out var regex))
            {
                var updated = regex.Replace(processed, replacement);

                if (!ReferenceEquals(updated, processed))
                {
                    LoggingService.Debug($"VocabularyProcessor: Replaced '{word}' -> '{replacement}'");
                }

                processed = updated;
            }
        }

        return processed.Trim();
    }
}
