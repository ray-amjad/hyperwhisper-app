namespace HyperWhisper.WhisperWorker;

/// <summary>
/// How a Windows on-device Whisper request carries the vocabulary (issue #1564).
/// Ray's rule (2026-10-19), the same as macOS (<c>LibWhisper.maxTextContext</c> and
/// <c>LibWhisperProvider.vocabularyPrompt</c>): a recording of up to 30 s with
/// vocabulary gets it as the initial prompt; anything longer keeps the #1560
/// repetition-loop guard, <c>WithMaxLastTextTokens(0)</c>, and gets no prompt.
/// </summary>
/// <remarks>
/// whisper.cpp primes each 30 s window with the initial prompt and the text decoded
/// so far through one carried-text channel, and <c>WithMaxLastTextTokens(0)</c>
/// closes that channel. So the two cannot be on together: the prompt would be
/// dropped. A recording of one window has no earlier window's text to carry, so the
/// prompt is safe there.
/// </remarks>
public static class WhisperVocabularyPrompt
{
    /// <summary>Whisper's input rate.</summary>
    public const int SampleRate = 16000;

    /// <summary>Recordings up to this many samples (30 s, one Whisper window) may carry the prompt.</summary>
    public const long CarryOverSampleLimit = 30L * SampleRate;

    /// <summary>
    /// The prompt for a vocabulary: the words, trimmed, without empty ones,
    /// space-joined; null when none is left. The callers already pass only words
    /// with no replacement (<c>VocabularyService.GetVocabularyWords</c>) or a mode's
    /// own word list.
    /// </summary>
    public static string? Build(IEnumerable<string?>? words)
    {
        if (words is null) return null;
        var kept = new List<string>();
        foreach (var word in words)
        {
            var trimmed = word?.Trim();
            if (!string.IsNullOrEmpty(trimmed)) kept.Add(trimmed);
        }
        return kept.Count == 0 ? null : string.Join(' ', kept);
    }

    /// <summary>
    /// The prompt and the loop guard for a recording of <paramref name="sampleCount"/>
    /// 16 kHz samples. Over 30 s: no prompt, guard on. Up to 30 s with vocabulary:
    /// the prompt, guard off. Up to 30 s with none: no prompt, guard on, as before.
    /// </summary>
    public static WhisperPromptDecision Decide(long sampleCount, IEnumerable<string?>? vocabulary)
    {
        var prompt = sampleCount > CarryOverSampleLimit ? null : Build(vocabulary);
        return new WhisperPromptDecision(prompt, MaxLastTextTokensZero: prompt is null);
    }
}

/// <summary>What <see cref="WhisperVocabularyPrompt.Decide"/> chose for one recording.</summary>
public readonly record struct WhisperPromptDecision(string? Prompt, bool MaxLastTextTokensZero);
