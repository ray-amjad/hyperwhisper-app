// SOUND EFFECTS SERVICE
// Plays audio feedback sounds when recording starts and stops.
// Uses NAudio for playback with volume control (matching macOS behavior).

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using NAudio.Wave;

namespace HyperWhisper.Services;

public sealed class SoundEffectsService : IDisposable
{
    private static readonly Lazy<SoundEffectsService> _instance = new(() => new SoundEffectsService());
    public static SoundEffectsService Instance => _instance.Value;

    private const float PlaybackVolume = 0.5f;

    private byte[]? _startSoundData;
    private byte[]? _stopSoundData;
    private bool _disposed;

    // In-flight playbacks, so their native NAudio handles can be flushed on
    // shutdown if PlaybackStopped never fires (e.g. app closed mid-chime).
    private readonly ConcurrentDictionary<WaveOutEvent, Playback> _activePlaybacks = new();

    private SoundEffectsService()
    {
        _startSoundData = LoadSoundData("HyperWhisper.Assets.Sounds.start1.wav");
        _stopSoundData = LoadSoundData("HyperWhisper.Assets.Sounds.stop2.wav");

        LoggingService.Debug($"SoundEffectsService: Initialized (start={_startSoundData != null}, stop={_stopSoundData != null})");
    }

    /// <summary>
    /// Disposes the service if it was created. Safe to call even if never used.
    /// </summary>
    public static void Shutdown()
    {
        if (_instance.IsValueCreated)
            _instance.Value.Dispose();
    }

    public void PlayStartSound()
    {
        if (!SettingsService.Instance.EnableSoundEffects) return;
        if (_startSoundData == null) return;

        PlaySound(_startSoundData);
    }

    public void PlayStopSound()
    {
        if (!SettingsService.Instance.EnableSoundEffects) return;
        if (_stopSoundData == null) return;

        PlaySound(_stopSoundData);
    }

    private void PlaySound(byte[] wavData)
    {
        if (_disposed) return;

        MemoryStream? ms = null;
        WaveFileReader? reader = null;
        WaveOutEvent? waveOut = null;
        Playback? playback = null;
        try
        {
            ms = new MemoryStream(wavData);
            reader = new WaveFileReader(ms);
            waveOut = new WaveOutEvent();

            waveOut.Volume = PlaybackVolume;
            waveOut.Init(reader);

            // Clean up after playback completes.
            playback = new Playback(waveOut, reader, ms, OnPlaybackStopped);
            _activePlaybacks[waveOut] = playback;

            waveOut.Play();
        }
        catch (Exception ex)
        {
            // Init/Play can throw (device busy, format mismatch, USB DAC
            // hot-unplug). PlaybackStopped never fires in that case, so dispose
            // the native handles here to avoid leaking them.
            LoggingService.Warn($"SoundEffectsService: Failed to play sound: {ex.Message}");
            if (waveOut != null) _activePlaybacks.TryRemove(waveOut, out _);
            if (playback != null)
            {
                playback.Dispose();
            }
            else
            {
                SafeDispose(ref waveOut);
                SafeDispose(ref reader);
                SafeDispose(ref ms);
            }
        }
    }

    private void OnPlaybackStopped(Playback playback)
    {
        if (playback.WaveOut is { } waveOut)
            _activePlaybacks.TryRemove(waveOut, out _);

        playback.Dispose();
    }

    /// <summary>
    /// Owns the three native NAudio handles for a single playback and disposes
    /// them in reverse order of creation.
    /// </summary>
    private sealed class Playback : IDisposable
    {
        public WaveOutEvent? WaveOut => _waveOut;
        private WaveOutEvent? _waveOut;
        private WaveFileReader? _reader;
        private MemoryStream? _ms;
        private readonly EventHandler<StoppedEventArgs> _playbackStoppedHandler;
        private bool _disposed;

        public Playback(WaveOutEvent waveOut, WaveFileReader reader, MemoryStream ms, Action<Playback> onPlaybackStopped)
        {
            _waveOut = waveOut;
            _reader = reader;
            _ms = ms;
            _playbackStoppedHandler = (_, _) => onPlaybackStopped(this);
            waveOut.PlaybackStopped += _playbackStoppedHandler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_waveOut != null)
            {
                try
                {
                    _waveOut.PlaybackStopped -= _playbackStoppedHandler;
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"SoundEffectsService: Failed to unsubscribe PlaybackStopped: {ex.Message}");
                }
                finally
                {
                    SafeDispose(ref _waveOut);
                }
            }
            else
            {
                SafeDispose(ref _waveOut);
            }

            SafeDispose(ref _reader);
            SafeDispose(ref _ms);
        }
    }

    private static void SafeDispose<T>(ref T? resource) where T : class, IDisposable
    {
        var temp = resource;
        resource = null;

        try
        {
            temp?.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"SoundEffectsService: Dispose failed for {typeof(T).Name}: {ex.Message}");
        }
    }

    private static byte[]? LoadSoundData(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                LoggingService.Warn($"SoundEffectsService: Resource not found: {resourceName}");
                return null;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"SoundEffectsService: Failed to load {resourceName}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // App.OnExit calls this on the UI thread. Do not synchronously dispose
        // active WaveOutEvent instances here: WinMM reset/close can block when
        // an output device is wedged or unplugged. Completed playbacks still
        // dispose themselves via PlaybackStopped; anything still playing during
        // process exit is left to the OS.
        foreach (var entry in _activePlaybacks)
        {
            _activePlaybacks.TryRemove(entry.Key, out _);
        }

        _startSoundData = null;
        _stopSoundData = null;

        LoggingService.Debug("SoundEffectsService: Disposed");
    }
}
