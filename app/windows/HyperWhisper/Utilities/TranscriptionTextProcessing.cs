using uniffi.hyperwhisper_core;

namespace HyperWhisper.Utilities;

/// <summary>
/// Helpers for streaming transcript cleanup and voice commands. Thin wrappers
/// over the shared Rust core (hw-text) so the behavior can never drift from
/// macOS — the previous local regex copy had already missed the core's
/// mid-word guard fix ("newlines" firing the "new line" command).
/// </summary>
public static class TranscriptionTextProcessing
{
    /// <summary>
    /// Replaces spoken break commands ("new line" / "newline" / "new paragraph",
    /// with optional trailing punctuation) with a paragraph break.
    /// </summary>
    public static string ProcessVoiceCommands(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return HyperwhisperCoreMethods.ProcessVoiceCommands(text);
    }

    /// <summary>
    /// Final cleanup before saving a completed streaming session to history.
    /// </summary>
    public static string FinalizeStreamingText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return HyperwhisperCoreMethods.FinalizeStreamingText(text);
    }
}
