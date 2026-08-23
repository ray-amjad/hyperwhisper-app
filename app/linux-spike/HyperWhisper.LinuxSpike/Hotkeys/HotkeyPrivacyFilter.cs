namespace HyperWhisper.LinuxSpike.Hotkeys;

/// <summary>
/// The privacy boundary for evdev. Raw events enter here; only configured
/// binding signals can leave. Non-configured keys are used transiently for
/// chord state and are never stored after release, logged, or emitted.
/// </summary>
internal sealed class HotkeyPrivacyFilter
{
    private readonly IReadOnlyList<HotkeyBinding> _bindings;
    private readonly HashSet<ushort> _configuredCodes;
    private readonly HashSet<ushort> _downCodes = [];
    private readonly HashSet<string> _activeBindings = [];

    public HotkeyPrivacyFilter(IReadOnlyList<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Any(binding => string.IsNullOrWhiteSpace(binding.Id)))
        {
            throw new ArgumentException("Every hotkey binding needs an action ID.", nameof(bindings));
        }

        if (bindings.Select(binding => binding.Id).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
        {
            throw new ArgumentException("Hotkey action IDs must be unique.", nameof(bindings));
        }

        _bindings = bindings
            .Select(binding => binding with { ModifierCodes = binding.ModifierCodes.ToHashSet() })
            .ToArray();
        _configuredCodes = _bindings
            .SelectMany(binding => binding.ModifierCodes.Append(binding.PrimaryCode))
            .ToHashSet();
    }

    public IReadOnlyList<HotkeySignal> Process(EvdevInputEvent inputEvent)
    {
        if (!inputEvent.IsKeyEvent)
        {
            return [];
        }

        // Privacy invariant: keys that are not part of any configured binding
        // are discarded at the boundary. They are not added to chord state.
        if (!_configuredCodes.Contains(inputEvent.Code))
        {
            return [];
        }

        var signals = new List<HotkeySignal>();
        var value = (EvdevKeyValue)inputEvent.Value;

        if (value == EvdevKeyValue.Released)
        {
            foreach (var binding in _bindings)
            {
                if (_activeBindings.Contains(binding.Id)
                    && (binding.PrimaryCode == inputEvent.Code
                        || binding.ModifierCodes.Contains(inputEvent.Code)))
                {
                    _activeBindings.Remove(binding.Id);
                    signals.Add(new HotkeySignal(binding.Id, HotkeySignalKind.Released));
                }
            }

            _downCodes.Remove(inputEvent.Code);
            return signals;
        }

        _downCodes.Add(inputEvent.Code);
        foreach (var binding in _bindings)
        {
            if (_downCodes.Contains(binding.PrimaryCode)
                && binding.ModifierCodes.All(_downCodes.Contains)
                && _activeBindings.Add(binding.Id))
            {
                signals.Add(new HotkeySignal(binding.Id, HotkeySignalKind.Pressed));
            }
        }

        return signals;
    }
}
