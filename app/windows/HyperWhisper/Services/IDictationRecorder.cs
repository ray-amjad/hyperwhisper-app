using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// The microphone end of a dictation, as MainViewModel's start, stop and cancel
/// path uses it. <see cref="AudioRecorderService"/> is the app's one; the smoke
/// suite drives that path with a fake through
/// <c>MainViewModel.DictationRecorderOverride</c> (#2241).
/// </summary>
internal interface IDictationRecorder
{
    /// <summary>How long the current recording has run.</summary>
    TimeSpan Duration { get; }

    /// <summary>PCM bytes the last recording captured; final once <see cref="StopRecording"/> returned.</summary>
    long CapturedAudioBytes { get; }

    void StartRecording(int deviceNumber);

    /// <summary>Stops the recording and returns the path of its WAV file.</summary>
    Result<string> StopRecording();

    void BoostMicVolume(int deviceNumber);

    void RestoreMicVolume();
}
