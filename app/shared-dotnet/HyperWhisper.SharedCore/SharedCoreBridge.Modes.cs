using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// One mode, projected down to the three columns the default-mode invariant
/// reads (issue #536).
/// </summary>
public readonly record struct PortableModeFlags(Guid Id, bool IsDefault, int SortOrder);

/// <summary>
/// What a head must write so that exactly one mode carries the default flag.
/// Apply it as one unit inside the write's own transaction.
/// </summary>
/// <param name="DefaultId">
/// The mode that must carry the flag when the write completes, or <c>null</c>
/// when there are no modes at all.
/// </param>
/// <param name="ClearIds">Every mode whose flag must be cleared.</param>
/// <param name="Changed">
/// Whether applying the plan changes anything. <c>false</c> means the set
/// already satisfies the invariant, so the caller can skip the save and the
/// change notification behind it.
/// </param>
public sealed record PortableDefaultModePlan(
    Guid? DefaultId,
    IReadOnlyList<Guid> ClearIds,
    bool Changed);

/// <summary>Whether a mode's name may be written.</summary>
public enum PortableModeNameChange
{
    Allowed,

    /// <summary>
    /// The mode carries the default flag, and the default mode's name is fixed.
    /// </summary>
    RejectedDefaultIsFixed
}

/// <summary>Whether a mode's default flag may be written as asked.</summary>
public enum PortableDefaultFlagChange
{
    Allowed,

    /// <summary>Clearing it would leave no default mode at all.</summary>
    RejectedLastDefault
}

public static partial class SharedCoreBridge
{
    /// <summary>
    /// Decide which mode carries the default flag once the write completes.
    /// </summary>
    /// <param name="rows">
    /// EVERY mode that will exist after the write — not the delta — in the
    /// head's display order. "Exactly one" cannot be decided from a subset, and
    /// the caller's order is the tie-break between equal sort orders.
    /// </param>
    /// <param name="preferred">
    /// The mode the caller is trying to make the default, or <c>null</c> when
    /// it is only repairing.
    /// </param>
    public static PortableDefaultModePlan PlanDefaultMode(
        IEnumerable<PortableModeFlags> rows,
        Guid? preferred)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var plan = HyperwhisperCoreMethods.ModePlanDefault(
            rows.Select(ToFfi).ToList(),
            preferred?.ToString("D"));
        return new PortableDefaultModePlan(
            plan.@defaultId is { } id ? Guid.Parse(id) : null,
            plan.@clearIds.Select(Guid.Parse).ToArray(),
            plan.@changed);
    }

    /// <summary>
    /// Whether a mode's name may be changed to <paramref name="newName"/>. The
    /// default mode's name is fixed; every other mode renames freely.
    /// </summary>
    public static PortableModeNameChange CheckModeNameChange(
        bool isDefault,
        string storedName,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(storedName);
        ArgumentNullException.ThrowIfNull(newName);
        return HyperwhisperCoreMethods.ModeCheckNameChange(isDefault, storedName, newName)
            switch
            {
                HwModeNameChange.RejectedDefaultIsFixed =>
                    PortableModeNameChange.RejectedDefaultIsFixed,
                _ => PortableModeNameChange.Allowed
            };
    }

    /// <summary>
    /// Whether <paramref name="id"/> may have its default flag written to
    /// <paramref name="requestedIsDefault"/>. <paramref name="rows"/> is the set
    /// as it stands BEFORE the write.
    /// </summary>
    public static PortableDefaultFlagChange CheckDefaultModeFlag(
        IEnumerable<PortableModeFlags> rows,
        Guid id,
        bool requestedIsDefault)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return HyperwhisperCoreMethods.ModeCheckDefaultFlag(
            rows.Select(ToFfi).ToList(),
            id.ToString("D"),
            requestedIsDefault)
            switch
            {
                HwDefaultFlagChange.RejectedLastDefault =>
                    PortableDefaultFlagChange.RejectedLastDefault,
                _ => PortableDefaultFlagChange.Allowed
            };
    }

    private static HwModeFlags ToFfi(PortableModeFlags flags) =>
        new(flags.Id.ToString("D"), flags.IsDefault, flags.SortOrder);
}
