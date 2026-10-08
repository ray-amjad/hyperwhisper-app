using HyperWhisper.Services;

namespace HyperWhisper.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // RECORDING COMPRESSION (WAV -> M4A), OFF THE UI THREAD (#1499)
    // =========================================================================

    /// <summary>
    /// Starts the WAV-to-M4A compression of a finished recording on a thread-pool thread.
    /// <see cref="StorageService.TryConvertWavToM4A"/> is synchronous and takes 13 s for a
    /// 61-minute file and 42 s for a 3-hour one, so running it on the dispatcher froze the
    /// window, the tray and the hotkeys (#1499). The task never faults: a failed
    /// compression returns null and keeps the WAV, as before.
    /// </summary>
    internal static Task<string?> StartRecordingCompression(Func<string, string?> convert, string wavPath)
    {
        ArgumentNullException.ThrowIfNull(convert);

        return Task.Run(() =>
        {
            try
            {
                return convert(wavPath);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"Background audio compression failed: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>
    /// Waits for a compression nobody else will record, then points the history row at
    /// the M4A, or deletes the M4A when the row is gone (the user deleted it, or cancelled
    /// the file job, while it ran). Runs entirely off the UI thread and touches no WPF
    /// object: <paramref name="pointRowAt"/> is <see cref="HistoryService.UpdateAudioFilePath"/>,
    /// whose event the History page applies on the dispatcher. Never throws.
    /// </summary>
    internal static async Task FinishRecordingCompressionAsync(
        Task<string?> compression,
        Guid transcriptId,
        Func<Guid, string, bool> pointRowAt,
        Action<string> deleteAudio)
    {
        try
        {
            var compressedPath = await compression.ConfigureAwait(false);
            if (string.IsNullOrEmpty(compressedPath))
            {
                return;
            }

            if (!pointRowAt(transcriptId, compressedPath))
            {
                LoggingService.Info($"MainViewModel: Transcript {transcriptId} was removed while its audio compressed; deleting the M4A");
                deleteAudio(compressedPath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Background audio compression failed: {ex.Message}");
        }
    }

    /// <summary>
    /// For a file job cancelled while its compression ran and whose row is already gone:
    /// once the encode ends, deletes the M4A it wrote (if any) and the WAV (if the encode
    /// failed and so kept it). The row's own delete could do neither: the encoder held the
    /// WAV open and the row never named the M4A. Never throws.
    /// </summary>
    internal static async Task DeleteRecordingAfterCompressionAsync(
        Task<string?> compression,
        string wavPath,
        Action<string> deleteAudio)
    {
        try
        {
            var compressedPath = await compression.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(compressedPath))
            {
                deleteAudio(compressedPath);
            }

            deleteAudio(wavPath);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MainViewModel: Cleanup after a cancelled compression failed: {ex.Message}");
        }
    }
}
