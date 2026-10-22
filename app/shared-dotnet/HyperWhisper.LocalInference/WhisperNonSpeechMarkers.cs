using HyperWhisper.SharedCore;

namespace HyperWhisper.LocalInference;

/// <summary>
/// Whisper's non-speech annotations: a bracketed or parenthesised marker it
/// emits in place of words when it hears none. Only fixed names match, so a
/// bracket the speaker dictated survives. The rule lives in the shared core
/// (<c>strip_whisper_non_speech_markers</c>, issue #1811), so this head, Windows
/// and macOS cannot drift apart; its cases are
/// <c>shared-conformance/whisper-marker-vectors.json</c>.
/// </summary>
internal static class WhisperNonSpeechMarkers
{
    /// <summary>
    /// Removes the markers and trims. A transcript made only of markers comes
    /// back empty, which the workflow reports as no speech.
    /// </summary>
    internal static string Strip(string? text) => SharedCoreBridge.StripWhisperNonSpeechMarkers(text);
}
