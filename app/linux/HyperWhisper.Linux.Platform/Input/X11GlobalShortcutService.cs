using System.Runtime.InteropServices;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Input;

internal sealed record X11ShortcutTrigger(uint Keysym, uint Modifiers, uint PrimaryModifier = 0);
internal sealed record X11ShortcutBinding(
    NamedShortcut Shortcut,
    IReadOnlyList<X11ShortcutTrigger> Triggers,
    bool ModifierOnly);
internal readonly record struct X11HotkeyEvent(byte Keycode, uint State, bool Pressed);

internal interface IX11HotkeyConnection : IDisposable
{
    byte Keycode(uint keysym);
    bool Grab(byte keycode, uint modifiers);
    void UngrabAll();
    bool TryRead(out X11HotkeyEvent value);
}

internal interface IX11HotkeyConnectionFactory
{
    PlatformResult<IX11HotkeyConnection> Open();
}

/// <summary>
/// True-Xorg global shortcuts. XGrabKey confines input at the X server to only
/// registered combinations; raw/unrelated key events never cross this module.
/// Wayland sessions deliberately retain the evdev privacy-filtered backend.
/// </summary>
internal sealed class X11GlobalShortcutService : IGlobalShortcutService
{
    private const uint CapsLockMask = 1u << 1;
    private const uint NumLockMask = 1u << 4;
    private const uint RelevantMask = 1u | 4u | 8u | 64u;
    private readonly object _gate = new();
    private readonly IX11HotkeyConnectionFactory _factory;
    private IReadOnlyList<X11ShortcutBinding> _bindings = [];
    private Dictionary<byte, List<(X11ShortcutBinding Binding, uint Modifiers)>> _byKeycode = [];
    private HashSet<string> _active = new(StringComparer.Ordinal);
    private IX11HotkeyConnection? _connection;
    private CancellationTokenSource? _cancellation;
    private Task? _reader;
    private bool _disposed;

    public X11GlobalShortcutService() : this(new X11HotkeyConnectionFactory()) { }
    internal X11GlobalShortcutService(IX11HotkeyConnectionFactory factory) => _factory = factory;

    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutPressed;
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutReleased;

    public IReadOnlyDictionary<string, PlatformResult> RegisterShortcuts(IReadOnlyCollection<NamedShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        var results = new Dictionary<string, PlatformResult>(StringComparer.Ordinal);
        var mapped = new List<X11ShortcutBinding>();
        var duplicates = shortcuts.GroupBy(value => value.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var shortcut in shortcuts)
        {
            if (duplicates.Contains(shortcut.Name))
            {
                results[shortcut.Name] = PlatformResult.Failure("shortcut_duplicate", "Shortcut names must be unique.");
                continue;
            }
            var result = X11ShortcutMapper.Map(shortcut);
            if (result.IsFailure) results[shortcut.Name] = PlatformResult.Failure(result.Error!.Code, result.Error.Message);
            else { mapped.Add(result.Value!); results[shortcut.Name] = PlatformResult.Success(); }
        }
        if (results.Values.Any(result => result.IsFailure) || mapped.Count != shortcuts.Count)
            return results;
        lock (_gate)
        {
            var previousBindings = _bindings;
            var previousActive = new HashSet<string>(_active, StringComparer.Ordinal);
            _bindings = mapped;
            if (_connection is not null && !ApplyGrabs())
            {
                foreach (var binding in mapped) results[binding.Shortcut.Name] = PlatformResult.Failure("shortcut_grab_failed", "The X11 shortcut is already in use.");
                _bindings = previousBindings;
                _active = previousActive;
                _ = ApplyGrabs();
            }
            else
            {
                var names = mapped.Select(binding => binding.Shortcut.Name).ToHashSet(StringComparer.Ordinal);
                _active.IntersectWith(names);
            }
        }
        return results;
    }

    public PlatformResult Start()
    {
        lock (_gate)
        {
            if (_disposed) return PlatformResult.Failure("shortcut_disposed", "The shortcut service is disposed.");
            if (_connection is not null) return PlatformResult.Success();
            var opened = _factory.Open();
            if (opened.IsFailure) return PlatformResult.Failure(opened.Error!.Code, opened.Error.Message);
            _connection = opened.Value!;
            if (!ApplyGrabs())
            {
                _connection.Dispose(); _connection = null;
                return PlatformResult.Failure("shortcut_grab_failed", "One or more X11 shortcuts are already in use.");
            }
            _cancellation = new CancellationTokenSource();
            _reader = Task.Run(() => ReadLoop(_cancellation.Token));
            return PlatformResult.Success();
        }
    }

    private bool ApplyGrabs()
    {
        _connection!.UngrabAll();
        var byCode = new Dictionary<byte, List<(X11ShortcutBinding, uint)>>();
        foreach (var binding in _bindings)
        foreach (var trigger in binding.Triggers)
        {
            var code = _connection.Keycode(trigger.Keysym);
            if (code == 0) { _connection.UngrabAll(); _byKeycode = []; return false; }
            var variants = new uint[] { 0, CapsLockMask, NumLockMask, CapsLockMask | NumLockMask };
            foreach (var locks in variants)
                if (!_connection.Grab(code, trigger.Modifiers | locks))
                { _connection.UngrabAll(); _byKeycode = []; return false; }
            if (!byCode.TryGetValue(code, out var entries)) byCode[code] = entries = [];
            entries.Add((binding, trigger.Modifiers));
        }
        _byKeycode = byCode;
        return true;
    }

    private async Task ReadLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            while (_connection is not null && _connection.TryRead(out var input)) Process(input);
            try { await Task.Delay(10, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private void Process(X11HotkeyEvent input)
    {
        var signals = new List<(bool Pressed, NamedShortcut Shortcut)>();
        lock (_gate)
        {
            if (!_byKeycode.TryGetValue(input.Keycode, out var candidates)) return;
            foreach (var candidate in candidates)
            {
                var binding = candidate.Binding;
                var state = input.State & RelevantMask;
                if (binding.ModifierOnly && !input.Pressed)
                    state &= ~binding.Triggers.First(trigger =>
                        _connection!.Keycode(trigger.Keysym) == input.Keycode).PrimaryModifier;
                if (state != candidate.Modifiers) continue;
                var changed = input.Pressed ? _active.Add(binding.Shortcut.Name) : _active.Remove(binding.Shortcut.Name);
                if (changed) signals.Add((input.Pressed, binding.Shortcut));
            }
        }
        foreach (var signal in signals)
            Raise(signal.Pressed ? ShortcutPressed : ShortcutReleased, signal.Shortcut);
    }

    private void Raise(EventHandler<ShortcutTriggeredEventArgs>? handlers, NamedShortcut shortcut)
    {
        if (handlers is null) return;
        var args = new ShortcutTriggeredEventArgs(shortcut.Name, shortcut.Shortcut);
        foreach (EventHandler<ShortcutTriggeredEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, args); } catch { }
    }

    public void Clear() { lock (_gate) { _bindings = []; _byKeycode.Clear(); _active.Clear(); _connection?.UngrabAll(); } }
    public void ResetKeyboardState() { lock (_gate) _active.Clear(); }
    public void Dispose()
    {
        Task? reader;
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; _cancellation?.Cancel(); reader = _reader;
        }
        try { reader?.GetAwaiter().GetResult(); } catch { }
        lock (_gate) { _connection?.UngrabAll(); _connection?.Dispose(); _connection = null; _cancellation?.Dispose(); }
        ShortcutPressed = null; ShortcutReleased = null;
    }
}

internal static class X11ShortcutMapper
{
    private const uint Shift = 1, Control = 4, Alt = 8, Meta = 64;
    public static PlatformResult<X11ShortcutBinding> Map(NamedShortcut named)
    {
        if (string.IsNullOrWhiteSpace(named.Name) || named.Shortcut.IsEmpty)
            return PlatformResult<X11ShortcutBinding>.Failure("shortcut_invalid", "A shortcut needs a name and at least one key.");
        var mask = (named.Shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift) ? Shift : 0) |
            (named.Shortcut.Modifiers.HasFlag(ShortcutModifiers.Control) ? Control : 0) |
            (named.Shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt) ? Alt : 0) |
            (named.Shortcut.Modifiers.HasFlag(ShortcutModifiers.Meta) ? Meta : 0);
        IReadOnlyList<X11ShortcutTrigger> triggers;
        if (named.Shortcut.Key.IsNone)
        {
            triggers = ModifierTriggers(named.Shortcut.Modifiers);
        }
        else triggers = MapKey(named.Shortcut.Key).Select(keysym => new X11ShortcutTrigger(keysym, mask)).ToArray();
        return triggers.Count == 0
            ? PlatformResult<X11ShortcutBinding>.Failure("shortcut_unsupported", "The shortcut key is not supported by X11.")
            : PlatformResult<X11ShortcutBinding>.Success(new(named, triggers, named.Shortcut.Key.IsNone));
    }

    private static IReadOnlyList<X11ShortcutTrigger> ModifierTriggers(ShortcutModifiers modifiers)
    {
        var groups = new (ShortcutModifiers Modifier, uint Mask, uint[] Keysyms)[]
        {
            (ShortcutModifiers.Control, Control, [0xffe3, 0xffe4]),
            (ShortcutModifiers.Alt, Alt, [0xffe9, 0xffea]),
            (ShortcutModifiers.Shift, Shift, [0xffe1, 0xffe2]),
            (ShortcutModifiers.Meta, Meta, [0xffeb, 0xffec]),
        };
        return groups.Where(group => modifiers.HasFlag(group.Modifier))
            .SelectMany(group => group.Keysyms.Select(keysym => new X11ShortcutTrigger(keysym,
                MaskFor(modifiers & ~group.Modifier), group.Mask)))
            .ToArray();
    }

    private static uint MaskFor(ShortcutModifiers modifiers) =>
        (modifiers.HasFlag(ShortcutModifiers.Shift) ? Shift : 0)
        | (modifiers.HasFlag(ShortcutModifiers.Control) ? Control : 0)
        | (modifiers.HasFlag(ShortcutModifiers.Alt) ? Alt : 0)
        | (modifiers.HasFlag(ShortcutModifiers.Meta) ? Meta : 0);

    private static IReadOnlyList<uint> MapKey(ShortcutKeyCode key)
    {
        var name = key.Value;
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z') return [(uint)name[0]];
        if (name.Length == 6 && name.StartsWith("Digit", StringComparison.Ordinal) && char.IsAsciiDigit(name[5])) return [(uint)name[5]];
        if (name[0] == 'F' && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24) return [(uint)(0xffbd + f)];
        var symbol = name switch
        {
            "LeftControl" => 0xffe3u, "RightControl" => 0xffe4u, "LeftShift" => 0xffe1u, "RightShift" => 0xffe2u,
            "LeftAlt" => 0xffe9u, "RightAlt" => 0xffeau, "LeftMeta" => 0xffebu, "RightMeta" => 0xffecu,
            "Escape" => 0xff1bu, "Space" => 0x20u, "Enter" => 0xff0du, "Tab" => 0xff09u,
            "Backspace" => 0xff08u, "Delete" => 0xffffu, "Insert" => 0xff63u, "Home" => 0xff50u, "End" => 0xff57u,
            "PageUp" => 0xff55u, "PageDown" => 0xff56u, "ArrowUp" => 0xff52u, "ArrowDown" => 0xff54u,
            "ArrowLeft" => 0xff51u, "ArrowRight" => 0xff53u, "Period" => 0x2eu, "Comma" => 0x2cu,
            "Minus" => 0x2du, "Equal" => 0x3du, "Slash" => 0x2fu, "Backslash" => 0x5cu,
            "Semicolon" => 0x3bu, "Quote" => 0x27u, "LeftBracket" => 0x5bu, "RightBracket" => 0x5du, "Grave" => 0x60u,
            _ => 0u
        };
        return symbol == 0 ? [] : [symbol];
    }
}

internal sealed class X11HotkeyConnectionFactory : IX11HotkeyConnectionFactory
{
    public PlatformResult<IX11HotkeyConnection> Open()
    {
        var display = X11HotkeyNative.XOpenDisplay(IntPtr.Zero);
        return display == IntPtr.Zero
            ? PlatformResult<IX11HotkeyConnection>.Failure("shortcut_x11_unavailable", "The X11 display is unavailable.")
            : PlatformResult<IX11HotkeyConnection>.Success(new X11HotkeyConnection(display));
    }
}

internal sealed class X11HotkeyConnection(IntPtr display) : IX11HotkeyConnection
{
    private static readonly object ErrorHandlerGate = new();
    // Avalonia may initialize Xlib before this adapter, so calling XInitThreads
    // here would be invalid. Serialize every operation on our private Display
    // instead; no Xlib call on this connection escapes this lock.
    private readonly object _displayGate = new();
    private readonly IntPtr _root = X11HotkeyNative.XDefaultRootWindow(display);
    public byte Keycode(uint keysym) { lock (_displayGate) return X11HotkeyNative.XKeysymToKeycode(display, (nuint)keysym); }
    public bool Grab(byte keycode, uint modifiers)
    {
        lock (_displayGate)
        lock (ErrorHandlerGate)
        {
            var failed = false;
            X11HotkeyNative.XErrorHandler handler = (_, _) => { failed = true; return 0; };
            var previous = X11HotkeyNative.XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(handler));
            try
            {
                X11HotkeyNative.XGrabKey(display, keycode, modifiers, _root, false, 1, 1);
                X11HotkeyNative.XSync(display, false);
                return !failed;
            }
            finally
            {
                X11HotkeyNative.XSetErrorHandler(previous);
                GC.KeepAlive(handler);
            }
        }
    }
    public void UngrabAll()
    {
        lock (_displayGate)
        { X11HotkeyNative.XUngrabKey(display, 0, 1u << 15, _root); X11HotkeyNative.XSync(display, false); }
    }
    public bool TryRead(out X11HotkeyEvent value)
    {
        lock (_displayGate)
        {
            value = default;
            if (X11HotkeyNative.XPending(display) == 0) return false;
            X11HotkeyNative.XNextEvent(display, out var input);
            if (input.Type is not (2 or 3)) return true;
            value = new(input.Keycode, input.State, input.Type == 2); return true;
        }
    }
    public void Dispose() { lock (_displayGate) X11HotkeyNative.XCloseDisplay(display); }
}

internal static class X11HotkeyNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);
    [StructLayout(LayoutKind.Explicit, Size = 192)] internal struct XKeyEvent
    { [FieldOffset(0)] public int Type; [FieldOffset(80)] public uint State; [FieldOffset(84)] public byte Keycode; }
    [DllImport("libX11.so.6")] internal static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] internal static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] internal static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [DllImport("libX11.so.6")] internal static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] internal static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport("libX11.so.6")] internal static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] internal static extern int XNextEvent(IntPtr display, out XKeyEvent value);
    [DllImport("libX11.so.6")] internal static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("libX11.so.6")] internal static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] internal static extern IntPtr XSetErrorHandler(IntPtr handler);
}
