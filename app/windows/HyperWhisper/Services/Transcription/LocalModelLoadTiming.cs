namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Adds up, for one caller, how long the local-engine work done inside it spent
/// starting a process and loading a model. The Local API opens one around its
/// transcription to split <c>load_ms</c> from <c>decode_ms</c>:
/// <list type="bullet">
/// <item>Whisper: after an idle exit the first request pays the worker start and
/// the model load (#1932).</item>
/// <item>Parakeet: a request that loads the model, reloads the daemon for another
/// model or language, or auto-restarts a crashed daemon pays the daemon's start
/// up to READY (#3041).</item>
/// </list>
///
/// The scope is an AsyncLocal, so a GUI dictation running on the same shared
/// service at the same time adds to none, or to its own.
/// </summary>
public sealed class LocalModelLoadTiming : IDisposable
{
    private static readonly AsyncLocal<LocalModelLoadTiming?> s_current = new();

    private readonly LocalModelLoadTiming? _outer;
    private long _ticks;
    private bool _disposed;

    private LocalModelLoadTiming(LocalModelLoadTiming? outer) => _outer = outer;

    /// <summary>
    /// Opens a scope on the calling flow. Call it from the method that awaits the
    /// transcription (not from inside a helper that awaits): a value set inside
    /// an async method does not flow back out to its caller.
    /// </summary>
    public static LocalModelLoadTiming Begin()
    {
        var timing = new LocalModelLoadTiming(s_current.Value);
        s_current.Value = timing;
        return timing;
    }

    /// <summary>The process start and model load time added so far.</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <summary>Adds <paramref name="startAndLoad"/> to the scope on this flow, if one is open.</summary>
    internal static void Add(TimeSpan startAndLoad)
    {
        if (startAndLoad <= TimeSpan.Zero || s_current.Value is not { } timing) return;
        Interlocked.Add(ref timing._ticks, startAndLoad.Ticks);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(s_current.Value, this)) s_current.Value = _outer;
    }
}
