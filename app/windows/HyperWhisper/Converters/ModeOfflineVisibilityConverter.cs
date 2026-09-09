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
    /// THE SOURCE OF TRUTH IS THE BRANCH THE APP ITSELF TAKES. Each half below
    /// mirrors, decision for decision, the one place that chooses between a local
    /// call and a network call —
    /// <see cref="Services.Transcription.TranscriptionOrchestrator"/> for the
    /// transcription and <see cref="Services.PostProcessingService"/> for the
    /// post-processing. That is the only definition that cannot drift into a lie:
    /// a badge derived from anything else is a second opinion about what the app
    /// will do.
    ///
    /// It is deliberately NOT the macOS <c>ModeData.isOfflineCapable</c> ported
    /// across. macOS reads <c>model == "cloud"</c>; on Windows the mode editor
    /// seeds <c>Model = "cloud"</c> on a NEW mode and never clears it when the
    /// user switches that mode to On-device (<c>ModeEditorWindow.xaml.cs:61</c>,
    /// <c>:2144</c>), so the macOS test would call a perfectly local mode a cloud
    /// one. The two halves of the QUESTION are the same on both platforms;
    /// the fields that answer it are not.
    ///
    /// Nor is it "the named model is in the on-device catalogue". A row can name
    /// a Whisper type or a local LLM the app no longer ships — an old backup, or
    /// a POST to the Local API — and that row is broken, but it is not ONLINE:
    /// neither service has a cloud fallback, so it fails on this machine without
    /// a packet leaving it. Requiring the catalogue would hide the badge on modes
    /// that genuinely never reach the network, which is what a first cut of this
    /// converter did.
    ///
    /// Also not part of it: whether the model has been downloaded yet. The badge
    /// answers "can this mode work with the network off", a property of the
    /// mode's configuration; whether the weights are on disk is a property of the
    /// machine, it changes under a list that is not re-bound, and the app already
    /// says so elsewhere. macOS draws the same line.
    /// </remarks>
    public static bool IsOfflineCapable(Mode mode)
    {
        return TranscriptionRunsOnDevice(mode) && PostProcessingRunsOnDevice(mode);
    }

    /// <summary>
    /// Mirrors <c>TranscriptionOrchestrator.TranscribeAsync</c>, which is a
    /// straight binary on this one field: <c>"cloud"</c> goes to the cloud path,
    /// everything else goes to the local engine. There is no fallback either way.
    /// </summary>
    private static bool TranscriptionRunsOnDevice(Mode mode)
        => mode.ProviderType != "cloud";

    /// <summary>
    /// Mirrors <c>PostProcessingService.ProcessAsync</c>'s provider routing, in
    /// its order: disabled skips, a custom endpoint is a URL, an unconfigured
    /// provider skips, and only the local LLM stays on this machine.
    /// </summary>
    private static bool PostProcessingRunsOnDevice(Mode mode)
    {
        // 0 = off. The service returns Skipped before it looks at anything else.
        if (mode.PostProcessingMode == 0)
            return true;

        // A custom endpoint is a URL the user typed. It may well be localhost,
        // but the row does not say so and a badge is not the place to guess.
        if (CustomPostProcessingEndpoint.IsCustomProviderString(mode.PostProcessingProvider))
            return false;

        var provider = PostProcessingProviderExtensions.FromString(mode.PostProcessingProvider ?? "");

        // "No provider configured" — the service logs exactly that and returns
        // Skipped, so nothing runs and nothing is sent.
        if (provider == PostProcessingProvider.None)
            return true;

        // Every other provider is an HTTP call. The local LLM is not: the model
        // id is resolved with a fallback to the default local model, so an
        // unknown or unset id still runs on this machine.
        return provider == PostProcessingProvider.LocalLlm;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}
