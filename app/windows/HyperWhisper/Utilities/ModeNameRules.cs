using System;
using System.Collections.Generic;
using System.Linq;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Utilities;

/// <summary>
/// The one rule for "another mode already uses this name", shared by the mode
/// editor and the Local API (issue #2478). Same comparison as the Local API's
/// <c>MODE_NAME_TAKEN</c> and macOS's <c>ModeEditorDefaults.nameIsTaken</c>:
/// the shared core's <c>LocalApiModeNameConflict</c> (trim, then case-fold).
/// </summary>
internal static class ModeNameRules
{
    /// <summary>
    /// Whether <paramref name="candidate"/> collides with one of
    /// <paramref name="otherNames"/> - every OTHER mode's stored name, the
    /// edited mode left out by id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="storedName"/> is the edited mode's own stored name (null
    /// when creating). A candidate with the same comparison key is never a
    /// conflict: if the key does not change, the multiset of keys in storage is
    /// identical after the write and before it, so the write cannot introduce a
    /// collision. A mode that already shares a name (written by an older build)
    /// can therefore still save its other settings.
    /// </para>
    /// <para>
    /// A blank candidate is not a conflict; callers refuse it on their own.
    /// </para>
    /// </remarks>
    internal static bool IsTaken(string candidate, string? storedName, IEnumerable<string> otherNames)
    {
        ArgumentNullException.ThrowIfNull(otherNames);
        if (storedName is not null && HyperwhisperCoreMethods.LocalApiModeNameConflict(candidate, [storedName])) return false;
        return HyperwhisperCoreMethods.LocalApiModeNameConflict(candidate, otherNames.ToList());
    }
}
