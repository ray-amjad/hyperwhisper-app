using HyperWhisper.Data.Entities;

namespace HyperWhisper.Linux;

public static class LinuxModeCycler
{
    public static Mode? Next(IEnumerable<Mode> modes, Mode? selected)
    {
        var ordered = modes
            .OrderBy(mode => mode.SortOrder)
            .ThenBy(mode => mode.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mode => mode.Id)
            .ToArray();
        if (ordered.Length == 0) return null;
        var current = selected is null ? -1 : Array.FindIndex(ordered, mode => mode.Id == selected.Id);
        return ordered[(current + 1 + ordered.Length) % ordered.Length];
    }
}
