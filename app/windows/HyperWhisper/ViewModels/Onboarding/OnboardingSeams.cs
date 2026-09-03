// ONBOARDING SEAMS
//
// The narrow interfaces the first-run flow model talks to, plus every value type
// that crosses them. Mirrors the six protocols in
// app/macos/hyperwhisper/Views/Onboarding/OnboardingFlowModel.swift (lines 125-218),
// with a seventh added for the cloud credits figure: macOS injects
// HyperWhisperCloudManager into the sheet as an @EnvironmentObject and reads
// `credits` straight from the views (OnboardingView.swift:63-64), which the Windows
// rule "only OnboardingLiveDependencies knows about singletons" forbids.
//
// Two shape changes are deliberate and are described in full in the PR:
//   - Permissions row 2 is the global shortcut, not Accessibility. Windows has no
//     Accessibility grant (RegisterHotKey / SendInput need no user consent), but
//     registering the shortcut genuinely fails - Win32 1409 "hot key already
//     registered" is a daily occurrence - so the row carries a three-state status
//     and a sentence rather than macOS's bare Bool.
//   - The audio seam carries a four-case availability, because Windows really does
//     have zero capture devices and really can fail to enumerate, and those two are
//     different diagnoses.
//
// macOS's Combine publishers become plain events: System.Reactive is not a
// dependency of this head. A seam that mirrors a CurrentValueSubject exposes a
// current-value property AND a change event, and the flow reads the property when
// the event fires.

using HyperWhisper.Models;

namespace HyperWhisper.ViewModels.Onboarding;

// =============================================================================
// Value types crossing the seams
// =============================================================================

/// <summary>
/// The transcription source the user is configuring. macOS reuses its own
/// <c>TranscriptionSource</c>; the Windows head has no such enum (a Mode stores a
/// raw "local"/"cloud" <c>ProviderType</c> string), so the flow owns this one.
/// </summary>
public enum OnboardingSourceKind
{
    OnDevice,
    HyperWhisperCloud,
    YourProvider
}

/// <summary>Which local engine a curated onboarding model belongs to.</summary>
public enum OnboardingModelKind
{
    Whisper,
    Parakeet
}

/// <summary>
/// Microphone consent, reduced to the three cases the flow reacts to.
/// <see cref="Undetermined"/> is kept for shape parity with macOS and with the
/// shared gating table; an unpackaged Win32 app has no request-and-prompt API, so
/// the Windows adapter never returns it.
/// </summary>
public enum OnboardingMicrophoneAuthorization
{
    Undetermined,
    Authorized,
    Denied
}

/// <summary>
/// Whether the configured global shortcut is actually registered right now.
/// <see cref="Unknown"/> is a real, renderable state (the window has no HWND yet)
/// and is NOT a failure.
/// </summary>
public enum OnboardingShortcutStatus
{
    Unknown,
    Registered,
    Failed
}

/// <summary>
/// Why the microphone step cannot list devices. Ordered by how the adapter
/// decides: a privacy block outranks an enumeration failure, which outranks an
/// empty-but-successful list.
/// </summary>
public enum OnboardingDeviceAvailability
{
    /// <summary>At least one capture device is present and consent is granted.</summary>
    Available,

    /// <summary>Microphone access is denied system-wide or for desktop apps.</summary>
    Blocked,

    /// <summary>Enumeration succeeded and returned nothing.</summary>
    NoDevices,

    /// <summary>Enumeration itself failed, i.e. the audio stack is broken.</summary>
    EnumerationFailed
}

/// <summary>How the Try It step can produce a transcript on this machine.</summary>
public enum OnboardingTryItMode
{
    /// <summary>Record from the microphone.</summary>
    Record,

    /// <summary>Transcribe the bundled sample clip: no capture device exists.</summary>
    Sample
}

/// <summary>
/// One selectable input device. <see cref="Id"/> is empty for the synthetic
/// "System Default" row, which is always offered first.
/// </summary>
public readonly record struct OnboardingInputDevice(string Id, string Name)
{
    /// <summary>
    /// The synthetic first option. An empty id means "follow the system default",
    /// which is how the rest of the app already encodes it.
    /// </summary>
    public static OnboardingInputDevice SystemDefault(string name) => new(string.Empty, name);

    public bool IsSystemDefault => string.IsNullOrEmpty(Id);
}

/// <summary>
/// The last known download error for each local engine. Both are carried so the
/// flow can pick the one matching the selected model rather than defaulting to
/// Whisper, which is how Parakeet failures used to disappear on macOS (bug 2).
/// </summary>
public sealed record OnboardingDownloadErrors(string? Whisper, string? Parakeet)
{
    public static readonly OnboardingDownloadErrors None = new(null, null);

    public string? Message(OnboardingModelKind kind) => kind switch
    {
        OnboardingModelKind.Whisper => Whisper,
        OnboardingModelKind.Parakeet => Parakeet,
        _ => null
    };
}

/// <summary>Result of a licence probe or activation, reduced to what the flow needs.</summary>
public sealed record OnboardingLicenseOutcome(bool IsValid, string? ErrorMessage)
{
    public static OnboardingLicenseOutcome Failure(string? message) => new(false, message);

    public static OnboardingLicenseOutcome Success() => new(true, null);
}

/// <summary>
/// One curated on-device model offered during onboarding. Deliberately spans BOTH
/// local engines behind one identity, because <see cref="Id"/> is written verbatim
/// to the Mode's model field and the transcription router keys the engine off the
/// "parakeet-tdt-" prefix.
/// </summary>
public sealed record OnboardingModelSelection(
    string Id,
    OnboardingModelKind Kind,
    string DisplayName,
    string SubtitleKey,
    string Size,
    int Speed,
    int Accuracy,
    bool IsRecommended);

/// <summary>
/// The fully staged source configuration. Nothing in here has touched the
/// database, SettingsService, or the credential store. It is handed to the
/// committer once, at an explicit commit boundary.
/// </summary>
public sealed record OnboardingStagedSource(
    OnboardingSourceKind Source,
    string Model,
    string? CloudProvider,
    int PostProcessingMode,
    string? CloudAccuracyTier);

/// <summary>
/// The configured global shortcut and whether it is registered. macOS's row is a
/// single Bool because Accessibility is granted or not; a Windows hotkey can fail
/// for a reason worth printing, so the sentence travels with the state.
/// </summary>
/// <param name="DisplayText">
/// Already formatted for display (the app's own <c>ToDisplayString()</c>, parts
/// joined with "+", "Unassigned" when empty), so the UI can split it into keycaps.
/// </param>
/// <param name="FailureReason">
/// A user-facing sentence, never a Win32 code. The adapter maps the code; the flow
/// model never sees an error number.
/// </param>
public sealed record OnboardingShortcutState(
    string DisplayText,
    OnboardingShortcutStatus Status,
    string? FailureReason)
{
    public static readonly OnboardingShortcutState Unknown =
        new(string.Empty, OnboardingShortcutStatus.Unknown, null);
}

/// <summary>
/// The HyperWhisper Cloud balance, flattened to exactly what the two cloud panels
/// display so the flow never holds a live service object.
/// </summary>
public sealed record OnboardingCloudCredits(
    double CreditsRemaining,
    int MinutesRemaining,
    string FormattedBalance);

/// <summary>
/// Opaque marker for whatever the committer needs in order to put production state
/// back. The flow only ever stores and returns it, so the Mode detail stays out of
/// the presentation layer and out of the tests.
/// </summary>
public interface IOnboardingRestorePoint
{
}

/// <summary>Extensions over the source enum.</summary>
public static class OnboardingSourceKindExtensions
{
    /// <summary>
    /// The stable string identity, matching the macOS <c>TranscriptionSource</c>
    /// raw values so both platforms name the same thing the same way.
    /// </summary>
    public static string Identifier(this OnboardingSourceKind source) => source switch
    {
        OnboardingSourceKind.OnDevice => "onDevice",
        OnboardingSourceKind.HyperWhisperCloud => "hyperwhisperCloud",
        OnboardingSourceKind.YourProvider => "yourProvider",
        _ => string.Empty
    };
}

// =============================================================================
// Seams
// =============================================================================

/// <summary>
/// Permission reads, the shortcut registration check, and the two Settings deep
/// links.
/// </summary>
public interface IOnboardingPermissions
{
    OnboardingMicrophoneAuthorization MicrophoneAuthorization { get; }

    /// <summary>The configured toggle shortcut and its registration outcome.</summary>
    OnboardingShortcutState Shortcut { get; }

    /// <summary>
    /// Ask the OS for microphone access. Windows cannot prompt, so the live
    /// adapter re-reads consent and returns the answer; the seam keeps the async
    /// shape because macOS genuinely prompts here.
    /// </summary>
    Task<bool> RequestMicrophoneAccessAsync();

    void OpenMicrophonePrivacySettings();

    /// <summary>
    /// Persist a new toggle shortcut, chosen on the Permissions step itself.
    ///
    /// This replaces a deep link into the Shortcuts settings section. The onboarding
    /// window is application-modal, so the shell behind it cannot be typed into
    /// until the flow ends: the link raised a page the user could look at and not
    /// use, which is worse than no offer at all. macOS has the same button and the
    /// same problem, and no answer for it, because its sheet is modal too.
    ///
    /// The parameter is the PERSISTED string, not a WPF <c>Key</c>, so the seam and
    /// the flow model stay free of WPF - the flow-model suite runs with no
    /// Application at all. <c>KeyboardShortcut.FromPersistedString</c> round-trips it.
    ///
    /// DELIBERATELY NOT REVERSIBLE, unlike every other write the flow makes. The
    /// user only reaches this control because their configured shortcut FAILED to
    /// register, and "Set Up Later" putting the broken one back would undo the one
    /// thing they came here to fix. It is also not first-run state: it is a setting,
    /// and it outlives the flow the same way it would if they had changed it in
    /// Settings a minute later.
    /// </summary>
    /// <returns>true if it was stored.</returns>
    bool SetToggleShortcut(string persistedShortcut);

    /// <summary>
    /// Re-run the registration check and raise <see cref="ShortcutChanged"/>.
    /// This replaces macOS's polling <c>waitForAccessibilityPermission</c>, which
    /// has no Windows analogue: the check is cheap and is run on demand.
    /// </summary>
    void RefreshShortcutRegistration();

    event EventHandler? ShortcutChanged;
}

/// <summary>The curated on-device shortlist plus download state for BOTH engines.</summary>
public interface IOnboardingModelCatalog
{
    IReadOnlyList<OnboardingModelSelection> Models { get; }

    bool IsInstalled(OnboardingModelSelection model);

    bool IsDownloading(OnboardingModelSelection model);

    /// <summary>
    /// Download progress as a FRACTION, 0 to 1, and 0 when nothing is running.
    /// Every consumer treats it that way: OnboardingProgressBar clamps its Value to
    /// [0,1] and the percent label multiplies by 100. An adapter over a service
    /// that reports percentages converts.
    /// </summary>
    double Progress(OnboardingModelSelection model);

    void StartDownload(OnboardingModelSelection model);

    /// <summary>
    /// Raised whenever either engine's error message changes. Carrying both in one
    /// value is what lets a Parakeet failure reach the UI.
    /// </summary>
    event EventHandler<OnboardingDownloadErrors>? DownloadErrorsChanged;

    /// <summary>
    /// Raised whenever download state or progress moves for either engine. The
    /// catalog reads are plain method calls, so without this tick nothing tells the
    /// binding layer that the setup step's progress bar has moved.
    /// </summary>
    event EventHandler? DownloadActivity;
}

/// <summary>
/// HyperWhisper Cloud. <see cref="ProbeAsync"/> is read-only;
/// <see cref="ActivateAsync"/> is the single explicit action that writes account
/// state. Entitlement itself stays server side.
/// </summary>
public interface IOnboardingLicenseGateway
{
    bool IsActive { get; }

    Task<OnboardingLicenseOutcome> ProbeAsync(string key, CancellationToken cancellationToken);

    Task<OnboardingLicenseOutcome> ActivateAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// The cloud credit balance. Display only: it never gates Continue and a failed
/// fetch never becomes a setup error.
/// </summary>
public interface IOnboardingCreditsGateway
{
    /// <summary>The last known balance, or null when it has never been read.</summary>
    OnboardingCloudCredits? Credits { get; }

    bool IsFetching { get; }

    Task RefreshAsync(bool force, CancellationToken cancellationToken);

    event EventHandler? CreditsChanged;
}

/// <summary>Bring-your-own-key providers.</summary>
public interface IOnboardingProviderKeyGateway
{
    /// <summary>
    /// The providers the Configure step offers, in display order. macOS keeps this
    /// list as a static on the view itself
    /// (OnboardingSourceViews.swift:200-202); Windows cannot, because a view that
    /// owned the list would also own the policy of which providers are safe to
    /// offer, and that policy is exactly what excludes the two whose health probe
    /// short-circuits to Healthy without a key.
    /// </summary>
    IReadOnlyList<CloudTranscriptionProvider> Providers { get; }

    string? ValidationError { get; }

    Task<ProviderHealth> ProbeAsync(
        CloudTranscriptionProvider provider,
        string apiKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns false when the credential write failed. A healthy probe alone must
    /// never be treated as a pass.
    /// </summary>
    bool Persist(string key, CloudTranscriptionProvider provider);

    bool HasKey(CloudTranscriptionProvider provider);

    /// <summary>
    /// Whatever is stored for this provider right now, or "" when nothing is.
    /// Snapshotted before the flow's first write so deferral can put it back, and
    /// an empty string round-trips as "delete the entry" through
    /// <see cref="Persist"/>.
    /// </summary>
    string CurrentKey(CloudTranscriptionProvider provider);
}

/// <summary>
/// Input devices, the idle level preview, the "give it a try" recording, and the
/// sample-clip fallback for a machine with no microphone.
/// </summary>
public interface IOnboardingAudioGateway
{
    IReadOnlyList<OnboardingInputDevice> Devices { get; }

    /// <summary>Why the device list is what it is. Computed by the adapter.</summary>
    OnboardingDeviceAvailability Availability { get; }

    /// <summary>
    /// Raised for BOTH a list change and an availability change, so plugging a
    /// microphone in while the step is open recovers live.
    /// </summary>
    event EventHandler? DevicesChanged;

    /// <summary>The device actually open right now. null means the system default.</summary>
    string? SelectedDeviceId { get; }

    /// <summary>
    /// The persisted preference, which is what deferral has to put back. It can
    /// name a device that is not connected, in which case it differs from
    /// <see cref="SelectedDeviceId"/>.
    /// </summary>
    string? StoredDeviceId { get; }

    void RefreshDevices();

    void RefreshMicrophoneAuthorization();

    void SelectDevice(string? id);

    /// <summary>
    /// Undo a selection. <see cref="SelectDevice"/> performs two writes, so
    /// deferral has to put both back: the stored preference (which survives even
    /// when the device it names is absent) and whichever device was actually open.
    /// </summary>
    void RestoreDevice(string? storedId, string? openId);

    /// <summary>
    /// No-op unless <see cref="Availability"/> is Available. Returns true only when
    /// a capture stream is genuinely running afterwards: a device can enumerate and
    /// still refuse to open, and the flow must not light the meter for one that did.
    /// </summary>
    bool StartInputLevelPreview();

    void StopInputLevelPreview();

    float InputLevel { get; }

    event EventHandler<float>? InputLevelChanged;

    /// <summary>
    /// Begin the Try It capture. False when nothing is recording afterwards, in
    /// which case the reason has already been published on <see cref="Transcript"/>.
    /// </summary>
    bool StartTestRecording();

    /// <summary>
    /// Stop the Try It capture and transcribe it. The task is returned rather than
    /// discarded so the flow can own it in its task box, exactly as it owns the
    /// sample-clip path: without that there is no transcribing state, no
    /// re-entrancy guard, and nothing for teardown to cancel.
    /// </summary>
    Task StopAndTranscribeAsync(CancellationToken cancellationToken);

    /// <summary>True when the bundled sample clip is present in this build.</summary>
    bool HasSampleClip { get; }

    /// <summary>
    /// Run the bundled clip through the same transcription path as a recording and
    /// publish the result on <see cref="Transcript"/>.
    /// </summary>
    Task TranscribeSampleClipAsync(CancellationToken cancellationToken);

    /// <summary>Privacy backstop on every exit path. Deliberately not gated on IsRecording.</summary>
    void StopRecordingForExit();

    void ClearTranscript();

    bool IsRecording { get; }

    event EventHandler? IsRecordingChanged;

    /// <summary>
    /// The transcript, or an error carried with the "Error:" sentinel so the view
    /// can render it differently.
    /// </summary>
    string Transcript { get; }

    event EventHandler? TranscriptChanged;

    /// <summary>
    /// A non-fatal warning raised while producing the current transcript, or null.
    ///
    /// This exists because post-processing can be SKIPPED and still return text.
    /// Five of the six seeded Modes post-process through a cloud LLM, so a 401 or a
    /// timeout leaves the user looking at a raw, un-post-processed transcript under
    /// full success chrome and concluding the source works. The GUI's toast is the
    /// wrong surface behind a modal, and TranscriptionResult carries no warning
    /// field, so the adapter forwards the orchestrator's own event on this channel
    /// and the Try It panel renders it inline.
    /// </summary>
    string? TranscriptWarning { get; }

    event EventHandler? TranscriptWarningChanged;
}

/// <summary>The one and only path from staged configuration to production state.</summary>
public interface IOnboardingSourceCommitter
{
    /// <summary>Snapshot everything <see cref="Apply"/> is about to overwrite.</summary>
    IOnboardingRestorePoint CaptureRestorePoint();

    void Apply(OnboardingStagedSource staged);

    /// <summary>
    /// Put back what <see cref="Apply"/> wrote.
    /// </summary>
    /// <returns>
    /// True when production state is back. A committer that swallowed a database
    /// failure and answered void let the flow discard the restore point and
    /// close over a Mode that was still the staged one, with nothing left to
    /// retry from. Same contract as
    /// <see cref="IOnboardingProviderKeyGateway.Persist"/>, which is the other
    /// sink here that can refuse a write.
    /// </returns>
    bool Restore(IOnboardingRestorePoint point);

    void MarkOnboardingCompleted();

    void ReturnToHome();
}
