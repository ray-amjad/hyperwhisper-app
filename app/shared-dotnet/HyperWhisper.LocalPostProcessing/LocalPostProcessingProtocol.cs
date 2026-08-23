namespace HyperWhisper.LocalPostProcessing;

/// <summary>
/// The local post-processing wire contract shared with the Windows host.
/// Prompt construction keeps dynamic context in the user message so the static
/// system prompt remains cacheable, and response handling mirrors hw-text's
/// lenient completion evaluation while rejecting prompt scaffolding.
/// </summary>
public static class LocalPostProcessingProtocol
{
    private static readonly string[] StartMarkers =
        ["<<CLEANED>>", "<<CLEANED>", "<CLEANED>>", "<CLEANED>", "<</CLEANED>>"];
    private static readonly string[] EndMarkers =
        ["<<END>>", "<<END>", "<END>>", "<END>", "<</END>>"];
    private static readonly string[] PromptMarkers =
        ["--TRANSCRIPT--", "--ENDTRANSCRIPT--", "<MODE_FLAGS>", "</MODE_FLAGS>",
         "<USER_SYSTEM_PROMPT>", "</USER_SYSTEM_PROMPT>", "<SYSTEM_INFO>", "</SYSTEM_INFO>",
         "<APPLICATION_CONTEXT>", "</APPLICATION_CONTEXT>", "<SCREEN_CONTEXT>", "</SCREEN_CONTEXT>",
         "<CUSTOM_VOCABULARY>", "</CUSTOM_VOCABULARY>",
         "<LANGUAGE_REQUIREMENTS>", "</LANGUAGE_REQUIREMENTS>"];

    public static string WrapTranscript(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return $"--TRANSCRIPT--\n{transcript}\n--ENDTRANSCRIPT--";
    }

    public static string BuildUserMessage(string dynamicSystemInfo, string transcript)
    {
        ArgumentNullException.ThrowIfNull(dynamicSystemInfo);
        ArgumentNullException.ThrowIfNull(transcript);
        return dynamicSystemInfo + "\n\n" + WrapTranscript(transcript);
    }

    public static bool TryEvaluateCompletion(string completion, out string cleaned)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var start = FindEarliest(completion, StartMarkers, 0);
        if (start is { } found)
        {
            var contentStart = found.Index + found.Length;
            var end = FindEarliest(completion, EndMarkers, contentStart);
            cleaned = completion[contentStart..(end?.Index ?? completion.Length)];
            cleaned = StripAll(StripAll(cleaned, StartMarkers), EndMarkers).Trim();
        }
        else
        {
            cleaned = StripAll(completion, EndMarkers).Trim();
        }

        var candidate = cleaned;
        if (candidate.Length == 0 || PromptMarkers.Any(marker =>
                candidate.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            cleaned = string.Empty;
            return false;
        }

        return true;
    }

    private static (int Index, int Length)? FindEarliest(
        string text,
        IReadOnlyList<string> markers,
        int startIndex)
    {
        (int Index, int Length)? best = null;
        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, startIndex, StringComparison.Ordinal);
            if (index >= 0 && (best is null || index < best.Value.Index))
            {
                best = (index, marker.Length);
            }
        }
        return best;
    }

    private static string StripAll(string value, IReadOnlyList<string> markers)
    {
        foreach (var marker in markers)
        {
            value = value.Replace(marker, string.Empty, StringComparison.Ordinal);
        }
        return value;
    }
}
