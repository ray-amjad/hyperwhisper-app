namespace HyperWhisper.Services;

/// <summary>
/// Remembers the Core Audio <c>FriendlyName</c> of each capture endpoint, keyed
/// by its endpoint ID (<c>MMDevice.ID</c>).
///
/// WHY (#980): each <c>MMDevice.FriendlyName</c> read opens the endpoint's
/// property store and costs ~50 ms. <see cref="AudioRecorderService.BoostMicVolume"/>
/// runs on the UI thread at every dictation start, where the low-level keyboard
/// hooks also run, so two reads there delayed key presses system-wide.
///
/// STALENESS: the key is the endpoint ID, which Windows never reuses for a
/// different endpoint, so an entry can never name another device. The one way
/// an entry goes stale is a rename of the same endpoint in the Sound control
/// panel; <see cref="AudioDeviceService"/> drops that endpoint's entry on its
/// property-change callback, and clears the whole cache on add / remove / state /
/// default-device changes.
///
/// Thread-safe: reads happen on the UI thread, invalidation on Core Audio's
/// notification (COM) thread.
/// </summary>
internal sealed class CaptureDeviceNameCache
{
    /// <summary>
    /// The process-wide cache. Endpoint IDs are system-wide, so every
    /// <see cref="AudioRecorderService"/> instance shares it.
    /// </summary>
    public static CaptureDeviceNameCache Shared { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    // Bumped by Invalidate / Clear. A read that started before an invalidation
    // does not store its (possibly pre-rename) result.
    private long _generation;

    /// <summary>
    /// Returns the cached name for <paramref name="endpointId"/>, or calls
    /// <paramref name="readName"/> once and caches what it returns.
    ///
    /// An exception from <paramref name="readName"/> propagates and nothing is
    /// cached, so the caller sees the same failure a direct read would give.
    /// A null or empty <paramref name="endpointId"/> is never cached: the read
    /// runs every time.
    /// </summary>
    public string GetOrRead(string? endpointId, Func<string> readName)
    {
        ArgumentNullException.ThrowIfNull(readName);

        if (string.IsNullOrEmpty(endpointId))
            return readName();

        long generation;
        lock (_lock)
        {
            if (_names.TryGetValue(endpointId, out var cached))
                return cached;
            generation = _generation;
        }

        // Read outside the lock: it is a slow COM call, and a notification
        // thread must not wait on it to invalidate.
        var name = readName() ?? string.Empty;

        lock (_lock)
        {
            if (generation == _generation)
                _names[endpointId] = name;
        }

        return name;
    }

    /// <summary>
    /// Drops the entry for one endpoint (e.g. its properties changed).
    /// </summary>
    public void Invalidate(string? endpointId)
    {
        if (string.IsNullOrEmpty(endpointId))
            return;

        lock (_lock)
        {
            _names.Remove(endpointId);
            _generation++;
        }
    }

    /// <summary>
    /// Drops every entry (e.g. the device list changed).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _names.Clear();
            _generation++;
        }
    }

    /// <summary>
    /// Number of cached entries. For tests.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _names.Count;
            }
        }
    }
}
