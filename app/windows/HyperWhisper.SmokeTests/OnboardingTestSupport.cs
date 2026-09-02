// ONBOARDING TEST SUPPORT
//
// Fakes and a harness for the first-run flow model
// (HyperWhisper/ViewModels/Onboarding/OnboardingFlowViewModel.cs), mirroring the
// ones in app/macos/hyperwhisperTests/OnboardingFlowModelTests.swift.
//
// The cases themselves live in Program.cs, in the same Run(...) idiom as the rest of
// the suite; only the doubles live here so Program.cs does not grow another 600
// lines of scaffolding.
//
// Two fakes can PARK an asynchronous call on a TaskCompletionSource
// (FakeOnboardingLicense.GateProbe / GateActivation,
// FakeOnboardingProviderKeys.GateProbe). That is what makes the staleness and
// "landed after the window closed" cases deterministic: a fake that completes
// synchronously would run the whole continuation inside the call that started it,
// leaving no window in which the user can edit the field.

using HyperWhisper.Models;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.SmokeTests;

// =============================================================================
// Permissions
// =============================================================================

internal sealed class FakeOnboardingPermissions : IOnboardingPermissions
{
    public OnboardingMicrophoneAuthorization MicrophoneAuthorization { get; set; }
        = OnboardingMicrophoneAuthorization.Undetermined;

    public OnboardingShortcutState Shortcut { get; set; }
        = new("Ctrl+Shift+Space", OnboardingShortcutStatus.Registered, null);

    public bool RequestResult { get; set; } = true;
    public int RequestCount { get; private set; }
    public int OpenedMicrophoneSettings { get; private set; }
    public int OpenedShortcutSettings { get; private set; }
    public int ShortcutRefreshes { get; private set; }

    public event EventHandler? ShortcutChanged;

    public Task<bool> RequestMicrophoneAccessAsync()
    {
        RequestCount++;
        if (RequestResult)
            MicrophoneAuthorization = OnboardingMicrophoneAuthorization.Authorized;
        return Task.FromResult(RequestResult);
    }

    public void OpenMicrophonePrivacySettings() => OpenedMicrophoneSettings++;

    public void OpenShortcutSettings() => OpenedShortcutSettings++;

    public void RefreshShortcutRegistration()
    {
        ShortcutRefreshes++;
        ShortcutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The shortcut is re-registered, or the user edits it in Settings.</summary>
    public void Publish(OnboardingShortcutState state)
    {
        Shortcut = state;
        ShortcutChanged?.Invoke(this, EventArgs.Empty);
    }
}

// =============================================================================
// Model catalog
// =============================================================================

internal sealed class FakeOnboardingCatalog : IOnboardingModelCatalog
{
    public static readonly OnboardingModelSelection Parakeet = new(
        "parakeet-tdt-0.6b-v2", OnboardingModelKind.Parakeet, "Parakeet V2",
        "onboarding.model.parakeetV2.subtitle", "474 MB", 5, 3, IsRecommended: true);

    public static readonly OnboardingModelSelection Whisper = new(
        "base", OnboardingModelKind.Whisper, "Whisper Base",
        "onboarding.model.whisperBase.subtitle", "142 MB", 5, 1, IsRecommended: false);

    public List<OnboardingModelSelection> Catalog { get; } = new() { Parakeet, Whisper };
    public HashSet<string> Installed { get; } = new();
    public HashSet<string> Downloading { get; } = new();
    public Dictionary<string, double> Progresses { get; } = new();
    public List<string> StartedDownloads { get; } = new();

    public IReadOnlyList<OnboardingModelSelection> Models => Catalog;

    public event EventHandler<OnboardingDownloadErrors>? DownloadErrorsChanged;
    public event EventHandler? DownloadActivity;

    public bool IsInstalled(OnboardingModelSelection model) => Installed.Contains(model.Id);

    public bool IsDownloading(OnboardingModelSelection model) => Downloading.Contains(model.Id);

    public double Progress(OnboardingModelSelection model)
        => Progresses.TryGetValue(model.Id, out var value) ? value : 0;

    public void StartDownload(OnboardingModelSelection model) => StartedDownloads.Add(model.Id);

    public void PublishErrors(OnboardingDownloadErrors errors)
        => DownloadErrorsChanged?.Invoke(this, errors);

    /// <summary>
    /// Stands in for the download manager's change tick. Unthrottled, because the
    /// live adapter is where any coalescing lives.
    /// </summary>
    public void PublishActivity() => DownloadActivity?.Invoke(this, EventArgs.Empty);
}

// =============================================================================
// Licence
// =============================================================================

internal sealed class FakeOnboardingLicense : IOnboardingLicenseGateway
{
    public bool IsActive { get; set; }
    public OnboardingLicenseOutcome ProbeOutcome { get; set; } = new(true, null);
    public OnboardingLicenseOutcome ActivateOutcome { get; set; } = new(true, null);
    public List<string> ProbedKeys { get; } = new();
    public List<string> ActivatedKeys { get; } = new();

    /// <summary>When true, the next probe parks until <see cref="Release"/>.</summary>
    public bool GateProbe { get; set; }

    /// <summary>When true, the next activation parks until <see cref="Release"/>.</summary>
    public bool GateActivation { get; set; }

    private TaskCompletionSource<bool>? _gate;

    public async Task<OnboardingLicenseOutcome> ProbeAsync(string key, CancellationToken cancellationToken)
    {
        if (GateProbe)
        {
            _gate = new TaskCompletionSource<bool>();
            await _gate.Task;
        }

        ProbedKeys.Add(key);
        return ProbeOutcome;
    }

    public async Task<OnboardingLicenseOutcome> ActivateAsync(string key, CancellationToken cancellationToken)
    {
        if (GateActivation)
        {
            _gate = new TaskCompletionSource<bool>();
            await _gate.Task;
        }

        ActivatedKeys.Add(key);
        if (ActivateOutcome.IsValid)
            IsActive = true;
        return ActivateOutcome;
    }

    /// <summary>Let a parked call land, long after the window may have closed.</summary>
    public void Release()
    {
        var gate = _gate;
        _gate = null;
        gate?.TrySetResult(true);
    }

    public bool IsParked => _gate is not null;
}

// =============================================================================
// Credits
// =============================================================================

internal sealed class FakeOnboardingCredits : IOnboardingCreditsGateway
{
    public OnboardingCloudCredits? Credits { get; set; }
    public bool IsFetching { get; set; }
    public int RefreshCount { get; private set; }
    public bool ThrowOnRefresh { get; set; }

    /// <summary>What a successful refresh lands, if anything.</summary>
    public OnboardingCloudCredits? NextCredits { get; set; }

    public event EventHandler? CreditsChanged;

    public Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        RefreshCount++;

        if (ThrowOnRefresh)
            return Task.FromException(new InvalidOperationException("credits endpoint unreachable"));

        if (NextCredits is not null)
            Credits = NextCredits;

        return Task.CompletedTask;
    }

    public void Publish(OnboardingCloudCredits? credits)
    {
        Credits = credits;
        CreditsChanged?.Invoke(this, EventArgs.Empty);
    }
}

// =============================================================================
// Provider keys
// =============================================================================

internal sealed class FakeOnboardingProviderKeys : IOnboardingProviderKeyGateway
{
    /// <summary>Two is enough to prove the chip strip renders a list and marks one selected.</summary>
    public IReadOnlyList<CloudTranscriptionProvider> Providers { get; set; } = new[]
    {
        CloudTranscriptionProvider.OpenAI,
        CloudTranscriptionProvider.Groq
    };

    public string? ValidationError { get; set; }
    public ProviderHealth Health { get; set; } = ProviderHealth.Healthy;
    public bool PersistSucceeds { get; set; } = true;
    public Dictionary<CloudTranscriptionProvider, string> Stored { get; } = new();
    public int ProbeCount { get; private set; }

    /// <summary>When true, the next probe parks until <see cref="Release"/>.</summary>
    public bool GateProbe { get; set; }

    private TaskCompletionSource<bool>? _gate;

    public async Task<ProviderHealth> ProbeAsync(
        CloudTranscriptionProvider provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ProbeCount++;

        if (GateProbe)
        {
            _gate = new TaskCompletionSource<bool>();
            await _gate.Task;
        }

        return Health;
    }

    public void Release()
    {
        var gate = _gate;
        _gate = null;
        gate?.TrySetResult(true);
    }

    public bool Persist(string key, CloudTranscriptionProvider provider)
    {
        if (!PersistSucceeds)
        {
            ValidationError = "credential store denied";
            return false;
        }

        // Mirrors ApiKeyService: writing an empty string deletes the entry.
        if (key.Length == 0)
            Stored.Remove(provider);
        else
            Stored[provider] = key;

        return true;
    }

    public bool HasKey(CloudTranscriptionProvider provider) => Stored.ContainsKey(provider);

    public string CurrentKey(CloudTranscriptionProvider provider)
        => Stored.TryGetValue(provider, out var key) ? key : string.Empty;
}

// =============================================================================
// Audio
// =============================================================================

internal sealed class FakeOnboardingAudio : IOnboardingAudioGateway
{
    public static readonly OnboardingInputDevice[] ConnectedDevices =
    {
        new("builtin", "Realtek Microphone Array"),
        new("usb", "External USB Microphone")
    };

    private List<OnboardingInputDevice> _devices = new(ConnectedDevices);

    public IReadOnlyList<OnboardingInputDevice> Devices => _devices;

    public OnboardingDeviceAvailability Availability { get; set; } = OnboardingDeviceAvailability.Available;

    public string? SelectedDeviceId { get; set; }
    public string? StoredDeviceId { get; set; }

    public int RefreshDeviceCalls { get; private set; }
    public int RefreshAuthorizationCalls { get; private set; }
    public int PreviewStarts { get; private set; }
    public int PreviewStops { get; private set; }
    public int ToggleCalls { get; private set; }
    public int StopForExitCalls { get; private set; }
    public int ClearTranscriptCalls { get; private set; }
    public int SampleTranscriptions { get; private set; }

    public bool HasSampleClip { get; set; } = true;
    public string SampleTranscript { get; set; } = "This is the bundled sample clip.";
    public bool SampleThrows { get; set; }

    public float InputLevel { get; private set; }
    public bool IsRecording { get; private set; }
    public string Transcript { get; private set; } = string.Empty;

    public event EventHandler? DevicesChanged;
    public event EventHandler<float>? InputLevelChanged;
    public event EventHandler? IsRecordingChanged;
    public event EventHandler? TranscriptChanged;

    public void RefreshDevices() => RefreshDeviceCalls++;

    public void RefreshMicrophoneAuthorization() => RefreshAuthorizationCalls++;

    public void SelectDevice(string? id)
    {
        SelectedDeviceId = id;
        StoredDeviceId = id;
    }

    /// <summary>
    /// Mirrors the live adapter: the preference goes back even when the device it
    /// names is absent, while the open device only reopens if it is still present.
    /// </summary>
    public void RestoreDevice(string? storedId, string? openId)
    {
        StoredDeviceId = storedId;
        SelectedDeviceId = _devices.Any(d => d.Id == openId) ? openId : null;
    }

    public void StartInputLevelPreview() => PreviewStarts++;

    public void StopInputLevelPreview() => PreviewStops++;

    public void ToggleTestRecording() => ToggleCalls++;

    public void StopRecordingForExit() => StopForExitCalls++;

    public void ClearTranscript()
    {
        ClearTranscriptCalls++;
        PublishTranscript(string.Empty);
    }

    public Task TranscribeSampleClipAsync(CancellationToken cancellationToken)
    {
        SampleTranscriptions++;

        if (SampleThrows)
            return Task.FromException(new InvalidOperationException("sample clip missing"));

        PublishTranscript(SampleTranscript);
        return Task.CompletedTask;
    }

    // --- Test drivers --------------------------------------------------------

    /// <summary>A device is plugged in or pulled out while the step is open.</summary>
    public void Publish(IEnumerable<OnboardingInputDevice> devices)
    {
        _devices = devices.ToList();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Consent or the audio stack changes without the list changing.</summary>
    public void PublishAvailability(OnboardingDeviceAvailability availability)
    {
        Availability = availability;
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PublishTranscript(string text)
    {
        Transcript = text;
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PublishRecording(bool recording)
    {
        IsRecording = recording;
        IsRecordingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PublishLevel(float level)
    {
        InputLevel = level;
        InputLevelChanged?.Invoke(this, level);
    }
}

// =============================================================================
// Committer
// =============================================================================

internal sealed record FakeOnboardingRestorePoint(string State) : IOnboardingRestorePoint;

/// <summary>
/// Stands in for the database, SettingsService and the shell. ProductionState is the
/// single observable fact the rollback cases assert on.
/// </summary>
internal sealed class FakeOnboardingCommitter : IOnboardingSourceCommitter
{
    public const string Seed = "seeded-default-mode";

    public string ProductionState { get; private set; } = Seed;
    public List<OnboardingStagedSource> Applied { get; } = new();
    public int CaptureCount { get; private set; }
    public int RestoreCount { get; private set; }
    public int MarkCompletedCount { get; private set; }
    public int ReturnHomeCount { get; private set; }

    public IOnboardingRestorePoint CaptureRestorePoint()
    {
        CaptureCount++;
        return new FakeOnboardingRestorePoint(ProductionState);
    }

    public void Apply(OnboardingStagedSource staged)
    {
        Applied.Add(staged);
        ProductionState = $"{staged.Source.Identifier()}:{staged.Model}:{staged.CloudProvider ?? "-"}";
    }

    public void Restore(IOnboardingRestorePoint point)
    {
        RestoreCount++;
        if (point is FakeOnboardingRestorePoint restore)
            ProductionState = restore.State;
    }

    public void MarkOnboardingCompleted() => MarkCompletedCount++;

    public void ReturnToHome() => ReturnHomeCount++;
}

// =============================================================================
// Harness
// =============================================================================

internal sealed class OnboardingHarness
{
    public const string SystemDefaultName = "System Default";

    public FakeOnboardingPermissions Permissions { get; } = new();
    public FakeOnboardingCatalog Catalog { get; } = new();
    public FakeOnboardingLicense License { get; } = new();
    public FakeOnboardingCredits Credits { get; } = new();
    public FakeOnboardingProviderKeys ProviderKeys { get; } = new();
    public FakeOnboardingAudio Audio { get; } = new();
    public FakeOnboardingCommitter Committer { get; } = new();
    public OnboardingFlowViewModel Flow { get; }

    public OnboardingHarness()
    {
        Flow = new OnboardingFlowViewModel(
            Permissions,
            Catalog,
            License,
            Credits,
            ProviderKeys,
            Audio,
            Committer,
            SystemDefaultName);
    }

    /// <summary>The most recent asynchronous action, awaitable exactly once it exists.</summary>
    public Task LastTask => Flow.LastAsyncTaskForTesting ?? Task.CompletedTask;

    /// <summary>Walk to a step, failing loudly at whichever gate refuses.</summary>
    public void AdvanceTo(OnboardingStep target)
    {
        while (Flow.Step < target)
        {
            var from = Flow.Step;
            if (!Flow.Advance())
                throw new InvalidOperationException($"blocked at {from} on the way to {target}");
        }
    }

    /// <summary>Grant the microphone so the permissions gate opens.</summary>
    public void GrantMicrophone()
    {
        Permissions.MicrophoneAuthorization = OnboardingMicrophoneAuthorization.Authorized;
        Flow.RefreshPermissions();
    }

    /// <summary>The shortest path to a usable on-device source.</summary>
    public void StageInstalledOnDeviceModel()
    {
        GrantMicrophone();
        Flow.SelectSource(OnboardingSourceKind.OnDevice);
        Flow.SelectModel(FakeOnboardingCatalog.Parakeet);
        Catalog.Installed.Add(FakeOnboardingCatalog.Parakeet.Id);
    }
}
