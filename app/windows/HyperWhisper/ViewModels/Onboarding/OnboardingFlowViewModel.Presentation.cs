// ONBOARDING FLOW MODEL, PRESENTATION PROJECTIONS
//
// Everything in this file is a pure, one-line derivation of state that already
// exists on the flow model. Nothing here decides anything: no gate, no write, no
// task, no seam call that is not a read.
//
// It exists because the macOS views compute exactly this much for themselves.
// OnboardingView.swift builds `reassurance`, `primaryTitle`, `transcriptHeading`,
// `transcriptMeta` and `sourceSummary` inline (lines 389-525), and
// OnboardingSourceViews.swift branches its scaffold and its cards on
// `flow.selectedSource` in four places. SwiftUI can express a switch in a view
// body; XAML cannot, and the alternative is a pile of converters that each hide a
// little policy. The Windows rule from the port is that the views hold none, so
// the branch lands here where it can be read and tested.
//
// Change notification: the derived members fan out from
// OnPropertyChanged(PropertyChangedEventArgs) at the bottom of the file rather
// than from each setter, so the flow model itself (OnboardingFlowViewModel.cs)
// needed no edit to carry them.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.ViewModels.Onboarding;

/// <summary>
/// The announcement for one row of a single-selection list. A styled Button
/// announces only its content, so without this a screen reader reads four
/// provider names and never says which one is chosen. macOS gets the same
/// sentence from .accessibilityAddTraits(.isSelected).
/// </summary>
internal static class OnboardingRowAccessibility
{
    public static string Describe(string label, bool isSelected) =>
        Loc.S(isSelected ? "onboarding.a11y.selectedDetail" : "onboarding.a11y.notSelectedDetail", label);
}

/// <summary>
/// One BYOK provider as the Configure step's chip strip renders it. Carries its
/// own selected flag so the chips need no per-item MultiBinding.
/// </summary>
public sealed record OnboardingProviderRow(
    CloudTranscriptionProvider Provider,
    string DisplayName,
    string AssetPath,
    bool IsSelected)
{
    public string AccessibleName => OnboardingRowAccessibility.Describe(DisplayName, IsSelected);

    /// <summary>
    /// True when this vendor has a real PNG. False collapses the image and puts the
    /// monogram in its place, exactly as the Model Library does - a blank 14x14 gap
    /// is what "bind the path anyway" looks like on screen.
    ///
    /// Bools, not System.Windows.Visibility: this file is presentation state, not
    /// WPF, and the page already has OnboardingBoolToVisibility for the conversion.
    /// </summary>
    public bool HasAsset => AssetPath.Length > 0;

    public bool ShowsMonogram => !HasAsset;

    /// <summary>The first letter of the display name, for the no-logo fallback.</summary>
    public string Monogram => DisplayName.Length > 0
        ? DisplayName.Substring(0, 1).ToUpperInvariant()
        : "?";
}

/// <summary>One curated model as the Configure step's list renders it.</summary>
public sealed record OnboardingModelRow(
    OnboardingModelSelection Model,
    string DisplayName,
    string Subtitle,
    string SizeText,
    bool IsSelected,
    bool IsInstalled)
{
    /// <summary>The size is shown only while the model is not yet on disk.</summary>
    public bool ShowsSize => !IsInstalled;

    public string AccessibleName => OnboardingRowAccessibility.Describe(DisplayName, IsSelected);
}

/// <summary>One input device as the Microphone step's list renders it.</summary>
public sealed record OnboardingDeviceRow(
    string Id,
    string Name,
    string Detail,
    bool IsSelected)
{
    /// <summary>
    /// The input list says "Selected input" rather than the generic "Selected":
    /// the Microphone step is the one place where the thing being chosen and the
    /// thing being metered are different, and the wording keeps them apart.
    /// </summary>
    public string AccessibleName => IsSelected
        ? $"{Loc.S("onboarding.a11y.selectedInput")}, {Name}"
        : Loc.S("onboarding.a11y.notSelectedDetail", Name);
}

/// <summary>
/// One of the three doors on the Source step. macOS keeps this table as
/// <c>OnboardingSourceSpec.all</c> on the view (OnboardingSourceViews.swift:109-128);
/// on Windows it lives here so the three cards, the four Configure branches and
/// the four Setup branches all read one selection flag.
/// </summary>
public sealed record OnboardingSourceRow(
    OnboardingSourceKind Kind,
    string Glyph,
    string Title,
    string Description,
    bool IsSelected)
{
    public string AccessibleName => OnboardingRowAccessibility.Describe(Title, IsSelected);
}

public sealed partial class OnboardingFlowViewModel
{
    // =========================================================================
    // COMPOSITION HOOKS
    //
    // Set once by OnboardingLiveDependencies.CreateLive. The default is the honest
    // answer for a flow driven by fakes, so the smoke suite needs no change.
    // =========================================================================

    /// <summary>
    /// Whether finished text is pasted at the cursor rather than left on the
    /// clipboard, i.e. SettingsService.AutoPasteEnabled. It reaches the Done step's
    /// "Text delivery" row through a delegate for the same reason every other piece
    /// of app state does: OnboardingLiveDependencies is the only file allowed to
    /// resolve a singleton.
    /// </summary>
    internal Func<bool> ReadAutoPasteEnabled { get; set; } = static () => false;

    // =========================================================================
    // SOURCE BRANCH FLAGS
    //
    // Three bools instead of an enum converter. The Configure and Setup steps each
    // fork four ways, and XAML reads a bool far more legibly than an equality
    // converter with a string parameter.
    // =========================================================================

    public bool IsCloudSelected => SelectedSource == OnboardingSourceKind.HyperWhisperCloud;

    public bool IsOnDeviceSelected => SelectedSource == OnboardingSourceKind.OnDevice;

    public bool IsProviderSelected => SelectedSource == OnboardingSourceKind.YourProvider;

    public bool HasNoSourceSelected => SelectedSource is null;

    /// <summary>
    /// The three doors, in macOS's order: HyperWhisper Cloud first, because it is
    /// the fastest path to a working first recording.
    /// </summary>
    public IReadOnlyList<OnboardingSourceRow> SourceOptions
    {
        get
        {
            var rows = new List<OnboardingSourceRow>(3)
            {
                new(
                    OnboardingSourceKind.HyperWhisperCloud,
                    "\uE753",
                    Loc.S("onboarding.source.cloud.title"),
                    Loc.S("onboarding.source.cloud.description"),
                    IsCloudSelected)
            };

            // The on-device branch is offered only where a local engine actually
            // runs. The catalog is already filtered by PlatformHelper, so an empty
            // one means this build ships no engine for this machine (ARM64 Windows
            // 10, or ARM64 with no native sherpa-onnx daemon). Offering the card
            // anyway led to a Configure step with an empty model list, a gate that
            // could never open, and no error surface on that step to say why -
            // which is a dead end, not a choice.
            if (IsOnDeviceAvailable)
            {
                rows.Add(new OnboardingSourceRow(
                    OnboardingSourceKind.OnDevice,
                    "\uE977",
                    Loc.S("onboarding.source.onDevice.title"),
                    Loc.S("onboarding.source.onDevice.description"),
                    IsOnDeviceSelected));
            }

            rows.Add(new OnboardingSourceRow(
                OnboardingSourceKind.YourProvider,
                "\uE72E",
                Loc.S("onboarding.source.provider.title"),
                Loc.S("onboarding.source.provider.description"),
                IsProviderSelected));

            return rows;
        }
    }

    /// <summary>
    /// Whether this machine has any on-device model to offer at all.
    /// </summary>
    public bool IsOnDeviceAvailable => _catalog.Models.Count > 0;

    // =========================================================================
    // FOOTER
    // =========================================================================

    /// <summary>
    /// The single primary. Mirrors OnboardingView.swift:480-486: the first and last
    /// steps name their own action, everything between says Continue.
    /// </summary>
    public string PrimaryButtonText => Step switch
    {
        OnboardingStep.Welcome => Loc.S("onboarding.welcome.getStarted"),
        OnboardingStep.Done => Loc.S("onboarding.done.button"),
        _ => Loc.S("onboarding.footer.continue")
    };

    /// <summary>
    /// One reassurance line per step, forked per source where the step forks.
    /// Mirrors OnboardingView.swift:496-525.
    /// </summary>
    public string FooterReassurance => Step switch
    {
        OnboardingStep.Welcome => Loc.S("onboarding.footer.reassurance.welcome"),
        OnboardingStep.Permissions => Loc.S("onboarding.footer.reassurance.permissions"),
        OnboardingStep.Source => Loc.S("onboarding.footer.reassurance.source"),
        OnboardingStep.Configure => SelectedSource switch
        {
            OnboardingSourceKind.HyperWhisperCloud => Loc.S("onboarding.footer.reassurance.configure.cloud"),
            OnboardingSourceKind.OnDevice => Loc.S("onboarding.footer.reassurance.configure.onDevice"),
            OnboardingSourceKind.YourProvider => Loc.S("onboarding.footer.reassurance.configure.provider"),
            _ => Loc.S("onboarding.footer.reassurance.pickSource")
        },
        OnboardingStep.Setup => SelectedSource switch
        {
            OnboardingSourceKind.HyperWhisperCloud => Loc.S("onboarding.footer.reassurance.setup.cloud"),
            OnboardingSourceKind.OnDevice => Loc.S("onboarding.footer.reassurance.setup.onDevice"),
            OnboardingSourceKind.YourProvider => Loc.S("onboarding.footer.reassurance.setup.provider"),
            _ => Loc.S("onboarding.footer.reassurance.pickSource")
        },
        OnboardingStep.Microphone => Loc.S("onboarding.footer.reassurance.microphone"),
        OnboardingStep.TryIt => Loc.S("onboarding.footer.reassurance.tryIt"),
        _ => Loc.S("onboarding.footer.reassurance.done")
    };

    /// <summary>Zero-based, for the eight-segment progress hairline.</summary>
    public int StepIndex => (int)Step;

    public string ProgressAccessibleName =>
        Loc.S("onboarding.progress.a11y", StepIndex + 1, OnboardingSteps.Count);

    // =========================================================================
    // PERMISSIONS STEP
    // =========================================================================

    /// <summary>
    /// macOS switches this button between "Grant" and "Open Settings" on the
    /// undetermined case. An unpackaged Win32 app has no request-and-prompt API, so
    /// the Windows adapter never reports Undetermined and the button is always the
    /// deep link. The branch is kept because the enum keeps all three cases.
    /// </summary>
    public string MicrophoneActionText =>
        MicrophoneAuthorization == OnboardingMicrophoneAuthorization.Undetermined
            ? Loc.S("onboarding.permissions.grant")
            : Loc.S("onboarding.permissions.open");

    public bool IsShortcutRegistered => ShortcutStatus == OnboardingShortcutStatus.Registered;

    public bool IsShortcutFailed => ShortcutStatus == OnboardingShortcutStatus.Failed;

    /// <summary>
    /// Unknown is a real, renderable state and is NOT a failure: it is what the
    /// adapter reports before the app has ever registered the hotkey. It draws the
    /// neutral "checking" line, never the warning block.
    /// </summary>
    public bool IsShortcutUnknown => ShortcutStatus == OnboardingShortcutStatus.Unknown;

    /// <summary>
    /// The reason, or the generic "not registered" label when the adapter gave a
    /// status but no sentence. Never empty while <see cref="IsShortcutFailed"/>, so
    /// the warning note can never render blank.
    /// </summary>
    public string ShortcutFailureText =>
        string.IsNullOrWhiteSpace(ShortcutFailureReason)
            ? Loc.S("onboarding.permissions.shortcut.conflict")
            : ShortcutFailureReason!;

    // =========================================================================
    // CONFIGURE STEP
    // =========================================================================

    /// <summary>The BYOK chip strip. Rebuilt on every selection so each row carries its own flag.</summary>
    public IReadOnlyList<OnboardingProviderRow> ProviderOptions
    {
        get
        {
            var rows = new List<OnboardingProviderRow>();
            foreach (var provider in _providerKeys.Providers)
            {
                // CHECKED, not concatenated. See Models/ProviderAssets.cs: a vendor
                // whose logo does not ship renders a monogram rather than a blank
                // gap, and the row carries the empty string that says so.
                rows.Add(new OnboardingProviderRow(
                    provider,
                    provider.GetDisplayName(),
                    ProviderAssets.PathFor(provider.GetAssetName()) ?? string.Empty,
                    provider == SelectedProvider));
            }

            return rows;
        }
    }

    /// <summary>The curated on-device shortlist, with install state folded in.</summary>
    public IReadOnlyList<OnboardingModelRow> ModelOptions
    {
        get
        {
            var rows = new List<OnboardingModelRow>();
            foreach (var model in _catalog.Models)
            {
                rows.Add(new OnboardingModelRow(
                    model,
                    model.DisplayName,
                    Loc.S(model.SubtitleKey),
                    model.Size,
                    SelectedModel is not null && SelectedModel.Id == model.Id,
                    _catalog.IsInstalled(model)));
            }

            return rows;
        }
    }

    public string SelectedProviderDisplayName => SelectedProvider.GetDisplayName();

    private string TrimmedLicenseKey => LicenseKeyInput.Trim();

    private string TrimmedApiKey => ApiKeyInput.Trim();

    /// <summary>Testing is refused on an empty field, exactly as macOS disables the button.</summary>
    public bool CanTestAccessKey => !IsTestingKey && TrimmedLicenseKey.Length > 0;

    public bool CanTestProviderKey => !IsTestingKey && TrimmedApiKey.Length > 0;

    public bool ShowsLicenseTestPassed => !IsTestingKey && LicenseTestPassed == true;

    /// <summary>
    /// The inline failure line under the Cloud key field. Deliberately keyed on the
    /// test having actually failed, not merely on a message existing, so an error
    /// from the Setup step cannot leak backwards onto Configure.
    /// </summary>
    public bool ShowsLicenseTestFailed => !IsTestingKey && LicenseTestPassed == false && HasSetupError;

    public bool ShowsProviderTestHealthy => !IsTestingKey && ProviderTestHealth == ProviderHealth.Healthy;

    public bool ShowsProviderTestUnauthorized => !IsTestingKey && ProviderTestHealth == ProviderHealth.Unauthorized;

    /// <summary>
    /// The fourth pill: the key was SAVED, and this vendor cannot be checked
    /// without spending a transcription. Meta MuseSTT is the only one, and without
    /// this row its Unknown matched no pill at all - the spinner stopped, nothing
    /// appeared, and Continue stayed disabled with no explanation.
    ///
    /// Keyed on the provider as well as the health, because Unknown from a vendor
    /// that CAN be checked means something else entirely.
    /// </summary>
    public bool ShowsProviderTestUnverified =>
        !IsTestingKey
        && ProviderTestHealth == ProviderHealth.Unknown
        && !SelectedProvider.SupportsKeyHealthProbe();

    /// <summary>
    /// Unknown from a vendor that DOES have a validation endpoint is a probe that
    /// answered nothing, which is the same thing to a user as one that could not be
    /// reached. Without this arm it matched no pill either.
    /// </summary>
    public bool ShowsProviderTestUnreachable =>
        !IsTestingKey
        && (ProviderTestHealth == ProviderHealth.Unreachable
            || (ProviderTestHealth == ProviderHealth.Unknown && SelectedProvider.SupportsKeyHealthProbe()));

    public bool ShowsProviderTestError => !IsTestingKey && ProviderTestHealth is null && HasSetupError;

    // =========================================================================
    // SETUP STEP
    // =========================================================================

    public bool CanActivateLicense => !IsActivatingLicense && TrimmedLicenseKey.Length > 0;

    /// <summary>
    /// "Save API key" now probes before it writes, so it shares IsTestingKey with
    /// "Test API key" and must be disabled while a check is running.
    /// </summary>
    public bool CanSaveProviderKey => !IsTestingKey && TrimmedApiKey.Length > 0;

    /// <summary>
    /// macOS ticks "credits confirmed" only once the licence is genuinely active AND
    /// a balance has arrived (OnboardingSourceViews.swift:557-560). A pending fetch
    /// leaves the line untocked and blocks nothing.
    /// </summary>
    public bool AreCreditsConfirmed => IsSelectedSourceUsable && HasCredits;

    public string SelectedModelDisplayName => SelectedModel?.DisplayName ?? string.Empty;

    public string SelectedModelSubtitle =>
        SelectedModel is null ? string.Empty : Loc.S(SelectedModel.SubtitleKey);

    public string SelectedModelSizeText => SelectedModel?.Size ?? string.Empty;

    public int SelectedModelProgressPercent =>
        (int)Math.Round(Math.Clamp(SelectedModelProgress, 0, 1) * 100);

    public string SelectedModelProgressPercentText => $"{SelectedModelProgressPercent}%";

    public string DownloadButtonText =>
        Loc.S("onboarding.setup.onDevice.download", SelectedModelDisplayName);

    public string DownloadingPillText =>
        Loc.S("onboarding.setup.onDevice.downloading", SelectedModelProgressPercent);

    /// <summary>The download card only draws once a model has been chosen.</summary>
    public bool HasSelectedModel => SelectedModel is not null;

    /// <summary>Neither installed nor downloading: the state that offers the button.</summary>
    public bool ShowsDownloadButton =>
        HasSelectedModel && !IsSelectedModelInstalled && !IsSelectedModelDownloading;

    /// <summary>
    /// The three setup error lines. Each frames the manager's hard-coded English in
    /// localized copy, exactly as macOS does, and each reads the SAME single
    /// SetupErrorMessage funnel; nothing here consults a second error channel.
    /// </summary>
    public string SetupCloudErrorText =>
        HasSetupError ? Loc.S("onboarding.setup.cloud.error", SetupErrorMessage!) : string.Empty;

    public string SetupOnDeviceErrorText =>
        HasSetupError ? Loc.S("onboarding.setup.onDevice.error", SetupErrorMessage!) : string.Empty;

    public string SetupProviderErrorText =>
        HasSetupError ? Loc.S("onboarding.setup.provider.error", SetupErrorMessage!) : string.Empty;

    public string ProviderValidatedCheckText =>
        Loc.S("onboarding.setup.provider.check.validated", SelectedProviderDisplayName);

    public string ProviderCredentialItemText =>
        Loc.S("onboarding.setup.provider.keychainItem", SelectedProviderDisplayName);

    /// <summary>
    /// Never renders the whole key, even on the machine that just typed it. Mirrors
    /// OnboardingSourceViews.swift:759-763.
    ///
    /// Sliced on TEXT ELEMENTS, not on UTF-16 code units. <c>key[..8]</c> cut an
    /// astral character in half and rendered a lone surrogate, which draws as the
    /// replacement glyph; a combining mark or a flag emoji split the same way. Text
    /// elements are the smallest unit a user would call "a character", so both the
    /// length test and the two slices count the same thing the reader does.
    /// </summary>
    public string MaskedApiKey
    {
        get
        {
            var key = TrimmedApiKey;

            // The start index of every text element, so a slice can only ever land
            // on a boundary. Cheap: a key long enough to mask is still short.
            var starts = new List<int>();
            var elements = StringInfo.GetTextElementEnumerator(key);
            while (elements.MoveNext())
                starts.Add(elements.ElementIndex);

            return starts.Count > 12
                ? $"{key[..starts[8]]}…{key[starts[^4]..]}"
                : Loc.S("onboarding.setup.provider.keyHidden");
        }
    }

    // =========================================================================
    // MICROPHONE STEP
    // =========================================================================

    /// <summary>
    /// The step's own question. macOS has one string here because a Mac effectively
    /// always has an input; Windows genuinely does not, and "Say something. Watch the
    /// bars." asks for something impossible on a machine with no capture device, a
    /// blocked consent toggle, or an audio stack that would not enumerate. Found in a
    /// recording of the real flow on a box with zero devices.
    /// </summary>
    public string MicrophoneStepTitle => HasUsableMicrophone
        ? Loc.S("onboarding.mic.title")
        : Loc.S("onboarding.mic.title.unavailable");

    /// <summary>
    /// Whether to show "If the level moves when you talk, HyperWhisper can hear you."
    ///
    /// Suppressed rather than replaced. The specific, honest diagnosis for each of the
    /// other three availabilities is <see cref="MicrophoneHintText"/>, which is already
    /// on screen a few rows below inside the card; a second copy of it in the step's
    /// supporting line would put the same sentence twice on one screen, and a fourth
    /// generic sentence would say less than the one that is already there.
    /// </summary>
    public bool ShowsMicrophonePrompt => HasUsableMicrophone;

    public bool IsMicrophoneBlocked => DeviceAvailability == OnboardingDeviceAvailability.Blocked;

    public bool IsMicrophoneMissing => DeviceAvailability == OnboardingDeviceAvailability.NoDevices;

    /// <summary>
    /// Kept visibly distinct from <see cref="IsMicrophoneMissing"/>. Telling someone
    /// with a broken audio stack to go and buy a microphone is the failure this
    /// separation exists to prevent.
    /// </summary>
    public bool IsMicrophoneEnumerationFailed =>
        DeviceAvailability == OnboardingDeviceAvailability.EnumerationFailed;

    /// <summary>The device list, with the trailing detail label macOS shows on each row.</summary>
    public IReadOnlyList<OnboardingDeviceRow> DeviceRows
    {
        get
        {
            var openDeviceId = _audio.SelectedDeviceId;
            var rows = new List<OnboardingDeviceRow>();

            foreach (var device in DeviceOptions)
            {
                string detail;
                if (device.IsSystemDefault)
                {
                    // The synthetic first row names the device Windows is actually
                    // using, so "System Default" is never a mystery.
                    detail = string.IsNullOrEmpty(openDeviceId)
                        ? Loc.S("onboarding.mic.device.followsSystem")
                        : openDeviceId!;
                }
                else
                {
                    detail = device.Id == openDeviceId ? Loc.S("onboarding.mic.device.inUse") : string.Empty;
                }

                rows.Add(new OnboardingDeviceRow(
                    device.Id,
                    device.Name,
                    detail,
                    device.Id == SelectedDeviceId));
            }

            return rows;
        }
    }

    /// <summary>
    /// The hint under the meter. macOS forks it on the permission; Windows forks it
    /// on the four-case availability, because "no device" and "could not enumerate"
    /// are different diagnoses and neither is a permission problem.
    /// </summary>
    public string MicrophoneHintText => DeviceAvailability switch
    {
        OnboardingDeviceAvailability.Available => Loc.S("onboarding.mic.levelHint"),
        OnboardingDeviceAvailability.Blocked => Loc.S("onboarding.mic.blocked"),
        OnboardingDeviceAvailability.NoDevices => Loc.S("onboarding.mic.none"),
        _ => Loc.S("onboarding.mic.enumerationFailed")
    };

    /// <summary>A blocked microphone is the one case whose remedy is the privacy page.</summary>
    public bool ShowsPrivacySettingsAction => IsMicrophoneBlocked;

    /// <summary>Every non-blocked failure points at Sound settings instead.</summary>
    public bool ShowsSoundSettingsAction => !IsMicrophoneBlocked;

    public int InputLevelPercent => (int)Math.Round(Math.Clamp(InputLevel, 0f, 1f) * 100);

    public string InputLevelAccessibleValue => Loc.S("onboarding.a11y.percent", InputLevelPercent);

    // =========================================================================
    // TRY IT STEP
    // =========================================================================

    public bool IsTryItRecordMode => TryItMode == OnboardingTryItMode.Record;

    public bool IsTryItSampleMode => TryItMode == OnboardingTryItMode.Sample;

    /// <summary>
    /// The heading above the transcript. Mirrors OnboardingView.swift:389-393.
    /// </summary>
    public string TranscriptHeading
    {
        get
        {
            if (IsRecording)
                return Loc.S("onboarding.test.status.speak");
            if (IsTranscribingSample)
                return Loc.S("onboarding.tryIt.sample.transcribing");
            if (IsTranscribingTestRecording)
                return Loc.S("recording.state.transcribing");
            if (TranscriptIsError)
                return Loc.S("common.error");
            return Loc.S("onboarding.try.transcript.heading");
        }
    }

    /// <summary>
    /// "%1$d words · %2$@". macOS counts words in the view; so does this, because
    /// nothing else in the flow needs the number.
    /// </summary>
    public string TranscriptMeta
    {
        get
        {
            var words = TranscriptBody.Split(
                new[] { ' ', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Length;

            // The device line has to tell the truth about which of the two paths
            // produced this text, or the sample clip reads as a live recording.
            var origin = TranscriptCameFromSample
                ? Loc.S("onboarding.tryIt.sample.result")
                : SelectedDeviceName;

            return Loc.S("onboarding.tryIt.transcriptMeta", words, origin);
        }
    }

    public bool ShowsTranscriptMeta => HasTranscript && !TranscriptIsError && !IsRecording;

    public bool ShowsEmptyTranscriptHint =>
        !HasTranscript && !IsRecording && !IsTranscribingSample && !IsTranscribingTestRecording;

    /// <summary>
    /// The microphone path's "transcribing" pill. Without it the step showed
    /// "Nothing here yet" and an armed Record button for the whole of a local
    /// model's run, which is what invited a second, overlapping capture.
    /// </summary>
    public bool ShowsTestRecordingTranscribing => IsTryItRecordMode && IsTranscribingTestRecording;

    /// <summary>
    /// A non-fatal warning about the transcript that IS on screen. Never shown for
    /// an error transcript: that already reads as a failure, and two failure
    /// surfaces at once is worse than one.
    /// </summary>
    public bool ShowsTranscriptWarning => HasTranscriptWarning && HasTranscript && !TranscriptIsError && !IsRecording;

    /// <summary>
    /// macOS writes this as if/else-if/else in the view. Windows cannot, so the
    /// three-way choice becomes three flags and the view stays a straight mapping.
    /// </summary>
    public bool ShowsTranscriptBody => HasTranscript && !IsRecording;

    /// <summary>The "not pasted anywhere" row and its repeat button share one gate.</summary>
    public bool ShowsTranscriptFooterRow => HasTranscript && !IsRecording;

    /// <summary>The "Record again" row appears only once there is something to replace.</summary>
    public bool ShowsRecordAgain =>
        HasTranscript && !IsRecording && IsTryItRecordMode && !IsTranscribingTestRecording;

    /// <summary>The sample path's equivalent: run it again.</summary>
    public bool ShowsSampleAgain => HasTranscript && IsTryItSampleMode && !IsTranscribingSample;

    /// <summary>Record is offered only when a recording can actually be made.</summary>
    public bool ShowsRecordButton =>
        IsTryItRecordMode && !IsRecording && !HasTranscript && !IsTranscribingTestRecording;

    public bool ShowsStopButton => IsRecording;

    public bool ShowsRecordedPill => IsTryItRecordMode && !IsRecording && HasTranscript;

    /// <summary>
    /// The whole point of the sample path: the user must never be shown a button
    /// whose only possible outcome is an error toast.
    /// </summary>
    public bool ShowsSampleButton => IsTryItSampleMode && !IsTranscribingSample && !HasTranscript;

    // =========================================================================
    // DONE STEP
    // =========================================================================

    /// <summary>Mirrors OnboardingView.swift:450-469.</summary>
    public string SourceSummary => SelectedSource switch
    {
        OnboardingSourceKind.OnDevice =>
            Loc.S("onboarding.done.summary.onDevice",
                SelectedModel?.DisplayName ?? Loc.S("onboarding.source.onDevice.title")),

        OnboardingSourceKind.HyperWhisperCloud =>
            HasCredits
                ? Loc.S("onboarding.done.summary.cloud", Loc.S("onboarding.source.cloud.title"), CreditsCountFormatted)
                : Loc.S("onboarding.source.cloud.title"),

        OnboardingSourceKind.YourProvider =>
            Loc.S("onboarding.done.summary.provider", SelectedProviderDisplayName),

        _ => Loc.S("onboarding.setup.selectFirst")
    };

    /// <summary>
    /// macOS branches this on the Accessibility grant. Windows has no such grant, so
    /// it branches on the setting that actually decides where the text lands.
    /// </summary>
    public string TextDeliverySummary =>
        ReadAutoPasteEnabled()
            ? Loc.S("onboarding.done.textDelivery.cursor")
            : Loc.S("onboarding.done.textDelivery.clipboard");

    // =========================================================================
    // CHANGE FAN-OUT
    //
    // One place that maps a source property to everything derived from it. The
    // alternative is an OnPropertyChanged call in each of the flow model's setters,
    // which would have meant editing that file for a presentation concern.
    // =========================================================================

    private static readonly IReadOnlyDictionary<string, string[]> DerivedProperties =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [nameof(Step)] = new[]
            {
                nameof(PrimaryButtonText), nameof(FooterReassurance), nameof(StepIndex),
                nameof(ProgressAccessibleName)
            },
            [nameof(SelectedSource)] = new[]
            {
                nameof(IsCloudSelected), nameof(IsOnDeviceSelected), nameof(IsProviderSelected),
                nameof(HasNoSourceSelected), nameof(FooterReassurance), nameof(SourceSummary),
                nameof(SourceOptions)
            },
            [nameof(MicrophoneAuthorization)] = new[] { nameof(MicrophoneActionText) },
            [nameof(ShortcutStatus)] = new[]
            {
                nameof(IsShortcutRegistered), nameof(IsShortcutFailed), nameof(IsShortcutUnknown)
            },
            [nameof(ShortcutFailureReason)] = new[] { nameof(ShortcutFailureText) },
            [nameof(SelectedProvider)] = new[]
            {
                nameof(ProviderOptions), nameof(SelectedProviderDisplayName),
                nameof(ProviderValidatedCheckText), nameof(ProviderCredentialItemText),
                nameof(SourceSummary),
                // Two pills read the provider as well as the health: which of them
                // an Unknown belongs to depends on whether the vendor can be
                // probed at all.
                nameof(ShowsProviderTestUnverified), nameof(ShowsProviderTestUnreachable)
            },
            [nameof(SelectedModel)] = new[]
            {
                nameof(ModelOptions), nameof(SelectedModelDisplayName), nameof(SelectedModelSubtitle),
                nameof(SelectedModelSizeText), nameof(DownloadButtonText), nameof(HasSelectedModel),
                nameof(ShowsDownloadButton), nameof(SourceSummary)
            },
            [nameof(LicenseKeyInput)] = new[] { nameof(CanTestAccessKey), nameof(CanActivateLicense) },
            [nameof(ApiKeyInput)] = new[]
            {
                nameof(CanTestProviderKey), nameof(CanSaveProviderKey), nameof(MaskedApiKey)
            },
            [nameof(IsTestingKey)] = new[]
            {
                nameof(CanTestAccessKey), nameof(CanTestProviderKey), nameof(CanSaveProviderKey),
                nameof(ShowsLicenseTestPassed),
                nameof(ShowsLicenseTestFailed), nameof(ShowsProviderTestHealthy),
                nameof(ShowsProviderTestUnauthorized), nameof(ShowsProviderTestUnreachable),
                nameof(ShowsProviderTestUnverified), nameof(ShowsProviderTestError)
            },
            [nameof(LicenseTestPassed)] = new[]
            {
                nameof(ShowsLicenseTestPassed), nameof(ShowsLicenseTestFailed)
            },
            [nameof(ProviderTestHealth)] = new[]
            {
                nameof(ShowsProviderTestHealthy), nameof(ShowsProviderTestUnauthorized),
                nameof(ShowsProviderTestUnreachable), nameof(ShowsProviderTestUnverified),
                nameof(ShowsProviderTestError)
            },
            [nameof(IsActivatingLicense)] = new[] { nameof(CanActivateLicense) },
            [nameof(SetupErrorMessage)] = new[]
            {
                nameof(SetupCloudErrorText), nameof(SetupOnDeviceErrorText), nameof(SetupProviderErrorText),
                nameof(ShowsLicenseTestFailed), nameof(ShowsProviderTestError)
            },
            [nameof(IsSelectedSourceUsable)] = new[] { nameof(AreCreditsConfirmed) },
            [nameof(IsSelectedModelInstalled)] = new[]
            {
                nameof(ModelOptions), nameof(ShowsDownloadButton)
            },
            [nameof(IsSelectedModelDownloading)] = new[] { nameof(ShowsDownloadButton) },
            [nameof(SelectedModelProgress)] = new[]
            {
                nameof(SelectedModelProgressPercent), nameof(SelectedModelProgressPercentText),
                nameof(DownloadingPillText)
            },
            [nameof(HasCredits)] = new[] { nameof(AreCreditsConfirmed), nameof(SourceSummary) },
            [nameof(CreditsFormatted)] = new[] { nameof(SourceSummary) },
            [nameof(CreditsCountFormatted)] = new[] { nameof(SourceSummary) },
            [nameof(DeviceOptions)] = new[] { nameof(DeviceRows) },
            [nameof(SelectedDeviceId)] = new[] { nameof(DeviceRows) },
            [nameof(DeviceAvailability)] = new[]
            {
                nameof(IsMicrophoneBlocked), nameof(IsMicrophoneMissing),
                nameof(IsMicrophoneEnumerationFailed), nameof(MicrophoneHintText),
                nameof(ShowsPrivacySettingsAction), nameof(ShowsSoundSettingsAction),
                nameof(MicrophoneStepTitle), nameof(ShowsMicrophonePrompt)
            },
            [nameof(InputLevel)] = new[] { nameof(InputLevelPercent), nameof(InputLevelAccessibleValue) },
            [nameof(TryItMode)] = new[]
            {
                nameof(IsTryItRecordMode), nameof(IsTryItSampleMode), nameof(ShowsRecordButton),
                nameof(ShowsRecordedPill), nameof(ShowsSampleButton), nameof(ShowsRecordAgain),
                nameof(ShowsSampleAgain), nameof(ShowsTestRecordingTranscribing)
            },
            [nameof(IsRecording)] = new[]
            {
                nameof(TranscriptHeading), nameof(ShowsRecordButton), nameof(ShowsStopButton),
                nameof(ShowsRecordedPill), nameof(ShowsRecordAgain), nameof(ShowsTranscriptMeta),
                nameof(ShowsEmptyTranscriptHint), nameof(ShowsTranscriptBody),
                nameof(ShowsTranscriptFooterRow), nameof(ShowsTranscriptWarning)
            },
            [nameof(IsTranscribingSample)] = new[]
            {
                nameof(TranscriptHeading), nameof(ShowsSampleButton), nameof(ShowsSampleAgain),
                nameof(ShowsEmptyTranscriptHint)
            },
            [nameof(IsTranscribingTestRecording)] = new[]
            {
                nameof(TranscriptHeading), nameof(ShowsEmptyTranscriptHint), nameof(ShowsRecordButton),
                nameof(ShowsRecordAgain), nameof(ShowsTestRecordingTranscribing)
            },
            [nameof(TranscriptWarning)] = new[] { nameof(ShowsTranscriptWarning) },
            [nameof(Transcript)] = new[]
            {
                nameof(TranscriptHeading), nameof(TranscriptMeta), nameof(ShowsTranscriptMeta),
                nameof(ShowsEmptyTranscriptHint), nameof(ShowsRecordAgain), nameof(ShowsSampleAgain),
                nameof(ShowsRecordButton), nameof(ShowsRecordedPill), nameof(ShowsSampleButton),
                nameof(ShowsTranscriptBody), nameof(ShowsTranscriptFooterRow),
                nameof(ShowsTranscriptWarning)
            },
            [nameof(TranscriptCameFromSample)] = new[] { nameof(TranscriptMeta) },
            [nameof(SelectedDeviceName)] = new[] { nameof(TranscriptMeta) }
        };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is not { } name)
            return;

        if (!DerivedProperties.TryGetValue(name, out var derived))
            return;

        foreach (var property in derived)
            OnPropertyChanged(property);
    }
}
