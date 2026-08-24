using System.Text;
using HyperWhisper.SharedCore;

namespace HyperWhisper.SpeechOutput;

public enum PortablePostProcessingMode
{
    Off = 0,
    Cloud = 1,
    Local = 2,
}

public sealed record PortableVocabularyReplacement(string Word, string Replacement);

/// <summary>
/// Output-only options. Punctuation, capitalization, and profanity filtering
/// are provider/prompt intents on Windows; they are retained for routing and
/// diagnostics but deliberately do not trigger lossy local rewriting.
/// </summary>
public sealed record SpeechOutputProcessingOptions(
    bool RemoveFillerWords = true,
    bool RemoveTrailingPeriod = false,
    bool AppendTrailingSpace = true,
    bool AutocapitalizeInsert = false,
    bool Punctuation = true,
    bool Capitalization = true,
    bool ProfanityFilter = false);

public sealed record SpeechOutputProcessingRequest(
    string Text,
    string Language,
    PortablePostProcessingMode PostProcessingMode,
    IReadOnlyList<PortableVocabularyReplacement> GlobalVocabulary,
    IReadOnlyList<PortableVocabularyReplacement> ModeVocabulary,
    SpeechOutputProcessingOptions Options,
    PortableCursorContext CursorContext = PortableCursorContext.Unknown);

public sealed record SpeechOutputProcessingResult(
    string TranscriptText,
    string InjectionText,
    bool FillerWordsChanged,
    bool VoiceCommandsChanged,
    int VocabularyRulesChanged,
    bool TrailingPeriodChanged,
    bool AutocapitalizationChanged,
    bool TrailingSpacingChanged,
    bool PunctuationRequested,
    bool CapitalizationRequested,
    bool ProfanityFilterRequested)
{
    public string Text => InjectionText;
}

/// <summary>
/// Portable final-output pipeline mirroring the Windows batch order:
/// lightweight off-mode cleanup, vocabulary, trailing-period removal,
/// cursor-aware autocapitalization, then language-aware trailing spacing.
/// </summary>
public static class SpeechOutputProcessor
{
    public static SpeechOutputProcessingResult Process(SpeechOutputProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        ArgumentNullException.ThrowIfNull(request.Options);
        if (!Enum.IsDefined(request.PostProcessingMode))
            throw new ArgumentOutOfRangeException(nameof(request), "The post-processing mode is invalid.");
        if (!Enum.IsDefined(request.CursorContext))
            throw new ArgumentOutOfRangeException(nameof(request), "The cursor context is invalid.");

        var text = request.Text;
        var fillerChanged = false;
        var commandsChanged = false;
        if (request.PostProcessingMode == PortablePostProcessingMode.Off)
        {
            if (request.Options.RemoveFillerWords)
            {
                var withoutFillers = SharedCoreBridge.RemoveFillerWords(text, request.Language);
                fillerChanged = !string.Equals(withoutFillers, text, StringComparison.Ordinal);
                text = withoutFillers;
            }

            var withCommands = SharedCoreBridge.ProcessVoiceCommands(text);
            commandsChanged = !string.Equals(withCommands, text, StringComparison.Ordinal);
            text = withCommands;
        }

        var vocabularyRulesChanged = 0;
        text = text.Normalize(NormalizationForm.FormC);
        foreach (var entry in EnumerateVocabulary(request))
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Word) || string.IsNullOrWhiteSpace(entry.Replacement))
                continue;
            var replaced = SharedCoreBridge.ApplyHardenedReplacement(
                text,
                entry.Word.Normalize(NormalizationForm.FormC),
                entry.Replacement);
            if (!string.Equals(replaced, text, StringComparison.Ordinal)) vocabularyRulesChanged++;
            text = replaced;
        }
        text = text.Trim();
        var transcriptText = text;

        var beforeTrailingPeriod = text;
        if (request.Options.RemoveTrailingPeriod)
            text = SharedCoreBridge.RemoveTrailingPeriod(text);
        var trailingPeriodChanged = !string.Equals(text, beforeTrailingPeriod, StringComparison.Ordinal);

        var beforeAutocapitalization = text;
        if (request.Options.AutocapitalizeInsert)
            text = SharedCoreBridge.ApplyAutocapitalize(text, request.CursorContext);
        var autocapitalizationChanged = !string.Equals(text, beforeAutocapitalization, StringComparison.Ordinal);

        var beforeSpacing = text;
        if (request.Options.AppendTrailingSpace)
            text = SharedCoreBridge.AppendTrailingSpace(text, request.Language ?? string.Empty);
        var trailingSpacingChanged = !string.Equals(text, beforeSpacing, StringComparison.Ordinal);

        return new SpeechOutputProcessingResult(
            transcriptText,
            text,
            fillerChanged,
            commandsChanged,
            vocabularyRulesChanged,
            trailingPeriodChanged,
            autocapitalizationChanged,
            trailingSpacingChanged,
            request.Options.Punctuation,
            request.Options.Capitalization,
            request.Options.ProfanityFilter);
    }

    private static IEnumerable<PortableVocabularyReplacement?> EnumerateVocabulary(SpeechOutputProcessingRequest request)
    {
        foreach (var entry in request.GlobalVocabulary ?? []) yield return entry;
        foreach (var entry in request.ModeVocabulary ?? []) yield return entry;
    }

}
