using HyperWhisper.Data.Entities;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.Persistence;

/// <summary>
/// The default-mode invariant, over the .NET heads' <see cref="Mode"/> entity:
/// exactly one mode carries <see cref="Mode.IsDefault"/>, and that mode's name
/// is fixed (issue #536).
/// </summary>
/// <remarks>
/// <para>
/// The DECISION lives in the shared Rust core (<c>hw-modes</c>), because the
/// tie-break — which mode is promoted when a restored backup leaves none
/// flagged, or two — is a cross-platform contract: three heads that chose
/// differently would restore the same backup to a different default mode each.
/// This class is the .NET half of applying it, shared by the Windows head and
/// the portable (Linux) head, which compile the same <see cref="Mode"/> entity.
/// macOS applies the same core decision from Swift.
/// </para>
/// <para>
/// It deliberately does NOT touch a database. Both heads' stores hand out
/// tracked entities, so the caller mutates them and saves inside its own
/// transaction; a helper that opened its own context could not be part of that
/// transaction and would be a second, racing writer.
/// </para>
/// </remarks>
public static class DefaultModePolicy
{
    /// <summary>
    /// Make exactly one of <paramref name="modes"/> the default, in place.
    /// </summary>
    /// <param name="modes">
    /// EVERY mode that will exist after the write, in display order. Pass the
    /// whole set: "exactly one" cannot be decided from a subset.
    /// </param>
    /// <param name="preferred">
    /// The mode the caller is trying to make the default, or <c>null</c> when it
    /// is only repairing a set that arrived broken.
    /// </param>
    /// <returns>
    /// Whether anything changed. <c>false</c> means the set already satisfied
    /// the invariant, so the caller can skip its save and the change
    /// notification behind it.
    /// </returns>
    public static bool Apply(IReadOnlyList<Mode> modes, Guid? preferred = null) =>
        ApplyAndReport(modes, preferred).Count != 0;

    /// <summary>
    /// As <see cref="Apply"/>, but returns the ids of the rows whose
    /// <see cref="Mode.IsDefault"/> this call actually wrote — empty when the
    /// set already satisfied the invariant.
    /// </summary>
    /// <remarks>
    /// For callers that must do something else to exactly those rows, such as
    /// bumping <c>ModifiedDate</c> or upserting them. The plan already knows
    /// which they are, so reading it here is better than snapshotting every
    /// row's flag and diffing it afterwards — two callers that each did their
    /// own diff could drift from each other, and from this.
    /// </remarks>
    public static IReadOnlyList<Guid> ApplyAndReport(IReadOnlyList<Mode> modes, Guid? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(modes);
        var plan = SharedCoreBridge.PlanDefaultMode(Flags(modes), preferred);
        if (!plan.Changed) return [];

        var touched = new List<Guid>(plan.ClearIds.Count + 1);
        foreach (var id in plan.ClearIds)
        {
            var row = modes.FirstOrDefault(mode => mode.Id == id);
            if (row is null) continue;
            row.IsDefault = false;
            touched.Add(row.Id);
        }
        if (plan.DefaultId is { } winner)
        {
            var row = modes.FirstOrDefault(mode => mode.Id == winner);
            // The winner may already carry the flag — the plan is `Changed`
            // whenever ANY row moves — so only report a row this call wrote.
            if (row is { IsDefault: false })
            {
                row.IsDefault = true;
                touched.Add(row.Id);
            }
        }
        return touched;
    }

    /// <summary>
    /// Whether <paramref name="stored"/> may be renamed to
    /// <paramref name="newName"/>. The default mode's name is fixed; every other
    /// mode renames freely.
    /// </summary>
    public static PortableModeNameChange CheckRename(Mode stored, string newName)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(newName);
        return SharedCoreBridge.CheckModeNameChange(stored.IsDefault, stored.Name, newName);
    }

    /// <summary>
    /// Whether <paramref name="id"/> may have its default flag written to
    /// <paramref name="requestedIsDefault"/>. <paramref name="modes"/> is the
    /// set as it stands BEFORE the write. Only one combination is refused:
    /// clearing the flag on the one mode that carries it.
    /// </summary>
    public static PortableDefaultFlagChange CheckDefaultFlag(
        IReadOnlyList<Mode> modes,
        Guid id,
        bool requestedIsDefault)
    {
        ArgumentNullException.ThrowIfNull(modes);
        return SharedCoreBridge.CheckDefaultModeFlag(Flags(modes), id, requestedIsDefault);
    }

    private static IEnumerable<PortableModeFlags> Flags(IEnumerable<Mode> modes) =>
        modes.Select(mode => new PortableModeFlags(mode.Id, mode.IsDefault, mode.SortOrder));
}
