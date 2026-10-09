// RETRY MODE RESOLVER
// Decides which mode a plain History "Retry" runs on.
//
// Issue #1644 (the Windows twin of macOS #1440 / PR #1616): Retry used to fall
// back to the default mode when the mode that made the recording was gone.
// The default mode can be a HyperWhisper Cloud mode, so audio an on-device
// mode recorded was uploaded without asking, and the row was rewritten to the
// default mode. Ray's rule (#1440, #1617): a deleted-mode Retry never runs the
// default mode. It stops, sends nothing, leaves the row as it was, and the
// user picks a mode with "Retry With...".

using HyperWhisper.Data.Entities;

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Pure lookup behind History's plain Retry. No default-mode fallback, on
/// purpose: a null result means "refuse", never "use something else".
/// "Retry With..." does not come through here; the user's pick is the mode.
/// </summary>
public static class RetryModeResolver
{
    /// <summary>Localization key for the refusal shown when the original mode is gone.</summary>
    public const string ModeDeletedMessageKey = "errors.retryModeDeleted";

    /// <summary>
    /// The mode named by the row, or null when no mode has that name.
    /// A row with no stored mode name (null or empty) also returns null: there
    /// is no mode the user picked, so there is nothing to retry on.
    /// </summary>
    public static Mode? ResolveOriginalMode(IEnumerable<Mode> modes, string? transcriptModeName)
    {
        ArgumentNullException.ThrowIfNull(modes);

        if (string.IsNullOrEmpty(transcriptModeName)) return null;

        return modes.FirstOrDefault(m => m.Name == transcriptModeName);
    }
}
