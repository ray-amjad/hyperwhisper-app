using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Injection;

internal interface INativeClipboardOwner : IDisposable
{
    bool IsAvailable { get; }
    ValueTask<PlatformResult> OwnAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken);
}

internal sealed class NativeX11ClipboardOwner : INativeClipboardOwner
{
    private const int SelectionClear = 29;
    private const int SelectionRequest = 30;
    private const int SelectionNotify = 31;
    private const int PropModeReplace = 0;
    private const int AtomFormat = 32;
    private readonly ConcurrentQueue<OwnershipRequest> _requests = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread? _thread;
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private int _disposed;
    private IntPtr _display;
    private IntPtr _window;
    private IntPtr _clipboard;
    private IntPtr _targets;
    private Dictionary<IntPtr, byte[]> _formats = [];

    public NativeX11ClipboardOwner(bool allowXWayland = false)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
            || (!allowXWayland && string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)))
        {
            _initialized.TrySetResult(false);
            return;
        }
        _thread = new Thread(EventLoop) { IsBackground = true, Name = "HyperWhisper X11 clipboard" };
        _thread.Start();
    }

    public bool IsAvailable
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            try { return _initialized.Task.Wait(TimeSpan.FromSeconds(2)) && _initialized.Task.Result; }
            catch { return false; }
        }
    }

    public async ValueTask<PlatformResult> OwnAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return PlatformResult.Failure("x11_clipboard_unavailable", "Native X11 clipboard ownership is unavailable.");
        var completion = new TaskCompletionSource<PlatformResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(new OwnershipRequest(Clone(snapshot.Formats), completion, cancellationToken));
        _wake.Set();
        return await completion.Task.WaitAsync(ExternalProcessRunner.DefaultTimeout, cancellationToken).ConfigureAwait(false);
    }

    private void EventLoop()
    {
        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) { _initialized.TrySetResult(false); return; }
            _window = XCreateSimpleWindow(_display, XDefaultRootWindow(_display), 0, 0, 1, 1, 0, 0, 0);
            _clipboard = XInternAtom(_display, "CLIPBOARD", false);
            _targets = XInternAtom(_display, "TARGETS", false);
            _initialized.TrySetResult(_window != IntPtr.Zero && _clipboard != IntPtr.Zero && _targets != IntPtr.Zero);
            while (!_stopping)
            {
                ProcessOwnershipRequests();
                while (XPending(_display) > 0)
                {
                    var storage = Marshal.AllocHGlobal(192);
                    try
                    {
                        _ = XNextEvent(_display, storage);
                        var type = Marshal.ReadInt32(storage);
                        if (type == SelectionRequest) HandleRequest(Marshal.PtrToStructure<XSelectionRequestEvent>(storage));
                        else if (type == SelectionClear) _formats.Clear();
                    }
                    finally { Marshal.FreeHGlobal(storage); }
                }
                _wake.WaitOne(10);
            }
        }
        catch { _initialized.TrySetResult(false); FailPending(); }
        finally
        {
            if (_display != IntPtr.Zero)
            {
                if (_window != IntPtr.Zero) _ = XDestroyWindow(_display, _window);
                _ = XCloseDisplay(_display);
            }
        }
    }

    private void ProcessOwnershipRequests()
    {
        while (_requests.TryDequeue(out var request))
        {
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                continue;
            }
            var mapped = new Dictionary<IntPtr, byte[]>();
            foreach (var pair in request.Formats)
            {
                var atom = XInternAtom(_display, pair.Key, false);
                if (atom != IntPtr.Zero) mapped[atom] = pair.Value;
            }
            _formats = mapped;
            XSetSelectionOwner(_display, _clipboard, _window, IntPtr.Zero);
            _ = XFlush(_display);
            request.Completion.TrySetResult(XGetSelectionOwner(_display, _clipboard) == _window
                ? PlatformResult.Success()
                : PlatformResult.Failure("x11_clipboard_ownership_failed", "X11 clipboard ownership could not be acquired."));
        }
    }

    private void HandleRequest(XSelectionRequestEvent request)
    {
        var property = request.property == IntPtr.Zero ? request.target : request.property;
        var success = false;
        if (request.target == _targets)
        {
            var atoms = _formats.Keys.Prepend(_targets).ToArray();
            var native = Marshal.AllocHGlobal(atoms.Length * IntPtr.Size);
            try
            {
                for (var index = 0; index < atoms.Length; index++) Marshal.WriteIntPtr(native, index * IntPtr.Size, atoms[index]);
                _ = XChangeProperty(_display, request.requestor, property, new IntPtr(4), AtomFormat,
                    PropModeReplace, native, atoms.Length);
                success = true;
            }
            finally { Marshal.FreeHGlobal(native); }
        }
        else if (_formats.TryGetValue(request.target, out var bytes))
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                _ = XChangeProperty(_display, request.requestor, property, request.target, 8,
                    PropModeReplace, handle.AddrOfPinnedObject(), bytes.Length);
                success = true;
            }
            finally { handle.Free(); }
        }
        var response = new XSelectionEvent
        {
            type = SelectionNotify,
            display = request.display,
            requestor = request.requestor,
            selection = request.selection,
            target = request.target,
            property = success ? property : IntPtr.Zero,
            time = request.time,
        };
        _ = XSendEvent(_display, request.requestor, false, IntPtr.Zero, ref response);
        _ = XFlush(_display);
    }

    private void FailPending()
    {
        while (_requests.TryDequeue(out var request))
            request.Completion.TrySetResult(PlatformResult.Failure("x11_clipboard_failed", "The X11 clipboard event loop stopped."));
    }

    private static Dictionary<string, byte[]> Clone(IReadOnlyDictionary<string, byte[]> source) =>
        source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        FailPending();
        _wake.Dispose();
    }

    private sealed record OwnershipRequest(Dictionary<string, byte[]> Formats,
        TaskCompletionSource<PlatformResult> Completion, CancellationToken CancellationToken);

    [StructLayout(LayoutKind.Sequential)]
    private struct XSelectionRequestEvent
    {
        public int type; public UIntPtr serial; public int send_event; public IntPtr display;
        public IntPtr owner; public IntPtr requestor; public IntPtr selection; public IntPtr target;
        public IntPtr property; public IntPtr time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSelectionEvent
    {
        public int type; public UIntPtr serial; public int send_event; public IntPtr display;
        public IntPtr requestor; public IntPtr selection; public IntPtr target; public IntPtr property; public IntPtr time;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent,
        int x, int y, uint width, uint height, uint borderWidth, ulong border, ulong background);
    [DllImport("libX11.so.6")] private static extern int XDestroyWindow(IntPtr display, IntPtr window);
    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern void XSetSelectionOwner(IntPtr display, IntPtr selection, IntPtr owner, IntPtr time);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr selection);
    [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property,
        IntPtr type, int format, int mode, IntPtr data, int elementCount);
    [DllImport("libX11.so.6")] private static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate,
        IntPtr eventMask, ref XSelectionEvent eventSend);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
