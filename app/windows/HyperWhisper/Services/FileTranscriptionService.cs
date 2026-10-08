using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// FILE TRANSCRIPTION SERVICE
///
/// Handles validation and conversion of audio files for transcription.
/// Converts any supported audio format to 16kHz mono WAV (Whisper requirement).
///
/// SUPPORTED FORMATS (Phase 1 MVP):
/// - WAV - Windows PCM format
/// - MP3 - Compressed audio (NAudio via MediaFoundation)
/// - M4A - Apple audio format (NAudio via MediaFoundation)
///
/// NAudio PIPELINE:
/// AudioFileDecoder → MonoFoldSampleProvider → WdlResamplingSampleProvider → WaveFileWriter (16-bit PCM)
///
/// FUTURE PHASES:
/// - FLAC, OGG support (requires additional NAudio packages)
/// - Video extraction via FFmpeg (MP4, MOV)
/// </summary>
public static class FileTranscriptionService
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Whisper requires 16kHz sample rate for optimal performance.
    /// </summary>
    private const int WhisperSampleRate = 16000;

    /// <summary>
    /// Supported file extensions for Phase 1 MVP.
    /// These formats are natively supported by NAudio via MediaFoundation on Windows.
    /// </summary>
    public static readonly string[] SupportedExtensions = { ".wav", ".mp3", ".m4a" };

    /// <summary>
    /// File filter string for OpenFileDialog.
    /// </summary>
    public static string FileFilter =>
        "Audio Files (*.wav;*.mp3;*.m4a)|*.wav;*.mp3;*.m4a|All Files (*.*)|*.*";

    // =========================================================================
    // PUBLIC METHODS
    // =========================================================================

    /// <summary>
    /// Converts an audio file to 16kHz mono 16-bit PCM WAV format required by local providers.
    ///
    /// BEHAVIOR:
    /// - If file is already 16kHz mono 16-bit PCM WAV: returns original path (no conversion)
    /// - Otherwise: converts to temp file and returns new path
    ///
    /// CONVERSION PIPELINE:
    /// 1. AudioFileDecoder - Reads any format (WAV, MP3, M4A); an Extensible PCM/float WAV without ACM
    /// 2. AudioFileDecoder.ToMono - Averages any channel count down to mono
    /// 3. WdlResamplingSampleProvider - Resamples to 16kHz if needed
    /// 4. WaveFileWriter - Writes 16kHz mono 16-bit PCM WAV to temp file
    ///
    /// RESULT PATTERN:
    /// Returns Result&lt;string&gt; with:
    /// - Success: Path to 16kHz mono WAV (original or converted)
    /// - Failure: Error message for unsupported format or conversion failure
    /// </summary>
    /// <param name="inputPath">Path to the audio file to convert</param>
    /// <returns>Result with path to 16kHz mono WAV file</returns>
    public static async Task<Result<string>> ConvertToWhisperFormatAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // GUARD CLAUSE: Validate file exists
        if (!File.Exists(inputPath))
        {
            LoggingService.Error($"FileTranscriptionService: File not found - {inputPath}");
            return Result<string>.Failure($"File not found: {inputPath}");
        }

        // GUARD CLAUSE: Validate file extension
        var extension = Path.GetExtension(inputPath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            LoggingService.Warn($"FileTranscriptionService: Unsupported format - {extension}");
            return Result<string>.Failure($"Unsupported audio format: {extension}. Please use WAV, MP3, or M4A.");
        }

        try
        {
            // Run conversion on background thread to avoid blocking UI
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsAlready16KhzMonoPcmWav(inputPath))
                {
                    LoggingService.Debug($"FileTranscriptionService: File already in correct format - {inputPath}");
                    return Result<string>.Success(inputPath);
                }

                // AudioFileDecoder, not AudioFileReader: a WAVE_FORMAT_EXTENSIBLE PCM or
                // float WAV (every ffmpeg WAV with more than two channels) would go to ACM
                // and fail with "NoDriver calling acmFormatSuggest" (#1450).
                using var reader = AudioFileDecoder.Open(inputPath);
                cancellationToken.ThrowIfCancellationRequested();

                // CONVERSION NEEDED: Create temp file for converted audio
                var tempPath = Path.Combine(Path.GetTempPath(), $"hyperwhisper_converted_{Guid.NewGuid()}.wav");
                LoggingService.Info($"FileTranscriptionService: Converting {Path.GetFileName(inputPath)} " +
                                   $"(from {reader.WaveFormat.SampleRate}Hz {reader.WaveFormat.Channels}ch " +
                                   $"to {WhisperSampleRate}Hz mono)");

                try
                {
                    // STEP 1: Fold to mono by averaging every channel. NAudio's ToMono()
                    // handles exactly two channels and throws on a 5.1 file.
                    ISampleProvider sampleProvider = AudioFileDecoder.ToMono(reader.Samples);

                    // STEP 2: Resample to 16kHz if needed
                    if (reader.WaveFormat.SampleRate != WhisperSampleRate)
                    {
                        sampleProvider = new WdlResamplingSampleProvider(sampleProvider, WhisperSampleRate);
                    }

                    // STEP 3: Write to 16kHz mono 16-bit PCM WAV file
                    WriteWaveFile16Cancellable(tempPath, sampleProvider, cancellationToken);

                    LoggingService.Info($"FileTranscriptionService: Conversion complete - {tempPath}");
                    return Result<string>.Success(tempPath);
                }
                catch
                {
                    // Clean up temp file on conversion failure
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch { /* Best effort cleanup */ }

                    throw; // Re-throw to be caught by outer try-catch
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LoggingService.Info("FileTranscriptionService: Conversion cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"FileTranscriptionService: Conversion failed - {ex.Message}", ex);
            return Result<string>.Failure($"Failed to convert audio file: {ex.Message}", ex);
        }
    }

    private static void WriteWaveFile16Cancellable(
        string outputPath,
        ISampleProvider sampleProvider,
        CancellationToken cancellationToken)
    {
        var waveProvider = new SampleToWaveProvider16(sampleProvider);
        using var writer = new WaveFileWriter(outputPath, waveProvider.WaveFormat);

        var bufferSize = Math.Max(waveProvider.WaveFormat.AverageBytesPerSecond / 10, waveProvider.WaveFormat.BlockAlign);
        bufferSize -= bufferSize % waveProvider.WaveFormat.BlockAlign;
        var buffer = new byte[bufferSize];

        int bytesRead;
        while ((bytesRead = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(buffer, 0, bytesRead);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsAlready16KhzMonoPcmWav(string inputPath)
    {
        if (!string.Equals(Path.GetExtension(inputPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var reader = new WaveFileReader(inputPath);
            return reader.WaveFormat.SampleRate == WhisperSampleRate &&
                   reader.WaveFormat.Channels == 1 &&
                   reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm &&
                   reader.WaveFormat.BitsPerSample == 16;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the duration of an audio file in seconds.
    /// Uses AudioFileDecoder (NAudio's AudioFileReader, plus Extensible PCM/float WAVs).
    ///
    /// RESULT PATTERN:
    /// Returns Result&lt;double&gt; with:
    /// - Success: Duration in seconds
    /// - Failure: Error message if file can't be read
    /// </summary>
    /// <param name="filePath">Path to the audio file</param>
    /// <returns>Result with duration in seconds</returns>
    public static Result<double> GetAudioDuration(string filePath)
    {
        // GUARD CLAUSE: Validate file exists
        if (!File.Exists(filePath))
        {
            LoggingService.Error($"FileTranscriptionService: Cannot get duration - file not found: {filePath}");
            return Result<double>.Failure($"File not found: {filePath}");
        }

        try
        {
            using var reader = AudioFileDecoder.Open(filePath);
            double duration = reader.TotalTime.TotalSeconds;
            LoggingService.Debug($"FileTranscriptionService: Audio duration = {duration:F2}s");
            return Result<double>.Success(duration);
        }
        catch (Exception ex)
        {
            LoggingService.Error($"FileTranscriptionService: Failed to read duration - {ex.Message}", ex);
            return Result<double>.Failure($"Failed to read audio duration: {ex.Message}", ex);
        }
    }

}
