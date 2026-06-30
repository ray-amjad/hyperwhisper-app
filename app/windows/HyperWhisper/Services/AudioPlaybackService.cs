using System.IO;
using NAudio.Wave;

namespace HyperWhisper.Services;

/// <summary>
/// AUDIO PLAYBACK SERVICE
///
/// Provides simple audio playback functionality using NAudio.
/// Used by the history detail view to play back recorded audio.
///
/// FEATURES:
/// - Play/Pause/Stop controls
/// - Position tracking
/// - Automatic cleanup on dispose
///
/// USAGE:
/// 1. Create instance
/// 2. Call Load(path) to load an audio file
/// 3. Call Play() to start playback
/// 4. Subscribe to PlaybackEnded for completion notification
/// 5. Dispose when done
///
/// THREAD SAFETY:
/// - NAudio handles threading internally
/// - Events are fired on NAudio's background thread
/// - UI should marshal to main thread as needed
/// </summary>
public class AudioPlaybackService : IDisposable
{
    // =========================================================================
    // FIELDS
    // =========================================================================

    private WaveOutEvent? _waveOut;
    private AudioFileReader? _audioFile;
    private System.Timers.Timer? _positionTimer;
    private bool _disposed;

    // =========================================================================
    // PROPERTIES
    // =========================================================================

    /// <summary>Whether audio is currently playing.</summary>
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    /// <summary>Whether audio is loaded and ready to play.</summary>
    public bool IsLoaded => _audioFile != null;

    /// <summary>Current playback position.</summary>
    public TimeSpan CurrentPosition => _audioFile?.CurrentTime ?? TimeSpan.Zero;

    /// <summary>Total duration of the loaded audio.</summary>
    public TimeSpan TotalDuration => _audioFile?.TotalTime ?? TimeSpan.Zero;

    /// <summary>Path to the currently loaded audio file.</summary>
    public string? LoadedFilePath { get; private set; }

    // =========================================================================
    // EVENTS
    // =========================================================================

    /// <summary>Fired when playback ends (reached end of file).</summary>
    public event Action? PlaybackEnded;

    /// <summary>Fired periodically during playback with current position.</summary>
    public event Action<TimeSpan>? PositionChanged;

    /// <summary>
    /// Fired once after a file is loaded and its total duration is known.
    /// NAudio's AudioFileReader reports TotalTime synchronously, but consumers
    /// may have displayed a placeholder (e.g. 0:00) — this lets them refresh
    /// their bound denominator without polling.
    /// </summary>
    public event Action<TimeSpan>? DurationReady;

    /// <summary>Fired when playback stops because the audio output/device reports an error.</summary>
    public event Action<Exception>? PlaybackFailed;

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Loads an audio file for playback.
    /// Stops any currently playing audio first.
    /// </summary>
    /// <param name="audioPath">Path to the audio file</param>
    /// <returns>True if loaded successfully</returns>
    public bool Load(string audioPath)
    {
        try
        {
            // Stop and cleanup any existing playback
            Stop();
            Cleanup();

            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
            {
                LoggingService.Warn($"AudioPlaybackService: Cannot load - file not found: {audioPath}");
                return false;
            }

            // Load the new file
            _audioFile = new AudioFileReader(audioPath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioFile);

            // Subscribe to playback stopped event
            _waveOut.PlaybackStopped += OnPlaybackStopped;

            LoadedFilePath = audioPath;
            LoggingService.Debug($"AudioPlaybackService: Loaded {audioPath} ({TotalDuration:mm\\:ss})");

            // Notify listeners that the authoritative duration is now available.
            // Wrapped in try/catch so one bad handler cannot break the raising code.
            try
            {
                DurationReady?.Invoke(TotalDuration);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"AudioPlaybackService: DurationReady handler threw: {ex.Message}", ex);
            }

            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"AudioPlaybackService: Failed to load {audioPath}: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Starts or resumes playback.
    /// </summary>
    public void Play()
    {
        if (_waveOut == null || _audioFile == null)
        {
            LoggingService.Warn("AudioPlaybackService: Cannot play - no audio loaded");
            return;
        }

        if (_waveOut.PlaybackState == PlaybackState.Playing)
        {
            return; // Already playing
        }

        _waveOut.Play();
        StartPositionTimer();
        LoggingService.Debug("AudioPlaybackService: Started playback");
    }

    /// <summary>
    /// Pauses playback. Can be resumed with Play().
    /// </summary>
    public void Pause()
    {
        if (_waveOut == null) return;

        if (_waveOut.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Pause();
            StopPositionTimer();
            LoggingService.Debug("AudioPlaybackService: Paused playback");
        }
    }

    /// <summary>
    /// Stops playback and resets position to the beginning.
    /// </summary>
    public void Stop()
    {
        if (_waveOut == null) return;

        _waveOut.Stop();
        StopPositionTimer();

        // Reset position to beginning
        if (_audioFile != null)
        {
            _audioFile.Position = 0;
        }

        LoggingService.Debug("AudioPlaybackService: Stopped playback");
    }

    /// <summary>
    /// Toggles between play and pause states.
    /// </summary>
    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>
    /// Seeks to a specific position in the audio.
    /// </summary>
    /// <param name="position">Position to seek to</param>
    public void Seek(TimeSpan position)
    {
        if (_audioFile == null) return;

        // Clamp to valid range
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        else if (position > TotalDuration)
        {
            position = TotalDuration;
        }

        _audioFile.CurrentTime = position;
        PositionChanged?.Invoke(position);
    }

    // =========================================================================
    // PRIVATE METHODS
    // =========================================================================

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        StopPositionTimer();

        // Check if we reached the end of the file
        if (_audioFile != null && _audioFile.Position >= _audioFile.Length)
        {
            // Reset to beginning
            _audioFile.Position = 0;
            PositionChanged?.Invoke(TimeSpan.Zero);
            PlaybackEnded?.Invoke();
            LoggingService.Debug("AudioPlaybackService: Playback ended (reached end of file)");
        }

        if (e.Exception != null)
        {
            LoggingService.Error($"AudioPlaybackService: Playback error: {e.Exception.Message}");
            PlaybackFailed?.Invoke(e.Exception);
        }
    }

    private void StartPositionTimer()
    {
        StopPositionTimer();

        _positionTimer = new System.Timers.Timer(100); // Update every 100ms
        _positionTimer.Elapsed += (s, e) =>
        {
            if (_audioFile != null)
            {
                PositionChanged?.Invoke(_audioFile.CurrentTime);
            }
        };
        _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Stop();
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    private void Cleanup()
    {
        if (_waveOut != null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Dispose();
            _waveOut = null;
        }

        _audioFile?.Dispose();
        _audioFile = null;

        LoadedFilePath = null;
    }

    // =========================================================================
    // IDISPOSABLE
    // =========================================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopPositionTimer();
        Stop();
        Cleanup();

        GC.SuppressFinalize(this);
    }
}
