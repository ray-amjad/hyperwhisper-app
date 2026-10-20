using System.Text;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Audio;

/// <summary>
/// Tells the app when the set of microphones, or the OS default input, may have changed, so the
/// pickers follow a plug or unplug without the "Refresh devices" button.
/// </summary>
/// <remarks>
/// Event-based: one long-lived <c>pactl subscribe</c> child, tied to our stdin so it cannot outlive
/// the app (PulseAudio and pipewire-pulse both
/// serve it). Only a source being added or removed, and a server change (that is how a new default
/// source is announced), count. <c>source-output</c> events are every capture stream opening and
/// closing — our own recordings and keep-warm included — and a source <c>change</c> is a volume
/// move, which the microphone boost makes during every recording; neither changes the list.
///
/// A burst (a USB interface brings a card, a source and a server change at once) is collapsed into
/// one <see cref="DevicesMayHaveChanged"/> after <c>debounce</c>. When the child cannot start or
/// exits (the sound server restarted), the watcher raises once — the list may have moved while
/// nobody listened — and tries again after <c>retryInterval</c>, so it degrades to polling rather
/// than going deaf. The event is raised on a pool thread.
/// </remarks>
public sealed class PulseAudioDeviceWatcher : IDisposable
{
    private readonly IChildProcessLauncher _launcher;
    private readonly string? _pactl;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _retryInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _loop;
    private IChildProcess? _child;
    private bool _pending;
    private bool _disposed;

    public PulseAudioDeviceWatcher()
        : this(new LinuxChildProcessLauncher(), CommandClipboardBackend.FindExecutable("pactl"),
            TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5)) { }

    internal PulseAudioDeviceWatcher(IChildProcessLauncher launcher, string? pactl, TimeSpan debounce, TimeSpan retryInterval)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _pactl = pactl;
        _debounce = debounce;
        _retryInterval = retryInterval;
    }

    /// <summary>Raised, debounced, on a pool thread when a source came or went or the default moved.</summary>
    public event EventHandler? DevicesMayHaveChanged;

    /// <summary>False when pactl is not installed; the device list then cannot be read either.</summary>
    public bool IsAvailable => _pactl is not null;

    /// <summary>Starts watching. A second call does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is not null || _pactl is null) return;
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }
    }

    /// <summary>The <c>pactl subscribe</c> lines that can change the input list or the default input.
    /// pactl runs under LC_ALL=C, so the wording is the untranslated one.</summary>
    internal static bool IsDeviceEvent(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        line = line.Trim();
        if (line.StartsWith("Event 'change' on server", StringComparison.Ordinal)) return true;
        return (line.StartsWith("Event 'new' on source #", StringComparison.Ordinal)
                || line.StartsWith("Event 'remove' on source #", StringComparison.Ordinal));
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Tied to our stdin, as the muffler's subscription is: `pactl subscribe` never writes
            // to its parent on its own, so after a crash or kill -9 it would run on, orphaned,
            // holding a server connection and the pipe. The shell kills it when our end closes.
            var started = _launcher.Start(new ChildProcessStartRequest
            {
                ExecutablePath = PulseAudioMuffler.Shell,
                Arguments = PulseAudioMuffler.TiedArguments(_pactl!, ["subscribe"], "hyperwhisper-device-watch"),
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                Environment = new Dictionary<string, string?> { ["LC_ALL"] = "C" },
            });
            if (started.IsSuccess)
            {
                await using var child = started.Value!;
                lock (_gate)
                {
                    if (_disposed) { _ = child.TerminateAsync(CancellationToken.None); return; }
                    _child = child;
                }
                try
                {
                    if (child.StandardOutput is { } output)
                    {
                        using var reader = new StreamReader(output, Encoding.UTF8);
                        while (!token.IsCancellationRequested)
                        {
                            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                            if (line is null) break;
                            if (IsDeviceEvent(line)) Schedule();
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception) { }
                finally
                {
                    lock (_gate) _child = null;
                    try { child.StandardInput?.Dispose(); } catch (Exception) { }
                    if (!child.HasExited)
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try { await child.TerminateAsync(deadline.Token).ConfigureAwait(false); } catch (Exception) { }
                    }
                }
            }
            if (token.IsCancellationRequested) return;
            // Nobody was listening for a while: whatever happened meanwhile, read the list again.
            Schedule();
            try { await Task.Delay(_retryInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Schedule()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_pending || _disposed) return;
            _pending = true;
            token = _lifetime.Token;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(_debounce, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            lock (_gate) _pending = false;
            var handlers = DevicesMayHaveChanged;
            if (handlers is null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); } catch (Exception) { }
        });
    }

    public void Dispose()
    {
        Task? loop;
        IChildProcess? child;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            loop = _loop;
            child = _child;
        }
        DevicesMayHaveChanged = null;
        _lifetime.Cancel();
        // A pipe read does not always honour the token; ending the child ends the read. Closing
        // its stdin is what makes the tying shell kill pactl; terminating the shell alone would not.
        if (child is not null)
        {
            try { child.StandardInput?.Dispose(); } catch (Exception) { }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { child.TerminateAsync(deadline.Token).AsTask().Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        }
        try { loop?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        // _lifetime is not disposed: a debounce still in flight holds its token, and a CTS without a timer owns nothing.
    }
}
