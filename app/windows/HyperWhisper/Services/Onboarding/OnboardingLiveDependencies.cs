// ONBOARDING LIVE DEPENDENCIES
//
// The adapters that bind the first-run flow model's seven seams
// (ViewModels/Onboarding/OnboardingSeams.cs) to the app's real services. The
// Windows port of
// app/macos/hyperwhisper/Views/Onboarding/OnboardingLiveDependencies.swift.
//
// Everything here is thin plumbing on purpose: the policy lives in
// OnboardingFlowViewModel so it can be exercised without a database, the
// credential store, a microphone, or the network.
//
// THIS FILE AND ITS AUDIO SIBLING ARE THE ONLY PLACES THAT KNOW ABOUT SINGLETONS.
// The Windows head has no DI container - services are hand-rolled lazy singletons
// - so the rule that keeps the flow testable is that Instance is resolved here
// and nowhere else. The flow view model takes its seven seams as constructor
// arguments and never reaches for a global.
//
// macOS's Combine publishers become plain events: System.Reactive is not a
// dependency of this head.
//
// The audio gateway lives in OnboardingLiveAudioGateway.cs - it is half the size
// of this file on its own, because Windows has neither an idle level meter nor a
// sample-clip path to reuse.

using System.IO;
using System.Reflection;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

// =============================================================================
// Permissions
// =============================================================================

/// <summary>
/// Microphone consent plus the "your shortcut works" row.
///
/// Row 2 is the global shortcut rather than macOS's Accessibility grant, because
/// Windows has no Accessibility analogue: RegisterHotKey, SetWindowsHookEx,
/// SendInput and UI Automation all work here without a user grant. Registering
/// the shortcut genuinely fails though - Win32 1409 "already registered by
/// another application" is a daily occurrence - so the row is a real
/// environmental precondition with three renderings and a sentence to show.
/// Like Accessibility on macOS it never gates Continue.
/// </summary>
public sealed class LiveOnboardingPermissions : IOnboardingPermissions
{
    /// <summary>
    /// The name MainViewModel.RegisterShortcutsFromSettings registers the toggle
    /// shortcut under. The two screens must agree, so this is keyed off the same
    /// string rather than a second copy of the shortcut.
    /// </summary>
    private const string ToggleShortcutName = "toggle";

    private readonly Action _openShortcutSettings;

    public LiveOnboardingPermissions(Action openShortcutSettings)
    {
        _openShortcutSettings = openShortcutSettings;
    }

    /// <summary>
    /// Never Undetermined. An unpackaged Win32 app has no request-and-prompt API:
    /// the consent is a system-wide toggle, so the answer is only ever allowed or
    /// denied. The third case is kept on the enum for shape parity with the shared
    /// gating table.
    /// </summary>
    public OnboardingMicrophoneAuthorization MicrophoneAuthorization =>
        MicrophonePrivacyService.ReadConsent() == MicrophoneConsent.Allowed
            ? OnboardingMicrophoneAuthorization.Authorized
            : OnboardingMicrophoneAuthorization.Denied;

    public OnboardingShortcutState Shortcut { get; private set; } = OnboardingShortcutState.Unknown;

    public event EventHandler? ShortcutChanged;

    /// <summary>
    /// Windows cannot prompt, so this re-reads consent and answers. The seam keeps
    /// the async shape because macOS genuinely prompts here.
    /// </summary>
    public Task<bool> RequestMicrophoneAccessAsync()
    {
        return Task.FromResult(MicrophoneAuthorization == OnboardingMicrophoneAuthorization.Authorized);
    }

    public void OpenMicrophonePrivacySettings() => MicrophonePrivacyService.OpenPrivacySettings();

    public void OpenShortcutSettings()
    {
        try
        {
            _openShortcutSettings();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingPermissions: Could not open the shortcut editor: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-read the registration outcome and publish it.
    ///
    /// It deliberately does NOT re-register. A Win32 hotkey is per-thread, so a
    /// second RegisterHotKey for the same combination fails with 1409 against
    /// ourselves; and KeyboardShortcutService.RegisterShortcut unregisters before
    /// it re-registers, so a competing attempt that lost would leave the user with
    /// no working hotkey at all. The app already re-registers everything whenever
    /// settings change (MainViewModel.OnSettingsChanged), which is exactly what
    /// happens when the user edits the shortcut from this step - so reading the
    /// recorded outcome is both current and side-effect free.
    ///
    /// This also replaces macOS's polling waitForAccessibilityPermission, which
    /// has no Windows analogue.
    /// </summary>
    public void RefreshShortcutRegistration()
    {
        var shortcut = SettingsService.Instance.ToggleShortcut;

        // The same call ShortcutsSettingsPage makes, so the two screens can never
        // disagree about what the shortcut is called.
        var display = shortcut.ToDisplayString();

        var state = BuildState(display, shortcut, KeyboardShortcutService.Current?.GetLastRegistrationResult(ToggleShortcutName));

        if (state != Shortcut)
        {
            Shortcut = state;
        }

        // Raised unconditionally: the caller asked for a refresh and the view has
        // to be told the answer arrived even when it did not change.
        ShortcutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Turns a registration Result into the row's three states. The ADAPTER maps
    /// the Win32 code into a sentence; the flow model never sees an error number.
    /// </summary>
    internal static OnboardingShortcutState BuildState(
        string displayText,
        KeyboardShortcut shortcut,
        Result? registration)
    {
        // Never registered through this service: no window yet, or the app has not
        // reached RegisterShortcutsFromSettings. Unknown is a real renderable
        // state and is NOT a failure.
        if (registration is not { } result)
        {
            return new OnboardingShortcutState(displayText, OnboardingShortcutStatus.Unknown, null);
        }

        if (result.IsSuccess)
        {
            return new OnboardingShortcutState(displayText, OnboardingShortcutStatus.Registered, null);
        }

        // "HwndSource is null" means the main window had no HWND when the attempt
        // ran. That is an ordering fact about the app, not a verdict about the
        // user's shortcut, so it must never render as a conflict.
        if (result.Error?.Contains("HwndSource is null", StringComparison.Ordinal) == true)
        {
            return new OnboardingShortcutState(displayText, OnboardingShortcutStatus.Unknown, null);
        }

        var code = ShortcutValidationService.ExtractWin32ErrorCode(result.Error);
        return new OnboardingShortcutState(
            displayText,
            OnboardingShortcutStatus.Failed,
            ShortcutValidationService.GetRegistrationErrorMessage(code, shortcut));
    }
}

// =============================================================================
// Model catalog
// =============================================================================

/// <summary>
/// The curated on-device shortlist plus download state for BOTH engines.
///
/// Windows is structurally better placed than macOS here: ModelDownloadService
/// raises ONE DownloadChanged stream carrying Progress / IsCompleted / IsSuccess
/// / Error for every engine, so the macOS bug where a Parakeet failure could only
/// ever be reported as a Whisper one cannot recur. The flow still funnels it
/// through a single SetupErrorMessage keyed on the selected model's engine.
/// </summary>
public sealed class LiveOnboardingModelCatalog : IOnboardingModelCatalog, IDisposable
{
    private readonly WhisperModelService _whisper;
    private readonly ParakeetModelService _parakeet;
    private readonly ModelDownloadService _downloads;

    private OnboardingDownloadErrors _errors = OnboardingDownloadErrors.None;
    private readonly Dictionary<string, double> _progress = new(StringComparer.Ordinal);
    private bool _disposed;

    public LiveOnboardingModelCatalog(
        WhisperModelService whisper,
        ParakeetModelService parakeet,
        ModelDownloadService downloads)
    {
        _whisper = whisper;
        _parakeet = parakeet;
        _downloads = downloads;
        _downloads.DownloadChanged += OnDownloadChanged;
    }

    public event EventHandler<OnboardingDownloadErrors>? DownloadErrorsChanged;

    public event EventHandler? DownloadActivity;

    /// <summary>
    /// The shortlist, in macOS's order. The ids are the WINDOWS ids, because
    /// OnboardingModelSelection.Id is written verbatim onto the Mode row: Parakeet
    /// is "parakeet-v2"/"parakeet-v3" here (macOS spells them
    /// "parakeet-tdt-0.6b-v2"/"-v3") and Whisper's turbo build is
    /// "large-v3-turbo" (macOS spells it "large-v3_turbo"). Sizes are read from
    /// the same catalogs the Model Library uses so the two screens agree.
    /// </summary>
    public IReadOnlyList<OnboardingModelSelection> Models => CuratedModels;

    internal static readonly IReadOnlyList<OnboardingModelSelection> CuratedModels = BuildCurated();

    private static IReadOnlyList<OnboardingModelSelection> BuildCurated()
    {
        string ParakeetSize(string id) =>
            ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == id)?.Size ?? "";

        string WhisperSize(string type) =>
            WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == type)?.Size ?? "";

        return new[]
        {
            new OnboardingModelSelection(
                "parakeet-v2", OnboardingModelKind.Parakeet,
                "Parakeet V2", "onboarding.model.parakeetV2.subtitle",
                ParakeetSize("parakeet-v2"), 5, 3, IsRecommended: true),
            new OnboardingModelSelection(
                "parakeet-v3", OnboardingModelKind.Parakeet,
                "Parakeet V3", "onboarding.model.parakeetV3.subtitle",
                ParakeetSize("parakeet-v3"), 5, 3, IsRecommended: false),
            new OnboardingModelSelection(
                "base", OnboardingModelKind.Whisper,
                "Whisper Base", "onboarding.model.whisperBase.subtitle",
                WhisperSize("base"), 5, 1, IsRecommended: false),
            new OnboardingModelSelection(
                "large-v3-turbo", OnboardingModelKind.Whisper,
                "Whisper Large v3 Turbo", "onboarding.model.whisperTurbo.subtitle",
                WhisperSize("large-v3-turbo"), 4, 3, IsRecommended: false),
        };
    }

    /// <summary>
    /// ModelDownloadService keys everything on the Model Library's row id
    /// ("whisper-base", "parakeet-parakeet-v2"), not on the raw model id the Mode
    /// row stores. One place converts.
    /// </summary>
    internal static string LibraryId(OnboardingModelSelection model) => model.Kind switch
    {
        OnboardingModelKind.Whisper => $"whisper-{model.Id}",
        OnboardingModelKind.Parakeet => $"parakeet-{model.Id}",
        _ => model.Id
    };

    public bool IsInstalled(OnboardingModelSelection model)
    {
        try
        {
            return model.Kind switch
            {
                OnboardingModelKind.Whisper => WhisperInfo(model.Id) is { } w && _whisper.IsModelDownloaded(w),
                OnboardingModelKind.Parakeet => ParakeetInfo(model.Id) is { } p && _parakeet.IsModelDownloaded(p),
                _ => false
            };
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingModelCatalog: install check failed for {model.Id}: {ex.Message}");
            return false;
        }
    }

    public bool IsDownloading(OnboardingModelSelection model) => _downloads.IsDownloading(LibraryId(model));

    public double Progress(OnboardingModelSelection model) =>
        _progress.TryGetValue(LibraryId(model), out var value) ? value : 0;

    public void StartDownload(OnboardingModelSelection model)
    {
        var row = BuildLibraryRow(model);
        if (row is null)
        {
            LoggingService.Warn($"LiveOnboardingModelCatalog: no catalog entry for '{model.Id}'");
            PublishError(model.Kind, Loc.S("onboarding.setup.model.notFound"));
            return;
        }

        // A new attempt supersedes the last failure for this engine, so the setup
        // step does not keep showing an error while a retry is running.
        PublishError(model.Kind, null);
        _downloads.TryStartDownload(row);
    }

    /// <summary>
    /// The minimum LibraryModel ModelDownloadService needs: it only reads Id,
    /// DisplayName and Payload. Building it here rather than running the whole
    /// ModelLibraryManager.Rebuild() keeps onboarding off the cloud-catalog and
    /// health-probe paths it has no use for.
    /// </summary>
    private LibraryModel? BuildLibraryRow(OnboardingModelSelection model)
    {
        object? payload = model.Kind switch
        {
            OnboardingModelKind.Whisper => WhisperInfo(model.Id),
            OnboardingModelKind.Parakeet => ParakeetInfo(model.Id),
            _ => null
        };

        if (payload is null) return null;

        return new LibraryModel
        {
            Id = LibraryId(model),
            DisplayName = model.DisplayName,
            ProviderName = model.Kind == OnboardingModelKind.Whisper ? "Whisper" : "NVIDIA",
            ProviderAssetName = model.Kind == OnboardingModelKind.Whisper ? "providerLocalWhisper" : "providerLocalParakeet",
            Kind = LibraryModelKind.Voice,
            LocationKind = LibraryModelLocationKind.Offline,
            StatusKind = LibraryModelStatusKind.Downloadable,
            Source = model.Kind == OnboardingModelKind.Whisper ? LibraryModelSource.Whisper : LibraryModelSource.Parakeet,
            SizeDescription = model.Size,
            Speed = model.Speed,
            Accuracy = model.Accuracy,
            SupportsCustomVocabulary = false,
            AvailableViaHyperWhisperCloud = false,
            Payload = payload
        };
    }

    private static WhisperModelInfo? WhisperInfo(string id) =>
        WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == id);

    private static ParakeetModelInfo? ParakeetInfo(string id) =>
        ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == id);

    private void OnDownloadChanged(object? sender, ModelDownloadChangedEventArgs e)
    {
        var model = CuratedModels.FirstOrDefault(m => LibraryId(m) == e.ModelId);
        if (model is null) return;

        _progress[e.ModelId] = e.Progress;

        if (e.IsCompleted)
        {
            _progress.Remove(e.ModelId);
            // Carry the engine the FAILURE belongs to, so a Whisper failure is
            // never attributed to a selected Parakeet model.
            PublishError(model.Kind, e.IsSuccess ? null : (e.Error ?? Loc.S("onboarding.setup.model.downloadFailed")));
        }

        DownloadActivity?.Invoke(this, EventArgs.Empty);
    }

    private void PublishError(OnboardingModelKind kind, string? message)
    {
        var next = kind == OnboardingModelKind.Whisper
            ? _errors with { Whisper = message }
            : _errors with { Parakeet = message };

        if (next == _errors) return;

        _errors = next;
        DownloadErrorsChanged?.Invoke(this, _errors);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloads.DownloadChanged -= OnDownloadChanged;
    }
}

// =============================================================================
// License
// =============================================================================

/// <summary>
/// HyperWhisper Cloud. Probe is read-only; Activate is the single explicit action
/// that writes account state. Entitlement itself stays server side and there is
/// no local shortcut here - there must never be one.
/// </summary>
public sealed class LiveOnboardingLicenseGateway : IOnboardingLicenseGateway
{
    private readonly LicenseManager _manager;

    public LiveOnboardingLicenseGateway(LicenseManager manager)
    {
        _manager = manager;
    }

    public bool IsActive => _manager.IsLicensed;

    public async Task<OnboardingLicenseOutcome> ProbeAsync(string key, CancellationToken cancellationToken)
    {
        var result = await _manager.ProbeLicenseAsync(key, cancellationToken);
        return ToOutcome(result);
    }

    public async Task<OnboardingLicenseOutcome> ActivateAsync(string key, CancellationToken cancellationToken)
    {
        var result = await _manager.ActivateLicenseAsync(key, cancellationToken);
        return ToOutcome(result);
    }

    private static OnboardingLicenseOutcome ToOutcome(LicenseValidationResult result) =>
        new(result.IsValid, result.IsValid ? null : (result.ErrorMessage ?? Loc.S("app.unknown.error")));
}

// =============================================================================
// Credits
// =============================================================================

/// <summary>
/// The cloud credit balance, flattened to what the two cloud panels show.
///
/// Display only: it never gates Continue, and a fetch failure is swallowed into
/// "credits unknown" rather than becoming a setup error. macOS gets this by
/// injecting HyperWhisperCloudManager into the sheet as an @EnvironmentObject,
/// which the singleton rule at the top of this file forbids here.
/// </summary>
public sealed class LiveOnboardingCreditsGateway : IOnboardingCreditsGateway, IDisposable
{
    private readonly HyperWhisperCloudManager _manager;
    private bool _disposed;

    public LiveOnboardingCreditsGateway(HyperWhisperCloudManager manager)
    {
        _manager = manager;
        _manager.CreditsUpdated += OnCreditsUpdated;
        _manager.PropertyChanged += OnManagerPropertyChanged;
    }

    public event EventHandler? CreditsChanged;

    public OnboardingCloudCredits? Credits => Flatten(_manager.Credits);

    public bool IsFetching => _manager.IsFetchingCredits;

    internal static OnboardingCloudCredits? Flatten(HyperWhisperCloudCredits? credits) =>
        credits is null
            ? null
            : new OnboardingCloudCredits(credits.CreditsRemaining, credits.MinutesRemaining, credits.FormattedBalance);

    /// <summary>
    /// FetchCreditsAsync self-guards against re-entry, so calling this on entry to
    /// both cloud steps is safe. A failure never propagates: the panels render an
    /// ellipsis and the flow moves on.
    /// </summary>
    public async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            await _manager.FetchCreditsAsync(force, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingCreditsGateway: credits unavailable: {ex.Message}");
        }
        finally
        {
            CreditsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCreditsUpdated(object? sender, EventArgs e) => CreditsChanged?.Invoke(this, EventArgs.Empty);

    private void OnManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The spinner state has to reach the UI too, not just the figure.
        if (e.PropertyName is nameof(HyperWhisperCloudManager.IsFetchingCredits)
            or nameof(HyperWhisperCloudManager.Credits))
        {
            CreditsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The manager is a process-lifetime singleton, so a handler left attached
    /// outlives the onboarding window.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.CreditsUpdated -= OnCreditsUpdated;
        _manager.PropertyChanged -= OnManagerPropertyChanged;
    }
}

// =============================================================================
// Provider keys
// =============================================================================

/// <summary>
/// Bring-your-own-key providers.
/// </summary>
public sealed class LiveOnboardingProviderKeyGateway : IOnboardingProviderKeyGateway
{
    private readonly ApiKeyService _apiKeys;
    private readonly CloudProviderHealthService _health;
    private string? _validationError;

    public LiveOnboardingProviderKeyGateway(ApiKeyService apiKeys, CloudProviderHealthService health)
    {
        _apiKeys = apiKeys;
        _health = health;
    }

    /// <inheritdoc />
    public IReadOnlyList<CloudTranscriptionProvider> Providers => ByokProviders;

    /// <summary>
    /// The reason the LAST credential write failed, or null. Deliberately local to
    /// this gateway rather than read from an app-global validation field: an
    /// unrelated failure from earlier in the session must never appear as this
    /// step's error.
    /// </summary>
    public string? ValidationError => _validationError;

    /// <summary>
    /// Grades the key the user just typed, WITHOUT saving it first, so a failed
    /// credential write cannot be masked by a health check that passed against a
    /// temporary in-memory credential.
    /// </summary>
    public Task<ProviderHealth> ProbeAsync(
        CloudTranscriptionProvider provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        return _health.ProbeAsync(provider, apiKey, cancellationToken);
    }

    /// <summary>
    /// Writes the key to Windows Credential Manager. An empty string DELETES the
    /// entry, mirroring macOS, which is what makes "" round-trip as "there was no
    /// key here" on rollback.
    /// </summary>
    public bool Persist(string key, CloudTranscriptionProvider provider)
    {
        _validationError = null;

        try
        {
            // SetApiKey treats null/empty as a delete, and it already registers the
            // change with the health service on the way through - so unlike macOS
            // there is no second registerAPIKeyChange call here.
            var value = string.IsNullOrEmpty(key) ? null : key;

            var postProcessing = provider.GetApiKeyProvider();
            if (postProcessing != PostProcessingProvider.None)
            {
                _apiKeys.SetApiKey(postProcessing, value);
            }
            else if (TranscriptionKeyType(provider) is { } type)
            {
                _apiKeys.SetApiKey(type, value);
            }
            else
            {
                _validationError = Loc.S("onboarding.setup.provider.saveFailed");
                return false;
            }

            // Read it back. A vault write that silently did nothing must not be
            // reported as a pass; the seam's contract is that a healthy probe alone
            // is never enough.
            var stored = CurrentKey(provider);
            var persisted = string.IsNullOrEmpty(key)
                ? string.IsNullOrEmpty(stored)
                : string.Equals(stored, key, StringComparison.Ordinal);

            if (!persisted)
            {
                _validationError = Loc.S("onboarding.setup.provider.saveFailed");
            }

            return persisted;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LiveOnboardingProviderKeyGateway: credential write failed: {ex.Message}", ex);
            _validationError = ex.Message;
            return false;
        }
    }

    public bool HasKey(CloudTranscriptionProvider provider) => !string.IsNullOrEmpty(CurrentKey(provider));

    /// <summary>Whatever is stored right now, or "" when nothing is.</summary>
    public string CurrentKey(CloudTranscriptionProvider provider)
    {
        try
        {
            var postProcessing = provider.GetApiKeyProvider();
            if (postProcessing != PostProcessingProvider.None)
            {
                return _apiKeys.GetApiKey(postProcessing) ?? string.Empty;
            }

            return TranscriptionKeyType(provider) is { } type
                ? _apiKeys.GetApiKey(type) ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingProviderKeyGateway: credential read failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// The providers offered on the "Your API key" branch, in a stable order.
    ///
    /// Every BYOK vendor, minus the three that need no key: HyperWhisper Cloud
    /// (its own step), and Microsoft Azure Speech / Google Speech, whose health
    /// probe short-circuits to Healthy WITHOUT a key. Offering those two would
    /// open the setup gate on a pass that proves nothing.
    /// </summary>
    public static IReadOnlyList<CloudTranscriptionProvider> ByokProviders { get; } = new[]
    {
        CloudTranscriptionProvider.OpenAI,
        CloudTranscriptionProvider.Groq,
        CloudTranscriptionProvider.Deepgram,
        CloudTranscriptionProvider.AssemblyAI,
        CloudTranscriptionProvider.ElevenLabs,
        CloudTranscriptionProvider.Mistral,
        CloudTranscriptionProvider.Soniox,
        CloudTranscriptionProvider.Gemini,
        CloudTranscriptionProvider.GeminiTranscribe,
        CloudTranscriptionProvider.Grok,
    };

    /// <summary>
    /// The key slot for a provider that has no shared post-processing provider.
    /// Mirrors the mapping CloudProviderHealthService.GetTranscriptionApiKey uses,
    /// in the write direction.
    /// </summary>
    internal static TranscriptionApiKeyType? TranscriptionKeyType(CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.Deepgram => TranscriptionApiKeyType.Deepgram,
        CloudTranscriptionProvider.AssemblyAI => TranscriptionApiKeyType.AssemblyAI,
        CloudTranscriptionProvider.ElevenLabs => TranscriptionApiKeyType.ElevenLabs,
        CloudTranscriptionProvider.Mistral => TranscriptionApiKeyType.Mistral,
        CloudTranscriptionProvider.Soniox => TranscriptionApiKeyType.Soniox,
        CloudTranscriptionProvider.GeminiTranscribe => TranscriptionApiKeyType.GeminiTranscribe,
        _ => null
    };
}

// =============================================================================
// Commit
// =============================================================================

/// <summary>
/// The one and only path from staged configuration to production state.
/// </summary>
public sealed class LiveOnboardingSourceCommitter : IOnboardingSourceCommitter
{
    private readonly ModeService _modes;
    private readonly SettingsService _settings;
    private readonly Action _returnToHome;

    public LiveOnboardingSourceCommitter(ModeService modes, SettingsService settings, Action returnToHome)
    {
        _modes = modes;
        _settings = settings;
        _returnToHome = returnToHome;
    }

    /// <summary>
    /// The flagged default row, or null. Deliberately NOT ModeService.GetDefaultMode(),
    /// which falls back to the lowest-SortOrder Mode when nothing is flagged: that
    /// fallback would make onboarding silently overwrite one of the user's own
    /// Modes. macOS's findDefaultMode is strictly isDefault == YES, and so is this.
    /// </summary>
    private Mode? FindDefaultMode() => _modes.GetAllModes().FirstOrDefault(m => m.IsDefault);

    public IOnboardingRestorePoint CaptureRestorePoint()
    {
        var existing = FindDefaultMode();
        return new WindowsOnboardingRestorePoint
        {
            ModeExisted = existing is not null,
            ModeId = existing?.Id ?? ModeDefaults.DefaultModeId,
            Snapshot = existing is null ? null : WindowsOnboardingRestorePoint.Clone(existing),
            PreviousSelectedModeId = _settings.SelectedModeId
        };
    }

    /// <summary>
    /// Reconfigure the EXISTING default Mode in place. Only the source fields move;
    /// everything else on the row is left exactly as it was, which is what makes
    /// this reversible from the snapshot above.
    ///
    /// CloudTranscriptionModel is deliberately cleared rather than set, so it
    /// re-derives for the new provider and tier.
    /// </summary>
    public void Apply(OnboardingStagedSource staged)
    {
        var existing = FindDefaultMode();
        var mode = existing is null ? NewDefaultMode() : WindowsOnboardingRestorePoint.Clone(existing);

        ApplyStagedFields(mode, staged);

        // SaveMode creates when the row is absent and updates when it is not, and
        // it writes every column either way.
        _modes.SaveMode(mode);

        // Writing the source onto Default is not enough on its own: a returning
        // user's SelectedModeId still points at their own Mode, so the next
        // recording would keep using that Mode's source.
        _modes.SetSelectedMode(mode.Id);
    }

    /// <summary>
    /// The staged source -> Mode mapping.
    ///
    /// The plan's table is a macOS shape and is incomplete for Windows: a local
    /// Mode here carries LocalEngine ("whisper"/"parakeet") and, for Parakeet,
    /// LocalParakeetModel. Writing only Model would leave a Parakeet pick running
    /// on Whisper. The engine is derived from the staged model id, which is why
    /// OnboardingStagedSource does not need a new field.
    /// </summary>
    internal static void ApplyStagedFields(Mode mode, OnboardingStagedSource staged)
    {
        mode.PostProcessingMode = staged.PostProcessingMode;
        mode.CloudProvider = staged.CloudProvider;
        mode.CloudTranscriptionModel = null;
        mode.CloudAccuracyTier = staged.CloudAccuracyTier ?? mode.CloudAccuracyTier;

        if (staged.Source == OnboardingSourceKind.OnDevice)
        {
            mode.ProviderType = "local";

            var isParakeet = ParakeetModelInfo.AllModels.Any(m => m.Id == staged.Model);
            mode.LocalEngine = isParakeet ? "parakeet" : "whisper";

            if (isParakeet)
            {
                mode.LocalParakeetModel = staged.Model;
            }
            else
            {
                mode.Model = staged.Model;
                mode.ModelType = staged.Model;
            }
        }
        else
        {
            mode.ProviderType = "cloud";
            mode.Model = staged.Model;
            mode.ModelType = staged.Model;
        }
    }

    private static Mode NewDefaultMode() => new()
    {
        Id = ModeDefaults.DefaultModeId,
        Name = "Hyper",
        Preset = "hyper",
        IsDefault = true,
        IsSystemProvided = true,
        SortOrder = 0,
        Language = "auto"
    };

    public void Restore(IOnboardingRestorePoint point)
    {
        if (point is not WindowsOnboardingRestorePoint restore) return;

        try
        {
            if (restore.ModeExisted && restore.Snapshot is not null)
            {
                _modes.SaveMode(WindowsOnboardingRestorePoint.Clone(restore.Snapshot));
            }
            else
            {
                // Nothing was flagged default before, so remove what Apply created
                // rather than leaving a synthetic default behind. DeleteMode
                // refuses to remove the last remaining Mode, which is the right
                // answer: an empty Modes table is worse than a stray default.
                _modes.DeleteMode(restore.ModeId);
            }

            // DeleteMode may have re-pointed the selection; put the user's back
            // afterwards, including a null that means "never chose one".
            _settings.SelectedModeId = restore.PreviousSelectedModeId;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LiveOnboardingSourceCommitter: restore failed: {ex.Message}", ex);
        }
    }

    public void MarkOnboardingCompleted() => _settings.OnboardingPending = false;

    public void ReturnToHome()
    {
        try
        {
            _returnToHome();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingSourceCommitter: could not return to Home: {ex.Message}");
        }
    }
}

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
        Action? openShortcutSettings = null,
        Action? returnToHome = null)
    {
        var mainViewModel = WpfApplication.Current?.MainWindow?.DataContext as MainViewModel;

        var permissions = new LiveOnboardingPermissions(
            openShortcutSettings ?? (() => NavigateMainShell(MainViewModel.NavigationPage.Settings)));

        var catalog = new LiveOnboardingModelCatalog(
            new WhisperModelService(),
            new ParakeetModelService(),
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
            // The running app's open device lives on MainViewModel, not in
            // settings; see the comment on those two delegates.
            readOpenDevice: mainViewModel is null ? null : () => mainViewModel.SelectedAudioDevice?.Name,
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
    /// Points the running app at a device by name. A null name, or a name that is
    /// not currently connected, leaves the app on the system default rather than
    /// on a device it cannot open.
    /// </summary>
    private static void ApplyOpenDevice(MainViewModel viewModel, string? name)
    {
        viewModel.SelectedAudioDevice = string.IsNullOrEmpty(name)
            ? null
            : viewModel.AudioDevices.FirstOrDefault(d => d.Name == name);
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
