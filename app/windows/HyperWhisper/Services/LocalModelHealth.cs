using System.Collections.Concurrent;
using System.IO;

namespace HyperWhisper.Services;

/// <summary>
/// Process-wide list of local model files that failed to load (#1598).
///
/// A damaged model can pass the install check (a disk fault that keeps the exact
/// size, or a Qwen3 export whose file sizes are not pinned). Before this, such a
/// model stayed "Ready" and "Installed" while every transcription failed, and
/// Model Library refused to delete it because a mode used it, so there was no
/// way to re-download it from inside the app.
///
/// When a load fails because of the model itself (a Whisper
/// <c>WhisperModelLoadException</c>, or the Parakeet engine daemon answering
/// "Failed to load model" or crashing with 0xC0000409 while it loads the model
/// before READY), the loader marks the model's path here. The model services then count
/// it as not installed, so the status bar stops saying Ready and Model Library
/// offers Download again. A successful download or a delete clears the mark.
///
/// Keyed by the model's file (Whisper) or directory (Parakeet family) path. The
/// mark is in memory only: a restart tries the file again, and the install
/// check or the next failed load decides again.
/// </summary>
public static class LocalModelHealth
{
    private static readonly ConcurrentDictionary<string, string> BrokenModels =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised with the model path whenever a model is marked broken or the mark
    /// is cleared. May fire on any thread; subscribers dispatch to the UI.
    /// </summary>
    public static event EventHandler<string>? Changed;

    /// <summary>True when the model at <paramref name="modelPath"/> failed to load in this session.</summary>
    public static bool IsBroken(string? modelPath)
    {
        var key = Normalize(modelPath);
        return key != null && BrokenModels.ContainsKey(key);
    }

    /// <summary>Marks the model at <paramref name="modelPath"/> broken until it is re-downloaded or deleted.</summary>
    public static void MarkBroken(string? modelPath, string reason)
    {
        var key = Normalize(modelPath);
        if (key == null) return;

        if (BrokenModels.TryAdd(key, reason))
        {
            // The leaf only: the full path carries the Windows account name.
            LoggingService.Warn($"LocalModelHealth: Marked model '{Path.GetFileName(key)}' broken ({reason}); it now counts as not installed");
            RaiseChanged(key);
        }
    }

    /// <summary>Clears the broken mark for <paramref name="modelPath"/>, if any.</summary>
    public static void ClearBroken(string? modelPath)
    {
        var key = Normalize(modelPath);
        if (key == null) return;

        if (BrokenModels.TryRemove(key, out _))
        {
            LoggingService.Info($"LocalModelHealth: Cleared broken mark for model '{Path.GetFileName(key)}'");
            RaiseChanged(key);
        }
    }

    private static string? Normalize(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;

        try
        {
            return Path.GetFullPath(modelPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void RaiseChanged(string modelPath)
    {
        var handler = Changed;
        if (handler == null) return;

        foreach (EventHandler<string> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(null, modelPath);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"LocalModelHealth: Changed subscriber failed: {ex.Message}");
            }
        }
    }
}
