namespace HyperWhisper.LinuxSpike.Hotkeys;

public enum HotkeySignalKind
{
    Pressed,
    Released,
}

/// <summary>
/// The only hotkey value allowed to leave the privacy boundary. It identifies
/// a configured action, never a raw key or scan code.
/// </summary>
public sealed record HotkeySignal(string BindingId, HotkeySignalKind Kind);

public sealed record HotkeyBinding(
    string Id,
    ushort PrimaryCode,
    IReadOnlySet<ushort> ModifierCodes);

public interface IHotkeyDiagnostics
{
    void MalformedFrame();

    void ReadFailed();

    void HandlerFailed();
}

internal sealed class NullHotkeyDiagnostics : IHotkeyDiagnostics
{
    public static NullHotkeyDiagnostics Instance { get; } = new();

    public void MalformedFrame() { }

    public void ReadFailed() { }

    public void HandlerFailed() { }
}
