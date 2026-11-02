// ONBOARDING LIVE DEPENDENCIES
//
// The composition root that binds the first-run flow model's seven seams
// (ViewModels/Onboarding/OnboardingSeams.cs) to the app's real services. The
// Windows port of
// app/macos/hyperwhisper/Views/Onboarding/OnboardingLiveDependencies.swift.
//
// Everything here is thin plumbing on purpose: the policy lives in
// OnboardingFlowViewModel so it can be exercised without a database, the
// credential store, a microphone, or the network.
//
// THIS FILE AND ITS ADAPTER SIBLINGS ARE THE ONLY PLACES THAT KNOW ABOUT SINGLETONS.
// The Windows head has no DI container - services are hand-rolled lazy singletons
// - so the rule that keeps the flow testable is that Instance is resolved in
// these files and nowhere else. The flow view model takes its seven seams as
// constructor arguments and never reaches for a global.
//
// macOS's Combine publishers become plain events: System.Reactive is not a
// dependency of this head.
//
// The adapters live one per file beside this one: LiveOnboardingPermissions,
// LiveOnboardingModelCatalog, LiveOnboardingLicenseGateway,
// LiveOnboardingCreditsGateway, LiveOnboardingProviderKeyGateway and
// LiveOnboardingSourceCommitter (split out of this file, #941). The audio gateway
// lives in OnboardingLiveAudioGateway.cs - it is the largest of them, because
// Windows has neither an idle level meter nor a sample-clip path to reuse.

using System.IO;
using System.Reflection;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

// =============================================================================
// Assembly
// =============================================================================

/// <summary>
/// Builds a flow view model wired to the app's real services. The single
/// composition point; nothing else in the onboarding code resolves a singleton.
/// </summary>
public static class OnboardingLiveDependencies
{
    /// <summary>
    /// The seven live adapters, plus the disposables the caller has to release
    /// when the window closes. The flow view model's own Cleanup() detaches from
    /// the seams; these three own OS resources (a COM notification client, a
    /// capture stream, and two event subscriptions on process-lifetime singletons)
    /// and outlive it if nobody disposes them.
    /// </summary>
    public sealed record LiveOnboarding(OnboardingFlowViewModel Flow, IReadOnlyList<IDisposable> Resources)
    {
        public void DisposeResources()
        {
            foreach (var resource in Resources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"OnboardingLiveDependencies: dispose failed: {ex.Message}");
                }
            }
        }
    }

    public static LiveOnboarding CreateLive(
        Action<KeyboardShortcut>? persistToggleShortcut = null,
        Action? returnToHome = null)
    {
        var mainViewModel = WpfApplication.Current?.MainWindow?.DataContext as MainViewModel;

        var permissions = new LiveOnboardingPermissions(
            persistToggleShortcut ?? (shortcut => SettingsService.Instance.ToggleShortcut = shortcut));

        // One pair for the whole window: the Setup step's catalog reads them to
        // decide what is installed, and the Try It step's gateway reads them to
        // LOAD what the Setup step just downloaded. Both are stateless wrappers
        // over the model directories, so sharing them costs nothing and keeps
        // the two steps answering from the same place.
        var whisperModels = new WhisperModelService();
        var parakeetModels = new ParakeetModelService();

        var catalog = new LiveOnboardingModelCatalog(
            whisperModels,
            parakeetModels,
            ModelDownloadService.Instance);

        var license = new LiveOnboardingLicenseGateway(LicenseManager.Instance);
        var credits = new LiveOnboardingCreditsGateway(HyperWhisperCloudManager.Instance);
        var providerKeys = new LiveOnboardingProviderKeyGateway(
            ApiKeyService.Instance,
            CloudProviderHealthService.Instance);

        // Onboarding owns its OWN device service and recorder rather than sharing
        // MainViewModel's. Sharing the recorder would put the main window's
        // IsRecording, its overlay and its history writes on the same object as
        // the Try It button.
        var deviceService = new AudioDeviceService();
        var recorder = new AudioRecorderService();

        var audio = new LiveOnboardingAudioGateway(
            deviceService,
            recorder,
            SettingsService.Instance,
            ModeService.Instance,
            MicrophoneKeepWarmService.Instance,
            VocabularyService.Instance,
            whisperModels,
            parakeetModels,
            // The running app's open device lives on MainViewModel, not in
            // settings; see the comment on those two delegates.
            readOpenDevice: mainViewModel is null ? null : () => mainViewModel.ConnectedSavedMicrophoneName,
            writeOpenDevice: mainViewModel is null ? null : name => ApplyOpenDevice(mainViewModel, name));

        var committer = new LiveOnboardingSourceCommitter(
            ModeService.Instance,
            SettingsService.Instance,
            returnToHome ?? (() => NavigateMainShell(MainViewModel.NavigationPage.Home)));

        var flow = new OnboardingFlowViewModel(
            permissions,
            catalog,
            license,
            credits,
            providerKeys,
            audio,
            committer,
            Loc.S("onboarding.mic.device.systemDefault"));

        // The Done step's "Text delivery" row is the one thing the flow renders that
        // is neither a seam read nor its own state. macOS branches it on the
        // Accessibility grant; Windows has none, so it branches on the setting that
        // actually decides where the text lands. It arrives as a delegate for the
        // same reason everything else does: this file is the only one that may
        // resolve a singleton.
        flow.ReadAutoPasteEnabled = () => SettingsService.Instance.AutoPasteEnabled;

        return new LiveOnboarding(flow, new IDisposable[] { audio, recorder, deviceService, credits, catalog });
    }

    /// <summary>
    /// Points the running app at the microphone onboarding just chose or
    /// restored. The gateway has already written
    /// SettingsService.LastSelectedMicrophone (the one saved choice), so
    /// MainViewModel simply re-resolves it: the named device when connected,
    /// otherwise the Windows default, with the choice itself kept. The name is
    /// only logged.
    /// </summary>
    private static void ApplyOpenDevice(MainViewModel viewModel, string? name)
    {
        LoggingService.Debug($"LiveOnboarding: applying the saved microphone choice ('{name ?? "(default)"}')");
        viewModel.ApplyOnboardingMicrophoneChoice();
    }

    /// <summary>
    /// Best effort navigation of the main shell behind the onboarding window.
    /// Silently does nothing when there is no main window, which is the smoke
    /// harness's state.
    /// </summary>
    private static void NavigateMainShell(MainViewModel.NavigationPage page)
    {
        if (WpfApplication.Current?.MainWindow?.DataContext is MainViewModel viewModel)
        {
            viewModel.CurrentPage = page;
        }
    }

    /// <summary>
    /// The bundled sample clip, extracted from the assembly to a real file.
    ///
    /// It is an EmbeddedResource, so there is no path on disk to hand to
    /// FileTranscriptionService; the stream is written out the same way
    /// SoundEffectsService reads the start/stop WAVs. The caller deletes it.
    /// Returns null when the resource is missing, which is what
    /// HasSampleClip reports.
    /// </summary>
    internal const string SampleClipResourceName = "HyperWhisper.Assets.Sounds.onboarding-sample.wav";

    internal static bool SampleClipExists()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SampleClipResourceName);
        return stream is not null;
    }

    internal static string? ExtractSampleClip()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SampleClipResourceName);
            if (stream is null)
            {
                LoggingService.Warn($"OnboardingLiveDependencies: '{SampleClipResourceName}' is not in this build");
                return null;
            }

            var directory = AppPaths.Combine("Temp");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"onboarding-sample-{Guid.NewGuid():N}.wav");

            using (var file = File.Create(path))
            {
                stream.CopyTo(file);
            }

            return path;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"OnboardingLiveDependencies: could not extract the sample clip: {ex.Message}", ex);
            return null;
        }
    }
}
