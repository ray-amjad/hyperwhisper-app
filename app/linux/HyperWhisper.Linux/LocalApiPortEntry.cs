using System.Globalization;

namespace HyperWhisper.Linux;

/// <summary>
/// The rules the Settings -> Local API "Preferred port" field applies to a raw entry (#692), kept
/// as plain functions with no control and no window attached.
///
/// They live here rather than inside MainWindow so they can be PINNED by a test that runs without
/// an X server: the xvfb smoke probe drives the real NumericUpDown end to end, and the composition
/// harness asserts these numbers directly. A markup guard could only ever grep for them.
/// </summary>
public static class LocalApiPortEntry
{
    /// <summary>The highest port a TCP bind accepts, and the field's Maximum.</summary>
    public const int MaximumPort = 65535;

    /// <summary>
    /// Reads an entry the way the CONTROL reads it. Avalonia 12.1.1's NumericUpDown parses with
    /// NumberStyles.Any against its own NumberFormat (ConvertTextToValueCore), so a narrower parse
    /// here would refuse text the field accepted before #692 and silently restore the stored port:
    /// on a German locale "8.080" is 8080 to the control and is not an integer at all to
    /// NumberStyles.Integer. Returns null when the control could not read it either, which is the
    /// entry the caller REFUSES.
    /// </summary>
    public static decimal? Parse(string? text, NumberFormatInfo? format = null)
        => decimal.TryParse((text ?? string.Empty).Trim(), NumberStyles.Any,
            format ?? NumberFormatInfo.CurrentInfo, out var value)
            ? value
            : null;

    /// <summary>
    /// The port an entry commits to, or null when the entry is REFUSED and the stored port has to
    /// come back on screen.
    ///
    /// Above 65535 the entry is CLAMPED and the clamped number is shown: that is #692's fault —
    /// "70000" bound 7000 while the box still read 70000 — and 65535 is the unambiguous nearest
    /// legal port. Empty, unreadable and NEGATIVE entries are refused: the control refused them
    /// before #692 too, and clamping the low end instead would land on port 0, which is a
    /// different feature (below) that a stray minus sign must not buy.
    ///
    /// 0 itself is NOT refused, and that is deliberate. It is a documented, reachable value on
    /// this field: Minimum="0" in the markup, the row's own label says the preferred port "falls
    /// back to an available loopback port", and PortableLocalApiHost passes 0 straight to Listen
    /// so the server takes an ephemeral port. It was equally committable before #692. Forbidding
    /// it would be a product decision about existing behaviour, not a fix for this issue.
    /// </summary>
    public static int? Committed(decimal? entered)
        => entered is { } value && value >= 0m
            ? (int)Math.Min(decimal.Truncate(value), MaximumPort)
            : null;
}
