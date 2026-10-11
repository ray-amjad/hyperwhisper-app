using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Input;

internal sealed record EvdevBinding(
    NamedShortcut NamedShortcut,
    ushort? Primary,
    IReadOnlyDictionary<ShortcutModifiers, IReadOnlySet<ushort>> ModifierGroups,
    IReadOnlySet<ushort> NonInterferingCodes);
internal sealed record EvdevFilterOutput(IReadOnlyList<(NamedShortcut Shortcut, bool Pressed)> Signals, bool Interfered);

/// <summary>
/// Matches evdev key events against the registered bindings. A modifier-only chord
/// (Ctrl+Alt) is the prefix of every shortcut that starts with the same modifiers
/// (Ctrl+Alt+Left), so by default it does not fire on press (#1725, the evdev
/// sibling of #1511). Its press arms it, but only when no other key is down; any other
/// key-down while it is held spoils it. The filter keeps which codes are down, never
/// what they spell. The release that breaks a still-clean chord emits Pressed and
/// Released back to back.
/// </summary>
internal sealed class EvdevShortcutFilter
{
    private readonly object _gate = new();
    private readonly X11ModifierOnlyTrigger _modifierOnly;
    private IReadOnlyList<EvdevBinding> _bindings = [];
    private HashSet<ushort> _relevantCodes = [];
    private readonly Dictionary<string, DeviceState> _states = new(StringComparer.Ordinal);

    public EvdevShortcutFilter(X11ModifierOnlyTrigger modifierOnly = X11ModifierOnlyTrigger.OnCleanRelease)
        => _modifierOnly = modifierOnly;

    private enum ChordState
    {
        /// <summary>The chord is down and nothing else joined it. Its release fires.</summary>
        Armed,
        /// <summary>Another key went down with the chord. Its release does nothing.</summary>
        Spoiled,
    }

    private bool FiresOnRelease(EvdevBinding binding) =>
        _modifierOnly == X11ModifierOnlyTrigger.OnCleanRelease
        && !binding.Primary.HasValue && binding.ModifierGroups.Count > 0;

    public void ReplaceBindings(IReadOnlyList<EvdevBinding> bindings)
    {
        lock (_gate)
        {
            _bindings = bindings.ToArray();
            _relevantCodes = bindings
                .SelectMany(binding =>
                    binding.ModifierGroups.Values.SelectMany(codes => codes)
                        .Concat(binding.Primary is { } primary ? [primary] : []))
                .Concat(bindings.SelectMany(binding => binding.NonInterferingCodes))
                .ToHashSet();
            var names = bindings.Select(binding => binding.NamedShortcut.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var state in _states.Values)
            {
                state.Down.IntersectWith(_relevantCodes);
                state.Active.IntersectWith(names);
                foreach (var name in state.Chords.Keys.Where(name => !names.Contains(name)).ToArray())
                    state.Chords.Remove(name);
            }
        }
    }

    public EvdevFilterOutput Process(string deviceId, EvdevEvent input, bool interferenceArmed = false)
    {
        lock (_gate)
        {
            if (!input.IsKey)
            {
                return new([], false);
            }

            if (!_states.TryGetValue(deviceId, out var state))
            {
                state = new DeviceState();
                _states.Add(deviceId, state);
            }

            // Every code, relevant or not, so a chord completed over a key pressed before it
            // (Shift, then Ctrl+Alt) arms spoiled. A repeat of a key whose press was missed counts too.
            var pressed = false;
            if (input.Value == 0) state.Held.Remove(input.Code);
            else pressed = state.Held.Add(input.Code) || input.Value == 1;
            if (!_relevantCodes.Contains(input.Code))
            {
                if (pressed) SpoilChords(state, input.Code);
                return new([], input.Value == 1 && (interferenceArmed || state.Active.Count > 0));
            }

            if (pressed) SpoilChords(state, input.Code);

            if (input.Value == 0)
            {
                state.Down.Remove(input.Code);
            }
            else
            {
                state.Down.Add(input.Code);
            }

            var output = new List<(NamedShortcut, bool)>();
            foreach (var binding in _bindings)
            {
                var matches = (!binding.Primary.HasValue || state.Down.Contains(binding.Primary.Value))
                    && binding.ModifierGroups.Values.All(group => group.Any(state.Down.Contains));
                if (FiresOnRelease(binding))
                {
                    StepChord(state, binding, matches, output);
                    continue;
                }

                var active = state.Active.Contains(binding.NamedShortcut.Name);
                if (matches && !active)
                {
                    state.Active.Add(binding.NamedShortcut.Name);
                    output.Add((binding.NamedShortcut, true));
                }
                else if (!matches && active)
                {
                    state.Active.Remove(binding.NamedShortcut.Name);
                    output.Add((binding.NamedShortcut, false));
                }
            }

            return new(output, false);
        }
    }

    /// <summary>
    /// A press of a key outside an armed chord spoils it. A key from the chord's own
    /// groups (the other Ctrl, or a held one that repeats) does not.
    /// </summary>
    private void SpoilChords(DeviceState state, ushort code)
    {
        foreach (var binding in _bindings)
        {
            var name = binding.NamedShortcut.Name;
            if (state.Chords.GetValueOrDefault(name) != ChordState.Armed) continue;
            if (binding.ModifierGroups.Values.Any(group => group.Contains(code))) continue;
            state.Chords[name] = ChordState.Spoiled;
        }
    }

    private static void StepChord(DeviceState state, EvdevBinding binding, bool matches,
        List<(NamedShortcut, bool)> output)
    {
        var name = binding.NamedShortcut.Name;
        var held = state.Chords.TryGetValue(name, out var chord);
        if (matches)
        {
            // Exact, like the X11 sibling: a key outside the chord already down spoils it at once.
            if (!held)
                state.Chords[name] = state.Held.All(code => binding.ModifierGroups.Values.Any(group => group.Contains(code)))
                    ? ChordState.Armed
                    : ChordState.Spoiled;
            return;
        }

        if (!held) return;
        state.Chords.Remove(name);
        if (chord != ChordState.Armed) return;
        output.Add((binding.NamedShortcut, true));
        output.Add((binding.NamedShortcut, false));
    }

    public void Reset()
    {
        lock (_gate)
        {
            _states.Clear();
        }
    }

    private sealed class DeviceState
    {
        public HashSet<ushort> Down { get; } = [];
        /// <summary>Every code down on the device, relevant or not.</summary>
        public HashSet<ushort> Held { get; } = [];
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);
        /// <summary>The release-fired chords that are down, by name. Absent means not down.</summary>
        public Dictionary<string, ChordState> Chords { get; } = new(StringComparer.Ordinal);
    }
}

internal static class EvdevShortcutMapper
{
    private static readonly IReadOnlyDictionary<ShortcutModifiers, IReadOnlySet<ushort>> ModifierCodes =
        new Dictionary<ShortcutModifiers, IReadOnlySet<ushort>>
        {
            [ShortcutModifiers.Control] = new HashSet<ushort> { 29, 97 },
            [ShortcutModifiers.Alt] = new HashSet<ushort> { 56, 100 },
            [ShortcutModifiers.Shift] = new HashSet<ushort> { 42, 54 },
            [ShortcutModifiers.Meta] = new HashSet<ushort> { 125, 126 },
        };

    public static PlatformResult<EvdevBinding> Map(NamedShortcut named)
    {
        if (string.IsNullOrWhiteSpace(named.Name) || named.Shortcut.IsEmpty)
        {
            return PlatformResult<EvdevBinding>.Failure("shortcut_invalid", "A shortcut needs a name and at least one key.");
        }

        ushort? primary = null;
        if (!named.Shortcut.Key.IsNone)
        {
            primary = MapKey(named.Shortcut.Key);
            if (!primary.HasValue)
            {
                return PlatformResult<EvdevBinding>.Failure("shortcut_unsupported", "The shortcut key is not supported by evdev.");
            }
        }

        var groups = ModifierCodes
            .Where(pair => named.Shortcut.Modifiers.HasFlag(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var nonInterfering = named.Shortcut.Key.Value switch
        {
            "LeftControl" => new HashSet<ushort> { 97 }, "RightControl" => new HashSet<ushort> { 29 },
            "LeftAlt" => new HashSet<ushort> { 100 }, "RightAlt" => new HashSet<ushort> { 56 },
            "LeftShift" => new HashSet<ushort> { 54 }, "RightShift" => new HashSet<ushort> { 42 },
            "LeftMeta" => new HashSet<ushort> { 126 }, "RightMeta" => new HashSet<ushort> { 125 },
            _ => [],
        };
        return PlatformResult<EvdevBinding>.Success(new EvdevBinding(named, primary, groups, nonInterfering));
    }

    private static ushort? MapKey(ShortcutKeyCode key)
    {
        var name = key.Value;
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z')
        {
            ushort[] codes = [30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44];
            return codes[name[0] - 'A'];
        }

        if (name.Length == 6 && name.StartsWith("Digit", StringComparison.Ordinal)
            && name[5] is >= '0' and <= '9')
        {
            ushort[] codes = [11, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            return codes[name[5] - '0'];
        }

        if (name.Length >= 2 && name[0] == 'F'
            && int.TryParse(name.AsSpan(1), out var functionNumber)
            && functionNumber is >= 1 and <= 10)
        {
            return (ushort)(58 + functionNumber);
        }

        if (name.Length >= 3 && name[0] == 'F'
            && int.TryParse(name.AsSpan(1), out functionNumber)
            && functionNumber is >= 13 and <= 24)
        {
            return (ushort)(170 + functionNumber);
        }

        return name switch
        {
            "F11" => 87, "F12" => 88,
            "LeftControl" => 29, "RightControl" => 97,
            "LeftAlt" => 56, "RightAlt" => 100,
            "LeftShift" => 42, "RightShift" => 54,
            "LeftMeta" => 125, "RightMeta" => 126,
            "Escape" => 1, "Space" => 57, "Enter" => 28, "Tab" => 15,
            "Backspace" => 14, "Delete" => 111, "Insert" => 110,
            "Home" => 102, "End" => 107, "PageUp" => 104, "PageDown" => 109,
            "ArrowUp" => 103, "ArrowDown" => 108, "ArrowLeft" => 105, "ArrowRight" => 106,
            "Period" => 52, "Comma" => 51, "Minus" => 12, "Equal" => 13,
            "Slash" => 53, "Backslash" => 43, "Semicolon" => 39, "Quote" => 40,
            "LeftBracket" => 26, "RightBracket" => 27, "Grave" => 41,
            _ => null,
        };
    }
}
