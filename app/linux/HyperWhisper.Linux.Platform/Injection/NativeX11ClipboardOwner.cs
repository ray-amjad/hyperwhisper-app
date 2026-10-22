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
    private const short PollIn = 0x1;
    private const int EINTR = 4;
    private const int EfdNonBlock = 0x800;
    private const int EfdCloexec = 0x80000;
    // Without an eventfd the loop falls back to the old 10 ms poll, so a request still gets picked up.
    private const int FallbackPollMilliseconds = 10;
    private readonly ConcurrentQueue<OwnershipRequest> _requests = new();
    // Written by OwnAsync and Dispose, so the event loop can sleep in poll() until there is work.
    private readonly int _wakeFd = -1;
    private readonly PollFd[] _pollFds = new PollFd[2];
    // Guards the eventfd against a late OwnAsync writing to it, or to a reused fd number, after Dispose closed it.
    private readonly object _wakeGate = new();
    private bool _wakeClosed;
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
        _wakeFd = eventfd(0, EfdNonBlock | EfdCloexec);
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
        Wake();
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
                WaitForWork();
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

    // XPending above flushed every request and drained the event queue, so nothing is left unread when
    // this blocks. The thread sleeps until the X server sends an event or Wake() writes the eventfd.
    private void WaitForWork()
    {
        _pollFds[0] = new PollFd { fd = XConnectionNumber(_display), events = PollIn };
        var count = 1;
        if (_wakeFd >= 0)
        {
            _pollFds[1] = new PollFd { fd = _wakeFd, events = PollIn };
            count = 2;
        }
        var ready = poll(_pollFds, (nuint)count, _wakeFd >= 0 ? -1 : FallbackPollMilliseconds);
        if (ready < 0)
        {
            // A real poll failure must not turn the loop into a busy spin.
            if (Marshal.GetLastPInvokeError() != EINTR) Thread.Sleep(FallbackPollMilliseconds);
            return;
        }
        if (_wakeFd >= 0 && (_pollFds[1].revents & PollIn) != 0) _ = read(_wakeFd, out _, sizeof(ulong));
    }

    private void Wake()
    {
        lock (_wakeGate)
        {
            if (_wakeFd < 0 || _wakeClosed) return;
            ulong one = 1;
            _ = write(_wakeFd, ref one, sizeof(ulong));
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
        Wake();
        var exited = _thread?.Join(TimeSpan.FromSeconds(2)) ?? true;
        FailPending();
        // A thread that did not exit may still be in poll() on the fd, so it keeps it.
        if (!exited || _wakeFd < 0) return;
        lock (_wakeGate)
        {
            _wakeClosed = true;
            _ = close(_wakeFd);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd; public short events; public short revents;
    }

    [DllImport("libc", SetLastError = true)] private static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);
    [DllImport("libc", SetLastError = true)] private static extern int eventfd(uint initval, int flags);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, out ulong value, nint count);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, ref ulong value, nint count);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libX11.so.6")] private static extern int XConnectionNumber(IntPtr display);
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
