using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Converters;

/// <summary>
/// MODE OFFLINE VISIBILITY CONVERTER
///
/// Converts a Mode to Visibility for the "Offline" badge on the mode card:
/// Visible when the mode needs no network at all, Collapsed otherwise.
///
/// This exists because the badge's old <c>DataTrigger</c> bound
/// <c>IsOfflineCapable</c>, a property of the macOS <c>ModeData</c> struct that
/// no C# type has ever had (issue #527). WPF resolves a missing path to
/// <c>DependencyProperty.UnsetValue</c> and writes a line to a trace source
/// nobody is listening to, so the trigger looked live in the markup and had
/// never once fired.
///
/// A converter, rather than a property on <see cref="Mode"/>: <c>Mode</c> is an
/// EF entity that maps a database table, and the sibling fact on this same card
/// — whether the post-processing row shows at all — is already a converter
/// (<see cref="PostProcessingVisibilityConverter"/>). This is the same kind of
/// fact, derived from the same row, so it is the same kind of object.
/// </summary>
public class ModeOfflineVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Mode mode && IsOfflineCapable(mode)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Whether every step this mode runs happens on this machine.
    /// </summary>
    /// <remarks>
    /// The two halves are the macOS <c>ModeData.isOfflineCapable</c>'s two
    /// halves — transcription is local, and post-processing is off or local —
    /// but each is answered from the catalogue of models that actually run
    /// on-device rather than from a flag, for two reasons.
    ///
    /// The first is that a Windows row's <c>Model</c> field cannot be trusted
    /// for this. macOS reads <c>model == "cloud"</c>; on Windows the mode editor
    /// seeds <c>Model = "cloud"</c> on a NEW mode and never clears it when the
    /// user switches that mode to On-device, so the macOS test would call a
    /// perfectly local mode a cloud one. <c>ProviderType</c> is the field the
    /// editor actually writes and the field the rest of the app branches on.
    ///
    /// The second is that "local" is a claim and the catalogue is the fact. A row
    /// can name a Whisper type or a Parakeet id that no longer ships — a mode
    /// restored from an old backup, or written through the Local API — and a
    /// badge promising it runs offline would then be wrong. The same catalogues
    /// are what <c>MainViewModel.IsLocalModelDownloaded</c> resolves against.
    ///
    /// Deliberately NOT part of this: whether the model has been downloaded yet.
    /// The badge answers "can this mode work with the network off", which is a
    /// property of the mode's configuration; whether the weights are on disk is a
    /// property of the machine, it changes under a list that is not re-bound, and
    /// the app already says so elsewhere. macOS draws the same line.
    /// </remarks>
    public static bool IsOfflineCapable(Mode mode)
    {
        return TranscriptionRunsOnDevice(mode) && PostProcessingRunsOnDevice(mode);
    }

    private static bool TranscriptionRunsOnDevice(Mode mode)
    {
        if (mode.ProviderType == "cloud")
            return false;

        return mode.LocalEngine == "parakeet"
            ? ParakeetModelInfo.AllModels.Any(m => m.Id == mode.LocalParakeetModel)
            : WhisperModelInfo.AllModels.Any(m => m.Type == mode.ModelType);
    }

    private static bool PostProcessingRunsOnDevice(Mode mode)
    {
        // 0 = off. Nothing runs, so nothing reaches the network.
        if (mode.PostProcessingMode == 0)
            return true;

        // Anything that is not the on-device LLM is a network call, including a
        // custom endpoint: its URL may well be localhost, but the row does not
        // say so and a badge is not the place to guess.
        if (PostProcessingProviderExtensions.FromString(mode.PostProcessingProvider)
            != PostProcessingProvider.LocalLlm)
        {
            return false;
        }

        // The editor writes the on-device LLM to LocalPostProcessingModel;
        // LanguageModel is the BYOK field and the pre-split fallback.
        var modelId = string.IsNullOrEmpty(mode.LocalPostProcessingModel)
            ? mode.LanguageModel
            : mode.LocalPostProcessingModel;

        return LocalLlmModelInfo.GetById(modelId) is not null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}
