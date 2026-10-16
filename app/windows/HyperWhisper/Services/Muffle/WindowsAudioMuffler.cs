// WINDOWS AUDIO MUFFLER
// "Muffle Audio": while the user dictates, every other app keeps playing, low-passed, as if from
// the next room. The macOS reference is AudioMuffler.swift; the filter is the shared MuffleFilter.
//
// Route (measured on Windows 11 build 26200):
//   1. Every other process with a session on the default render endpoint has its session volume
//      scaled by VolumeFactor (1e-4, -80 dB). The endpoint is left with a -80 dB leak.
//   2. Process-loopback capture (INCLUDE_TARGET_PROCESS_TREE) of each such process is taken AFTER
//      the session volume, linearly and unquantised, so the capture times 1/VolumeFactor is exactly
//      what the user heard at their own per-app level.
//   3. The captures are mixed, low-passed and rendered from our own process on a private,
//      non-persisted session. HyperWhisper's own sounds are never captured, so they stay clear.
//      The captures sit on a private session too: a capture is scaled by its own session's
//      volume, and HyperWhisper's default session is the chimes' (0.5) and the mixer's.
// Windows persists per-app volume, so every change is written to the MuffleRestoreJournal first.
//
// Threads: Start, session handling and Dispose run on control threads under _gate (always MTA).
// The audio thread only drains captures, mixes, filters and renders: no allocation, no locks.

using System.Diagnostics;
using System.Runtime.InteropServices;
using HyperWhisper.Models;
using HyperWhisper.Platform.Abstractions.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace HyperWhisper.Services.Muffle;

public sealed unsafe class WindowsAudioMuffler : IDisposable
{
    /// <summary>Session volume multiplier while muffled; the capture is multiplied by its reciprocal.</summary>
    public const float VolumeFactor = 1e-4f;
    /// <summary>A captured sample above VolumeFactor times this did not come through a scaled session.</summary>
    private const float TripHeadroom = 2f;
    internal const int MaxSources = 32;
    private const long RenderBufferHns = 400_000; // 40 ms
    private const double RenderTargetSeconds = 0.030;
    // Our replay fades in when the first capture carries sound and out before teardown, and the
    // apps' session volumes ramp instead of stepping: a hard step at either end was a click.
    internal const double RenderFadeSeconds = 0.015;
    internal const double VolumeRampSeconds = 0.050;
    private const int VolumeRampSteps = 20;
    private static readonly TimeSpan FadeOutWait = TimeSpan.FromMilliseconds(250);
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    // Our render stream gets its own session, so the muffled mix never inherits a per-app volume
    // the user set on HyperWhisper's chimes; NoPersist keeps it out of the volume store.
    private static readonly Guid RenderSessionGuid = new("8d3c2f51-0b6e-4a7a-9f24-6c1e5b7d9a03");

    private readonly object _gate = new();
    private readonly MuffleRestoreJournal _journal;
    private readonly Func<uint, bool>? _processFilter;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly ProcessLoopbackSource?[] _sources = new ProcessLoopbackSource?[MaxSources];
    private readonly List<ScaledSession> _scaled = new();
    private readonly HashSet<uint> _capturedPids = new();
    private readonly HashSet<uint> _skippedPids = new();
    private int _sourceCount;

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioSessionManager? _sessionManager;
    private AudioSessionManager.SessionCreatedDelegate? _sessionCreatedHandler;
    private AudioClient? _audioClient;
    private AudioRenderClient? _renderClient;
    private EventWaitHandle? _renderEvent;
    private IntPtr _formatPtr;
    private Thread? _audioThread;
    private int _bufferFrames;
    private int _targetFrames;
    private volatile bool _stopRequested;
    private volatile bool _faulted;
    private string? _faultMessage;
    private const int EngageReleased = 0;
    private const int EngageRequested = 1;
    private const int EngageDone = 2;
    private int _engageState;
    private volatile bool _fadeOutRequested;
    private readonly ManualResetEventSlim _fadedOut = new(false);
    private long _renderedFrames;
    private float _outputPeak;
    private bool _disposed;

    private WindowsAudioMuffler(MuffleRestoreJournal journal, Func<uint, bool>? processFilter)
    {
        _journal = journal;
        _processFilter = processFilter;
    }

    public string DeviceId { get; private set; } = string.Empty;
    public string DeviceName { get; private set; } = string.Empty;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public MuffleFilter Filter { get; private set; } = null!;
    public int SourceCount => Volatile.Read(ref _sourceCount);
    public bool IsFaulted => _faulted;
    /// <summary>True once a release has glided the cutoff back open.</summary>
    public bool IsOpen => Filter.IsOpen;

    /// <summary>
    /// Muffles every other app on the default output until <see cref="Release"/> and
    /// <see cref="Dispose"/>. Fails (and changes nothing) when process loopback is missing
    /// (Windows build &lt; 20348), the endpoint cannot be opened, or no capture can start.
    /// <paramref name="processFilter"/> limits which process ids are muffled (diagnostics and
    /// device tests only; the app passes null).
    /// </summary>
    public static Result<WindowsAudioMuffler> Start(MuffleRestoreJournal journal, Func<uint, bool>? processFilter = null)
    {
        ArgumentNullException.ThrowIfNull(journal);

        if (!ProcessLoopbackNative.IsSupported)
        {
            return Result<WindowsAudioMuffler>.Failure(
                $"process loopback needs Windows build {ProcessLoopbackNative.MinimumWindowsBuild} or later (this is {Environment.OSVersion.Version.Build})");
        }

        return RunInMta(() => StartCore(journal, processFilter));
    }

    /// <summary>
    /// Glide the cutoff down to the muffled frequency (thread-safe). The glide starts once a capture
    /// carries sound, so it is heard instead of happening during the start-up gap.
    /// </summary>
    public void Engage() => Volatile.Write(ref _engageState, EngageRequested);

    /// <summary>Glide back open. Tear down with <see cref="Dispose"/> after <see cref="MuffleFilter.TeardownDelay"/>.</summary>
    public void Release()
    {
        Volatile.Write(ref _engageState, EngageReleased);
        Filter.Release();
    }

    private static Result<WindowsAudioMuffler> StartCore(MuffleRestoreJournal journal, Func<uint, bool>? processFilter)
    {
        var stopwatch = Stopwatch.StartNew();
        var muffler = new WindowsAudioMuffler(journal, processFilter);
        try
        {
            var opened = muffler.OpenRender();
            if (opened.IsFailure)
            {
                muffler.Dispose();
                return Result<WindowsAudioMuffler>.Failure(opened.Error ?? "could not open the default output");
            }

            muffler.Engage();
            muffler.StartAudioThread();
            muffler.SubscribeToNewSessions();
            var renderVolume = muffler.EnsureUnityRenderSessionVolume();

            var (needed, captured) = muffler.AttachExistingSessions();
            if (needed > 0 && captured == 0)
            {
                muffler.Dispose();
                return Result<WindowsAudioMuffler>.Failure("no process loopback capture could start");
            }

            if (needed == 0)
            {
                // Nothing plays yet: prove process loopback works now, so a failure falls back to
                // muting instead of surfacing only when an app starts playing mid-recording.
                var probe = ProcessLoopbackSource.Open(muffler._ownProcessId, muffler._formatPtr, muffler.SampleRate, muffler.Channels, 1f, 1f);
                if (probe.IsFailure)
                {
                    muffler.Dispose();
                    return Result<WindowsAudioMuffler>.Failure(probe.Error ?? "process loopback is unavailable");
                }

                probe.Value!.Dispose();
            }

            var captureVolume = muffler.SourceCount > 0 ? muffler._sources[0]!.SessionVolume : float.NaN;
            LoggingService.Info($"WindowsAudioMuffler: Muffling {muffler.SourceCount} app(s) on {muffler.DeviceName} ({muffler.SampleRate} Hz, {muffler.Channels} ch, start {stopwatch.ElapsedMilliseconds} ms, render session vol {renderVolume:G4}, capture session vol {captureVolume:G4})");
            return Result<WindowsAudioMuffler>.Success(muffler);
        }
        catch (Exception ex)
        {
            muffler.Dispose();
            return Result<WindowsAudioMuffler>.Failure($"muffle start failed: {ex.Message}", ex);
        }
    }

    private Result OpenRender()
    {
        _enumerator = new MMDeviceEnumerator();
        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            return Result.Failure($"no default output device: {ex.Message}", ex);
        }

        DeviceId = _device.ID;
        DeviceName = _device.FriendlyName;

        WaveFormat mixFormat;
        using (var formatClient = _device.AudioClient)
        {
            mixFormat = formatClient.MixFormat;
        }

        if (mixFormat.Channels < 1 || mixFormat.Channels > MuffleFilter.MaxChannels || mixFormat.SampleRate < 8_000)
        {
            return Result.Failure($"unsupported mix format {mixFormat}");
        }

        SampleRate = mixFormat.SampleRate;
        Channels = mixFormat.Channels;
        // The shared-mode mix format is float32 on every endpoint seen so far; anything else is
        // asked for as float32 at the same rate and layout and converted by the audio engine.
        var renderFormat = IsFloat32(mixFormat) ? mixFormat : new WaveFormatExtensible(SampleRate, 32, Channels);

        _audioClient = _device.AudioClient;
        _audioClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.NoPersist
                | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            RenderBufferHns,
            0,
            renderFormat,
            RenderSessionGuid);
        _renderEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _audioClient.SetEventHandle(_renderEvent.SafeWaitHandle.DangerousGetHandle());
        _renderClient = _audioClient.AudioRenderClient;
        _bufferFrames = _audioClient.BufferSize;
        _targetFrames = Math.Clamp((int)(SampleRate * RenderTargetSeconds), 1, _bufferFrames);
        _formatPtr = WaveFormat.MarshalToPtr(renderFormat);
        Filter = new MuffleFilter(SampleRate, Channels);
        return Result.Success();
    }

    private void StartAudioThread()
    {
        _audioThread = new Thread(AudioThreadMain)
        {
            IsBackground = true,
            Name = "HyperWhisper muffle",
            Priority = ThreadPriority.Highest,
        };
        _audioThread.SetApartmentState(ApartmentState.MTA);
        _audioThread.Start();
        _audioClient!.Start();
    }

    private void AudioThreadMain()
    {
        var avrtHandle = IntPtr.Zero;
        try
        {
            var taskIndex = 0u;
            avrtHandle = ProcessLoopbackNative.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"WindowsAudioMuffler: MMCSS unavailable: {ex.Message}");
        }

        try
        {
            var client = _audioClient!;
            var render = _renderClient!;
            var renderEvent = _renderEvent!;
            var filter = Filter;
            var channels = Channels;
            var bufferFrames = _bufferFrames;
            var targetFrames = _targetFrames;
            var sources = _sources;
            var fadeStep = 1f / (float)Math.Max(1, SampleRate * RenderFadeSeconds);
            var fadeGain = 0f;
            var signalSeen = false;
            long silentFramesAfterFade = 0;

            while (!_stopRequested)
            {
                renderEvent.WaitOne(20);
                if (_stopRequested)
                {
                    break;
                }

                var sourceCount = Volatile.Read(ref _sourceCount);
                for (var i = 0; i < sourceCount; i++)
                {
                    sources[i]!.Drain();
                }

                if (!signalSeen && AnySignal(sources, sourceCount))
                {
                    signalSeen = true;
                }

                var fadingOut = _fadeOutRequested;
                if (fadingOut && (!signalSeen || (fadeGain == 0f && silentFramesAfterFade >= targetFrames)))
                {
                    // Nothing was ever replayed, or the fade and every frame queued before it
                    // have played: the apps' own audio can come back without overlapping ours.
                    _fadedOut.Set();
                }

                if (Volatile.Read(ref _engageState) == EngageRequested && AnySignal(sources, sourceCount))
                {
                    filter.Engage();
                    // A Release that landed between the read and here wins.
                    if (Interlocked.CompareExchange(ref _engageState, EngageDone, EngageRequested) != EngageRequested)
                    {
                        filter.Release();
                    }
                }

                var padding = client.CurrentPadding;
                var frames = Math.Min(targetFrames - padding, bufferFrames - padding);
                if (frames <= 0)
                {
                    continue;
                }

                var output = new Span<float>((void*)render.GetBuffer(frames), frames * channels);
                output.Clear();
                for (var i = 0; i < sourceCount; i++)
                {
                    sources[i]!.MixInto(output);
                }

                filter.Process(output);
                var fadeTarget = fadingOut || !signalSeen ? 0f : 1f;
                var blockSilent = fadingOut && fadeGain == 0f;
                var peak = _outputPeak;
                for (var frame = 0; frame < frames; frame++)
                {
                    if (fadeGain != fadeTarget)
                    {
                        fadeGain = fadeTarget > fadeGain
                            ? Math.Min(fadeTarget, fadeGain + fadeStep)
                            : Math.Max(fadeTarget, fadeGain - fadeStep);
                    }

                    var start = frame * channels;
                    for (var c = 0; c < channels; c++)
                    {
                        var sample = Math.Clamp(output[start + c] * fadeGain, -1f, 1f);
                        output[start + c] = sample;
                        if (Math.Abs(sample) > peak) peak = Math.Abs(sample);
                    }
                }

                _outputPeak = peak;
                if (blockSilent)
                {
                    silentFramesAfterFade += frames;
                }

                render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
                _renderedFrames += frames;
            }
        }
        catch (Exception ex)
        {
            // Device removed, default changed, audio service restarted: the muffled apps would stay
            // at -80 dB with nothing replaying them, so give them their volume back now.
            _faultMessage = ex.Message;
            _faulted = true;
            ThreadPool.QueueUserWorkItem(_ => OnAudioFault());
        }
        finally
        {
            if (avrtHandle != IntPtr.Zero)
            {
                ProcessLoopbackNative.AvRevertMmThreadCharacteristics(avrtHandle);
            }
        }
    }

    private static bool AnySignal(ProcessLoopbackSource?[] sources, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (sources[i]!.HasSignal) return true;
        }

        return false;
    }

    private void OnAudioFault()
    {
        LoggingService.Warn($"WindowsAudioMuffler: Output stream failed ({_faultMessage}); restoring app volumes early");
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            RestoreAllLocked("output stream failed");
        }
    }

    // ---------------------------------------------------------------- sessions

    private void SubscribeToNewSessions()
    {
        _sessionManager = _device!.AudioSessionManager;
        _sessionManager.RefreshSessions();
        _sessionCreatedHandler = (_, newSession) => OnSessionCreated(newSession);
        _sessionManager.OnSessionCreated += _sessionCreatedHandler;
    }

    // Our render session is private and NoPersist, so it should start at 1.0 and unmuted. Its
    // volume scales the whole muffled mix, so anything else is logged and put back to 1.0.
    // Returns the volume found (NaN when the session could not be read).
    private float EnsureUnityRenderSessionVolume()
    {
        try
        {
            var sessions = _sessionManager!.Sessions;
            var guid = RenderSessionGuid.ToString("B");
            var count = sessions.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session.GetProcessID != _ownProcessId
                        || session.GetSessionInstanceIdentifier?.Contains(guid, StringComparison.OrdinalIgnoreCase) != true)
                    {
                        continue;
                    }

                    var volume = session.SimpleAudioVolume;
                    var level = volume.Volume;
                    if (Math.Abs(level - 1f) > 1e-6f || volume.Mute)
                    {
                        LoggingService.Warn($"WindowsAudioMuffler: Render session was at {level:G4} (mute {volume.Mute}); setting it to 1");
                        volume.Volume = 1f;
                        volume.Mute = false;
                    }

                    return level;
                }
                catch (Exception ex)
                {
                    LoggingService.Debug($"WindowsAudioMuffler: Skipped a session while checking the render session: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"WindowsAudioMuffler: Could not check the render session volume: {ex.Message}");
        }

        return float.NaN;
    }

    private void OnSessionCreated(IAudioSessionControl newSession)
    {
        // The notification thread must return at once; activation waits on the audio service.
        var control = new AudioSessionControl(newSession);
        ThreadPool.QueueUserWorkItem(_ => HandleNewSession(control));
    }

    private void HandleNewSession(AudioSessionControl session)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _faulted)
                {
                    return;
                }

                var pid = session.GetProcessID;
                if (IsAlwaysExcluded(pid, session))
                {
                    return;
                }

                var outcome = AttachProcessLocked(pid, new List<AudioSessionControl> { session }, ProcessTree.Snapshot(), CollectForeignSessionPids());
                LoggingService.Info($"WindowsAudioMuffler: New session from {DescribeProcess(pid)}: {outcome}");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"WindowsAudioMuffler: Could not handle a new audio session: {ex.Message}");
        }
    }

    private (int Needed, int Captured) AttachExistingSessions()
    {
        lock (_gate)
        {
            var groups = new Dictionary<uint, List<AudioSessionControl>>();
            try
            {
                var sessions = _sessionManager!.Sessions;
                var count = sessions.Count;
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        var pid = session.GetProcessID;
                        if (IsAlwaysExcluded(pid, session))
                        {
                            continue;
                        }

                        if (!groups.TryGetValue(pid, out var list))
                        {
                            groups[pid] = list = new List<AudioSessionControl>();
                        }

                        list.Add(session);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"WindowsAudioMuffler: Skipped a session: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"WindowsAudioMuffler: Session enumeration failed: {ex.Message}");
            }

            var tree = ProcessTree.Snapshot();
            var foreign = CollectForeignSessionPids();
            int needed = 0, captured = 0;
            // Ancestors first, so a child is covered by its parent's tree capture, not captured twice.
            foreach (var pid in groups.Keys.OrderBy(tree.Depth))
            {
                var outcome = AttachProcessLocked(pid, groups[pid], tree, foreign);
                LoggingService.Debug($"WindowsAudioMuffler: {DescribeProcess(pid)}: {outcome}");
                if (outcome is AttachOutcome.Captured or AttachOutcome.CaptureFailed)
                {
                    needed++;
                }

                if (outcome == AttachOutcome.Captured)
                {
                    captured++;
                }
            }

            return (needed, captured);
        }
    }

    private bool IsAlwaysExcluded(uint pid, AudioSessionControl session) =>
        pid == 0 || pid == _ownProcessId || session.IsSystemSoundsSession
        || (_processFilter != null && !_processFilter(pid));

    private AttachOutcome AttachProcessLocked(uint pid, List<AudioSessionControl> sessions, ProcessTree tree, HashSet<uint> foreignPids)
    {
        if (_skippedPids.Contains(pid))
        {
            return AttachOutcome.Skipped;
        }

        var covered = _capturedPids.Contains(pid) || _capturedPids.Any(c => tree.IsAncestor(c, pid));
        string? skipReason = null;
        if (tree.IsAncestor(pid, _ownProcessId))
        {
            skipReason = "its capture would include HyperWhisper";
        }
        else if (foreignPids.Any(f => f == pid || tree.IsAncestor(pid, f)))
        {
            // Process loopback hears a process on EVERY endpoint; its other output would be
            // replayed here at +80 dB.
            skipReason = "it also plays on another output";
        }
        else if (!covered && _capturedPids.Any(c => tree.IsAncestor(pid, c)))
        {
            skipReason = "a child of it is already captured";
        }

        if (skipReason != null)
        {
            _skippedPids.Add(pid);
            LoggingService.Info($"WindowsAudioMuffler: Leaving {DescribeProcess(pid)} unmuffled: {skipReason}");
            return AttachOutcome.Skipped;
        }

        var scaledNow = new List<ScaledSession>();
        foreach (var session in sessions)
        {
            var scaled = ScaleSessionLocked(session, pid);
            if (scaled != null)
            {
                scaledNow.Add(scaled);
            }
        }

        if (scaledNow.Count == 0)
        {
            return AttachOutcome.NothingAudible;
        }

        if (covered)
        {
            return AttachOutcome.Covered;
        }

        if (_sourceCount >= MaxSources)
        {
            RestoreLocked(scaledNow, "too many apps");
            _skippedPids.Add(pid);
            return AttachOutcome.Skipped;
        }

        // The volume is already scaled, so the capture never replays unscaled audio times 1e4.
        var opened = ProcessLoopbackSource.Open(pid, _formatPtr, SampleRate, Channels, 1f / VolumeFactor, VolumeFactor * TripHeadroom);
        if (opened.IsFailure)
        {
            LoggingService.Warn($"WindowsAudioMuffler: Could not capture {DescribeProcess(pid)}: {opened.Error}");
            RestoreLocked(scaledNow, "capture failed");
            _skippedPids.Add(pid);
            return AttachOutcome.CaptureFailed;
        }

        var source = opened.Value!;
        var started = source.Start();
        if (started.IsFailure)
        {
            LoggingService.Warn($"WindowsAudioMuffler: Could not start capturing {DescribeProcess(pid)}: {started.Error}");
            source.Dispose();
            RestoreLocked(scaledNow, "capture failed");
            _skippedPids.Add(pid);
            return AttachOutcome.CaptureFailed;
        }

        _sources[_sourceCount] = source;
        Volatile.Write(ref _sourceCount, _sourceCount + 1);
        _capturedPids.Add(pid);
        return AttachOutcome.Captured;
    }

    private ScaledSession? ScaleSessionLocked(AudioSessionControl session, uint pid)
    {
        string instanceId;
        string sessionId;
        float current;
        float prior;
        try
        {
            if (session.State == AudioSessionState.AudioSessionStateExpired)
            {
                return null;
            }

            instanceId = session.GetSessionInstanceIdentifier ?? string.Empty;
            sessionId = session.GetSessionIdentifier ?? string.Empty;
            if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(sessionId))
            {
                // Its journal entry could never be matched back to it after a crash.
                LoggingService.Info($"WindowsAudioMuffler: Not muffling a session of pid {pid}: it has no identifier to restore it by");
                return null;
            }

            if (!string.IsNullOrEmpty(instanceId)
                && _scaled.Any(s => string.Equals(s.Entry.SessionInstanceIdentifier, instanceId, StringComparison.Ordinal)))
            {
                return null;
            }

            // A crash leftover is put back first, so it is never scaled twice.
            _journal.TryRecoverStale(session);

            if (session.SimpleAudioVolume.Mute)
            {
                return null;
            }

            current = session.SimpleAudioVolume.Volume;
            prior = PriorVolumeFor(current, sessionId, _scaled.Select(s => s.Entry));
            if (prior != current)
            {
                LoggingService.Info($"WindowsAudioMuffler: Another session of pid {pid} opened at our lowered volume ({current:G4}); remembering the app's own {prior:G4}");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"WindowsAudioMuffler: Could not read a session of pid {pid}: {ex.Message}");
            return null;
        }

        if (prior <= 0)
        {
            return null;
        }

        var entry = new MuffleRestoreEntry(sessionId, instanceId, pid, prior, prior * VolumeFactor, DateTimeOffset.UtcNow);
        if (_journal.Add(entry).IsFailure)
        {
            LoggingService.Warn($"WindowsAudioMuffler: Not muffling pid {pid}: the restore journal could not be written");
            return null;
        }

        try
        {
            var volume = session.SimpleAudioVolume;
            // A session that opened at our value already (see PriorVolumeFor) is not ramped.
            if (!MuffleRestoreJournal.StillAtSetVolume(current, entry.SetVolume))
            {
                RampVolumes(new[] { new VolumeRamp(volume, current, entry.SetVolume) });
            }

            volume.Volume = entry.SetVolume;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"WindowsAudioMuffler: Could not scale a session of pid {pid}: {ex.Message}");
            try
            {
                // The ramp may have got part of the way down.
                session.SimpleAudioVolume.Volume = current;
                _journal.Remove(entry);
            }
            catch (Exception restoreEx)
            {
                LoggingService.Debug($"WindowsAudioMuffler: Could not put pid {pid} back after a failed scale; kept for retry: {restoreEx.Message}");
                _journal.MarkStale(entry);
                _journal.WatchForStaleSessions();
            }

            return null;
        }

        var scaled = new ScaledSession(session, entry);
        _scaled.Add(scaled);
        return scaled;
    }

    /// <summary>
    /// The volume to remember for a session about to be scaled. Windows keeps one volume per app
    /// (session identifier), so a second session of an app we already lowered opens at our value;
    /// taken as its prior, it would be scaled again and "restored" to p x 1e-4 for good. When an
    /// entry for the same identifier is still in force and the session sits at its set volume,
    /// that entry's prior is used instead.
    /// </summary>
    internal static float PriorVolumeFor(float current, string sessionId, IEnumerable<MuffleRestoreEntry> scaledEntries)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return current;
        }

        foreach (var entry in scaledEntries)
        {
            if (string.Equals(entry.SessionIdentifier, sessionId, StringComparison.Ordinal)
                && MuffleRestoreJournal.StillAtSetVolume(current, entry.SetVolume))
            {
                return entry.PriorVolume;
            }
        }

        return current;
    }

    private void RestoreAllLocked(string reason)
    {
        RestoreLocked(_scaled.ToList(), reason);
    }

    // Puts back only volumes we changed, and only while they still hold the value we set: a user
    // who moved an app's slider mid-recording keeps their own choice.
    private void RestoreLocked(List<ScaledSession> sessions, string reason)
    {
        int restored = 0, leftAlone = 0, failed = 0;
        var toRestore = new List<(ScaledSession Scaled, SimpleAudioVolume Volume)>();
        foreach (var scaled in sessions)
        {
            _scaled.Remove(scaled);
            try
            {
                var volume = scaled.Control.SimpleAudioVolume;
                if (scaled.Control.State == AudioSessionState.AudioSessionStateExpired)
                {
                    // The app closed this session. A write to it can succeed without reaching the
                    // per-app volume Windows will reopen the app at, so the entry is kept: the
                    // session watcher or the next start restores it (or drops it if the app comes
                    // back at its own volume).
                    try
                    {
                        volume.Volume = scaled.Entry.PriorVolume;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"WindowsAudioMuffler: Write to pid {scaled.Entry.ProcessId}'s expired session failed: {ex.Message}");
                    }

                    failed++;
                    _journal.MarkStale(scaled.Entry);
                    LoggingService.Info($"WindowsAudioMuffler: pid {scaled.Entry.ProcessId}'s session has expired; kept for retry");
                    continue;
                }

                var current = volume.Volume;
                if (MuffleRestoreJournal.StillAtSetVolume(current, scaled.Entry.SetVolume))
                {
                    toRestore.Add((scaled, volume));
                    continue;
                }

                leftAlone++;
                LoggingService.Info($"WindowsAudioMuffler: pid {scaled.Entry.ProcessId} volume changed during recording ({current:G4}); leaving it");
                _journal.Remove(scaled.Entry);
            }
            catch (Exception ex)
            {
                // The session is gone; the journal keeps the entry and retries when the app reopens.
                failed++;
                _journal.MarkStale(scaled.Entry);
                LoggingService.Warn($"WindowsAudioMuffler: Could not restore pid {scaled.Entry.ProcessId}; kept for retry: {ex.Message}");
            }
        }

        // All the apps ramp back together, so the restore costs one ramp however many there are.
        RampVolumes(toRestore.Select(r => new VolumeRamp(r.Volume, r.Scaled.Entry.SetVolume, r.Scaled.Entry.PriorVolume)).ToList());
        foreach (var (scaled, volume) in toRestore)
        {
            try
            {
                volume.Volume = scaled.Entry.PriorVolume;
                restored++;
                _journal.Remove(scaled.Entry);
            }
            catch (Exception ex)
            {
                failed++;
                _journal.MarkStale(scaled.Entry);
                LoggingService.Warn($"WindowsAudioMuffler: Could not restore pid {scaled.Entry.ProcessId}; kept for retry: {ex.Message}");
            }
        }

        if (sessions.Count > 0)
        {
            LoggingService.Info($"WindowsAudioMuffler: Restored {restored} session volume(s), left {leftAlone}, kept {failed} for retry ({reason})");
        }

        if (failed > 0)
        {
            _journal.WatchForStaleSessions();
        }
    }

    // Pids with an audible session on any OTHER active render endpoint.
    private HashSet<uint> CollectForeignSessionPids()
    {
        var pids = new HashSet<uint>();
        try
        {
            var devices = _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var deviceCount = devices.Count;
            for (var d = 0; d < deviceCount; d++)
            {
                MMDevice? device = null;
                try
                {
                    device = devices[d];
                    if (string.Equals(device.ID, DeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sessions = device.AudioSessionManager.Sessions;
                    var count = sessions.Count;
                    for (var i = 0; i < count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            var pid = session.GetProcessID;
                            if (pid == 0 || pid == _ownProcessId || session.State == AudioSessionState.AudioSessionStateExpired)
                            {
                                continue;
                            }

                            if (!session.SimpleAudioVolume.Mute && session.SimpleAudioVolume.Volume > 0)
                            {
                                pids.Add(pid);
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Debug($"WindowsAudioMuffler: Skipped a session on another endpoint: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Debug($"WindowsAudioMuffler: Skipped an endpoint: {ex.Message}");
                }
                finally
                {
                    device?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"WindowsAudioMuffler: Endpoint enumeration failed: {ex.Message}");
        }

        return pids;
    }

    // ---------------------------------------------------------------- teardown

    /// <summary>Stops the stream, restores the volumes we changed and releases everything. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        RunInMta(() =>
        {
            DisposeCore();
            return true;
        });
    }

    private void DisposeCore()
    {
        if (_sessionManager != null && _sessionCreatedHandler != null)
        {
            try
            {
                _sessionManager.OnSessionCreated -= _sessionCreatedHandler;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"WindowsAudioMuffler: Unsubscribe failed: {ex.Message}");
            }
        }

        // Fade our replay out and let it play out BEFORE the volumes come back: the apps' own audio
        // overlapping our copy is +6 dB, and a hard stop is a click.
        var fadeWatch = Stopwatch.StartNew();
        var fadedOut = false;
        if (_audioThread != null && !_faulted)
        {
            _fadeOutRequested = true;
            fadedOut = _fadedOut.Wait(FadeOutWait);
        }

        var fadeMs = fadeWatch.ElapsedMilliseconds;
        _stopRequested = true;
        var joined = true;
        if (_audioThread != null)
        {
            try { _renderEvent?.Set(); } catch (ObjectDisposedException) { }
            joined = _audioThread.Join(TimeSpan.FromSeconds(2));
            if (!joined)
            {
                LoggingService.Warn("WindowsAudioMuffler: Audio thread did not stop; leaking its clients");
            }
        }

        try
        {
            _audioClient?.Stop();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"WindowsAudioMuffler: Render stop failed: {ex.Message}");
        }

        // Stop replaying BEFORE the volumes come back, or the restored audio would be doubled.
        var count = Volatile.Read(ref _sourceCount);
        int tripped = 0, underruns = 0, failedSources = 0, packets = 0, silentPackets = 0;
        var inputPeak = 0f;
        for (var i = count - 1; i >= 0; i--)
        {
            var source = _sources[i];
            if (source == null) continue;
            tripped += source.TrippedPackets;
            underruns += source.Underruns;
            packets += source.Packets;
            silentPackets += source.SilentPackets;
            inputPeak = Math.Max(inputPeak, source.PeakInput);
            if (source.Failed) failedSources++;
            if (joined)
            {
                source.Dispose();
                _sources[i] = null;
            }
        }

        lock (_gate)
        {
            RestoreAllLocked(_faulted ? "after output failure" : "muffle ended");
        }

        if (joined)
        {
            SafeDispose(ref _renderClient);
            SafeDispose(ref _audioClient);
            SafeDispose(ref _renderEvent);
            _fadedOut.Dispose();
        }

        SafeDispose(ref _device);
        SafeDispose(ref _enumerator);
        if (_formatPtr != IntPtr.Zero && joined)
        {
            Marshal.FreeHGlobal(_formatPtr);
            _formatPtr = IntPtr.Zero;
        }

        if (count > 0 || _renderedFrames > 0)
        {
            var seconds = SampleRate > 0 ? _renderedFrames / (double)SampleRate : 0;
            LoggingService.Info($"WindowsAudioMuffler: Stopped ({count} capture(s), {packets} packet(s) of which {silentPackets} silent, input peak {Dbfs(inputPeak / VolumeFactor)}, output peak {Dbfs(_outputPeak)}, {seconds:F1}s rendered, {underruns} underrun(s), {tripped} unscaled packet(s) silenced, {failedSources} capture(s) failed, fade-out {(fadedOut ? $"{fadeMs} ms" : "skipped")})");
        }
    }

    private void SafeDispose<T>(ref T? resource) where T : class, IDisposable
    {
        var temp = resource;
        resource = null;

        try
        {
            temp?.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"WindowsAudioMuffler: Dispose failed for {typeof(T).Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    // WASAPI and the session objects are free-threaded, but anything created here must not belong
    // to the WPF STA: a call marshalled to a busy UI thread would stall the restore.
    private static T RunInMta<T>(Func<T> work) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? Task.Run(work).GetAwaiter().GetResult()
            : work();

    private static bool IsFloat32(WaveFormat format) =>
        format.BitsPerSample == 32
        && (format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat));

    private static string Dbfs(float value) => value > 0 ? $"{20 * Math.Log10(value):F1} dBFS" : "-inf dBFS";

    private static string DescribeProcess(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return $"{process.ProcessName} (pid {pid})";
        }
        catch
        {
            return $"pid {pid}";
        }
    }

    // Walks each volume linearly from From toward To over VolumeRampSeconds, stopping one step
    // short: the caller writes the final value itself and handles its failure. The engine reads a
    // session volume once per period (~10 ms), so a single write is a step and a click.
    // Thread.Sleep(1) can take a whole 15.6 ms timer tick, so the steps are timed by spinning.
    private static void RampVolumes(IReadOnlyList<VolumeRamp> ramps)
    {
        if (ramps.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        for (var step = 1; step < VolumeRampSteps; step++)
        {
            var dueSeconds = VolumeRampSeconds * step / VolumeRampSteps;
            while (stopwatch.Elapsed.TotalSeconds < dueSeconds)
            {
                Thread.Sleep(0);
            }

            var fraction = (float)step / VolumeRampSteps;
            foreach (var ramp in ramps)
            {
                try
                {
                    ramp.Volume.Volume = ramp.From + ((ramp.To - ramp.From) * fraction);
                }
                catch (Exception ex)
                {
                    LoggingService.Debug($"WindowsAudioMuffler: A volume ramp step failed: {ex.Message}");
                }
            }
        }

        while (stopwatch.Elapsed.TotalSeconds < VolumeRampSeconds)
        {
            Thread.Sleep(0);
        }
    }

    private sealed record VolumeRamp(SimpleAudioVolume Volume, float From, float To);

    private sealed record ScaledSession(AudioSessionControl Control, MuffleRestoreEntry Entry);

    private enum AttachOutcome
    {
        Captured,
        Covered,
        NothingAudible,
        Skipped,
        CaptureFailed,
    }
}

/// <summary>A snapshot of parent process ids, for process-tree capture decisions.</summary>
internal sealed class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const int MaxDepth = 64;
    private readonly Dictionary<uint, uint> _parents;

    private ProcessTree(Dictionary<uint, uint> parents)
    {
        _parents = parents;
    }

    public static ProcessTree Snapshot()
    {
        var parents = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return new ProcessTree(parents);
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    parents[entry.th32ProcessID] = entry.th32ParentProcessID;
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return new ProcessTree(parents);
    }

    /// <summary>True when <paramref name="ancestor"/> is a strict ancestor of <paramref name="pid"/>.</summary>
    public bool IsAncestor(uint ancestor, uint pid)
    {
        var current = pid;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (!_parents.TryGetValue(current, out var parent) || parent == 0 || parent == current)
            {
                return false;
            }

            if (parent == ancestor)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    public int Depth(uint pid)
    {
        var depth = 0;
        var current = pid;
        while (depth < MaxDepth && _parents.TryGetValue(current, out var parent) && parent != 0 && parent != current)
        {
            depth++;
            current = parent;
        }

        return depth;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
