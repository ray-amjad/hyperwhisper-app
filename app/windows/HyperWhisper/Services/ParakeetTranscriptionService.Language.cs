// PARAKEET TRANSCRIPTION SERVICE — LANGUAGE
// The language normalization and segment-join rules that decide whether a mode
// switch must respawn the daemon (NeedsReload) and which --segment-join it gets.

using HyperWhisper.SharedCore;

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    /// <summary>
    /// The <c>--segment-join</c> value for a daemon spawned with
    /// <paramref name="language"/> (raw mode value; null/"auto" mean auto-detect):
    /// <c>"none"</c> for a no-space language, <c>"space"</c> for any other declared
    /// language, and null for auto, which leaves the join to the daemon.
    /// <para>
    /// The host decides the join from the shared core
    /// (<see cref="SharedCoreBridge.IsNoSpaceLanguage"/>) and sends the answer, so
    /// the x64 C++ daemon and the ARM64 managed daemon can no longer answer it from
    /// two different tables (#902). It goes through <see cref="ResolveJoinClass"/>,
    /// so the respawn predictor and the spawned daemon read the same rule.
    /// </para>
    /// </summary>
    internal static string? ResolveSegmentJoinArgument(string? language) =>
        ResolveJoinClass(NormalizeLanguage(language)) switch
        {
            "no-space" => "none",
            "spaced" => "space",
            _ => null,
        };

    /// <summary>
    /// Canonicalizes a mode/request language for reload comparison:
    /// null / whitespace / "auto" → "auto"; everything else trimmed + lowercased.
    /// </summary>
    internal static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "auto";
        }

        var normalized = language.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "auto" : normalized;
    }

    /// <summary>
    /// How the daemon spawned with <paramref name="normalizedLanguage"/> joins its
    /// segments. THREE classes, not two — <c>"auto"</c> is its own.
    /// </summary>
    /// <remarks>
    /// The daemon's join used to have two outcomes, so comparing
    /// <see cref="SharedCoreBridge.IsNoSpaceLanguage"/> across a mode switch was enough. Since
    /// issue #286 it has three: a no-space code joins with <c>""</c>, any other
    /// declared code joins with <c>" "</c>, and <c>"auto"</c> decides per boundary
    /// from the segment text. <c>"en"</c> and <c>"auto"</c> therefore produce
    /// DIFFERENT transcripts for the same Japanese audio, while
    /// <c>IsNoSpaceLanguage</c> calls them the same class — so a warm daemon
    /// spawned for <c>en</c> was kept for an auto-language mode and went on
    /// joining Japanese with spaces.
    /// <para>
    /// Over-classifying is safe here: the only cost of an unnecessary class change
    /// is one extra daemon respawn.
    /// </para>
    /// </remarks>
    internal static string ResolveJoinClass(string normalizedLanguage) => normalizedLanguage switch
    {
        "auto" => "auto",
        var code when SharedCoreBridge.IsNoSpaceLanguage(code) => "no-space",
        _ => "spaced",
    };

    /// <summary>
    /// Whether switching to <paramref name="modelId"/> + <paramref name="requestedLanguage"/>
    /// (raw mode value; null/"auto" mean auto-detect) requires respawning the daemon.
    /// Single source of truth for the previously copy-pasted call-site checks.
    ///
    /// - Not ready, or a different model → reload.
    /// - Language-hint engines (Nemotron online, Qwen3) take the language at
    ///   spawn → reload on any normalized-language change.
    /// - Parakeet TDT auto-detects language; its spawn hint only picks the
    ///   segment join → reload only when the join CLASS changes
    ///   (en→fr skips; en→ja and en→auto reload — see <see cref="ResolveJoinClass"/>).
    /// </summary>
    public bool NeedsReload(string modelId, string? requestedLanguage)
    {
        // Lock-free reads of _isReady/_loadedModelId/_lastLanguage — same
        // consistency model as the call sites this consolidates.
        if (!_isReady)
        {
            return true;
        }

        if (!string.Equals(_loadedModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var requested = NormalizeLanguage(requestedLanguage);
        var loaded = NormalizeLanguage(_lastLanguage);

        if (_isOnline || _isQwen3)
        {
            return !string.Equals(requested, loaded, StringComparison.Ordinal);
        }

        return JoinNeedsRespawn(requested, loaded);
    }

    /// <summary>
    /// Whether a Parakeet TDT daemon spawned for <paramref name="loaded"/> must be
    /// respawned to join segments for <paramref name="requested"/> (both already
    /// normalized): a change of <see cref="ResolveJoinClass"/>, or ANY change
    /// between two no-space codes.
    /// <para>
    /// The second rule is for the committed x64 <c>parakeet-engine.exe</c>, which
    /// predates <c>--segment-join</c> and still classes the code from its own
    /// four-code table (#902): a daemon spawned for <c>th</c> joins with spaces
    /// there, so it cannot be kept for <c>ja</c>. Over-classifying costs one extra
    /// respawn and nothing else.
    /// </para>
    /// </summary>
    internal static bool JoinNeedsRespawn(string requested, string loaded)
    {
        var requestedClass = ResolveJoinClass(requested);
        if (!string.Equals(requestedClass, ResolveJoinClass(loaded), StringComparison.Ordinal))
        {
            return true;
        }

        return requestedClass == "no-space" && !string.Equals(requested, loaded, StringComparison.Ordinal);
    }
}
