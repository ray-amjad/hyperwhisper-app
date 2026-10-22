using System.Reflection;
using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using SharedApi = HyperWhisper.LocalApi;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// The Windows head's <see cref="SharedApi.ILocalApiBackend"/>: the app services
/// the old <c>Endpoints/</c> route handlers called, behind the shared server
/// (<see cref="SharedApi.PortableLocalApi"/>, issue #356 item 6).
/// </summary>
/// <remarks>
/// <para>
/// The shared server owns the HTTP surface: the routes, the request parsing
/// and validation, the origin guard, the bearer check, the body limits, the
/// <c>file</c> path policy and the response envelopes. This class owns only
/// what the services answer. Where the two used to disagree the shared answer
/// won, and each difference is listed in the implementation notes for #356.
/// </para>
/// <para>
/// A business failure leaves here as a <see cref="SharedApi.LocalApiFailureException"/>
/// built by <see cref="WindowsLocalApiFailure"/>, which the shared middleware
/// writes verbatim. Every other exception is converted before it leaves, as the
/// old handlers did, because the shared middleware answers an unmapped one with
/// a bare 500.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsLocalApiBackend : SharedApi.ILocalApiBackend
{
    private readonly LocalApiServer _server;

    internal WindowsLocalApiBackend(LocalApiServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    internal static IReadOnlyList<CloudTranscriptionProvider> TranscriptionProviders { get; } =
        Enum.GetValues<CloudTranscriptionProvider>()
            .Where(provider => provider != CloudTranscriptionProvider.None)
            .ToArray();

    // =========================================================================
    // GET /health
    // =========================================================================

    public ValueTask<SharedApi.HealthSnapshot> GetHealthAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        var cloudAccountKeyPresent = CloudAccountKeyPresent();
        return ValueTask.FromResult(new SharedApi.HealthSnapshot(
            version,
            BuildTranscriptionProviders(_server.ApiKeys, _server.CloudHealth, cloudAccountKeyPresent),
            BuildPostProcessingProviders(_server.ApiKeys, _server.CloudHealth, cloudAccountKeyPresent),
            BuildLocalModels(_server)));
    }

    /// <summary>
    /// Whether HyperWhisper Cloud holds an account key, read through the SAME
    /// predicate `/transcribe` and Cloud post-processing refuse on with
    /// MISSING_API_KEY: <see cref="LicenseManager.GetTranscriptionIdentifier"/>'s
    /// IsLicensed (HyperWhisperCloudService.Transcription,
    /// HyperWhisperRoutedTranscriptionClient, HyperWhisperCloudService.PostProcessing).
    /// The HW-Cloud-routed rows used to be hard-coded true, so a profile with no
    /// account key read key_present:true while every Cloud job was refused
    /// (issue #650). Presence only: the identifier is discarded, never logged.
    /// </summary>
    internal static bool CloudAccountKeyPresent() =>
        LicenseManager.Instance.GetTranscriptionIdentifier().IsLicensed;

    /// <summary>
    /// The HW-Cloud-routed transcription providers: they hold no BYOK key and
    /// run only with the HyperWhisper Cloud account key
    /// (<see cref="CloudAccountKeyPresent"/>). `/health` key_present and
    /// `/models` installed both read this set.
    /// </summary>
    internal static bool UsesCloudAccountKey(CloudTranscriptionProvider provider) => provider is
        CloudTranscriptionProvider.HyperWhisperCloud
        or CloudTranscriptionProvider.MicrosoftAzureSpeech
        or CloudTranscriptionProvider.GoogleSpeech;

    internal static List<SharedApi.ProviderStatus> BuildTranscriptionProviders(
        ApiKeyService? apiKeys, CloudProviderHealthService? health, bool cloudAccountKeyPresent)
    {
        var list = new List<SharedApi.ProviderStatus>();
        if (apiKeys == null) return list;

        foreach (var provider in TranscriptionProviders)
        {
            var keyPresent = HasKeyForTranscriptionProvider(apiKeys, provider, cloudAccountKeyPresent);
            var status = health?.GetHealthStatus(provider) ?? ProviderHealth.Unknown;
            list.Add(new SharedApi.ProviderStatus(
                provider.GetIdentifier(),
                keyPresent,
                IsReachable(provider, keyPresent, status),
                StatusString(provider, keyPresent, status)));
        }
        return list;
    }

    private static bool HasKeyForTranscriptionProvider(
        ApiKeyService apiKeys, CloudTranscriptionProvider provider, bool cloudAccountKeyPresent)
    {
        // HW-Cloud-routed providers hold no BYOK key: /transcribe refuses them
        // with MISSING_API_KEY when no HyperWhisper Cloud account key is stored,
        // so report that (CloudAccountKeyPresent). Every other provider answers
        // from the shared key map (issue #1220); None has no slot, so false.
        return UsesCloudAccountKey(provider)
            ? cloudAccountKeyPresent
            : apiKeys.HasApiKey(provider);
    }

    internal static List<SharedApi.ProviderStatus> BuildPostProcessingProviders(
        ApiKeyService? apiKeys, CloudProviderHealthService? health, bool cloudAccountKeyPresent)
    {
        var list = new List<SharedApi.ProviderStatus>();
        if (apiKeys == null) return list;

        foreach (PostProcessingProvider provider in Enum.GetValues<PostProcessingProvider>())
        {
            if (provider == PostProcessingProvider.None) continue;
            if (provider == PostProcessingProvider.LocalLlm) continue; // surfaced under local_models.local_llm

            var status = health?.GetStatus(provider) ?? ProviderHealth.Unknown;
            list.Add(new SharedApi.ProviderStatus(
                provider.ToStringValue(),
                // HyperWhisperCloud has no BYOK slot (HasApiKey is always false
                // for it); Cloud post-processing refuses on the account key.
                provider == PostProcessingProvider.HyperWhisperCloud
                    ? cloudAccountKeyPresent
                    : apiKeys.HasApiKey(provider),
                status == ProviderHealth.Healthy,
                StatusString(status)));
        }
        return list;
    }

    private static HealthLocalModels BuildLocalModels(LocalApiServer server)
    {
        var whisper = new List<HealthLocalModelEntry>();
        if (server.WhisperModels is { } whisperSvc)
        {
            foreach (var m in WhisperModelInfo.AllModels)
            {
                whisper.Add(new HealthLocalModelEntry
                {
                    Id = m.Type,
                    DisplayName = m.DisplayName,
                    Installed = whisperSvc.IsModelDownloaded(m)
                });
            }
        }

        var parakeet = new List<HealthLocalModelEntry>();
        var qwen3Asr = new List<HealthLocalModelEntry>();
        if (server.ParakeetModels is { } parakeetSvc)
        {
            foreach (var m in ParakeetModelInfo.AllModels)
            {
                var entry = new HealthLocalModelEntry
                {
                    Id = m.Id,
                    DisplayName = m.DisplayName,
                    Installed = parakeetSvc.IsModelDownloaded(m)
                };

                // Keep all sherpa-daemon models in the legacy parakeet bucket for
                // Windows client compatibility. Also fill the documented qwen3_asr
                // bucket for cross-platform consumers that look for Qwen directly.
                parakeet.Add(entry);
                if (m.Engine == ParakeetEngine.Qwen3)
                {
                    qwen3Asr.Add(entry);
                }
            }
        }

        var localLlm = new List<HealthLocalModelEntry>();
        if (server.LocalLlmModels is { } llmSvc)
        {
            foreach (var m in LocalLlmModelInfo.AllModels)
            {
                localLlm.Add(new HealthLocalModelEntry
                {
                    Id = m.Id,
                    DisplayName = m.DisplayName,
                    Installed = llmSvc.IsModelDownloaded(m)
                });
            }
        }

        return new HealthLocalModels
        {
            Whisper = whisper,
            Parakeet = parakeet,
            Qwen3Asr = qwen3Asr,
            AppleSpeech = new List<HealthLocalModelEntry>(),    // Apple Speech is macOS-only
            LocalLlm = localLlm
        };
    }

    private static string StatusString(ProviderHealth status) => status switch
    {
        ProviderHealth.Healthy => "healthy",
        ProviderHealth.Unauthorized => "unauthorized",
        ProviderHealth.Checking => "checking",
        ProviderHealth.Unknown => "unknown",
        _ => status.ToString().ToLowerInvariant()
    };

    internal static string StatusString(
        CloudTranscriptionProvider provider, bool keyPresent, ProviderHealth status) =>
        provider == CloudTranscriptionProvider.Meta && keyPresent
            ? "configured"
            : StatusString(status);

    internal static bool IsReachable(
        CloudTranscriptionProvider provider, bool keyPresent, ProviderHealth status) =>
        provider != CloudTranscriptionProvider.Meta
        && status == ProviderHealth.Healthy;

    // =========================================================================
    // GET /models
    // =========================================================================

    /// <summary>
    /// Every voice and text model the app knows about (cloud and local), with
    /// installed status. Backs the cross-platform <c>hyperwhisper.list_models</c>
    /// MCP tool.
    /// </summary>
    public ValueTask<IReadOnlyList<SharedApi.ModelEntry>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var library = _server.ModelLibrary
            ?? throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.EngineUnavailable,
                "Model library not yet initialized.");

        // Read once per request: the account key /transcribe refuses on.
        var cloudAccountKeyPresent = CloudAccountKeyPresent();
        IReadOnlyList<SharedApi.ModelEntry> models = library.Rebuild()
            .Select(row => ToEntry(row, cloudAccountKeyPresent))
            .ToList();
        return ValueTask.FromResult(models);
    }

    internal static SharedApi.ModelEntry ToEntry(LibraryModel row, bool cloudAccountKeyPresent) =>
        new(
            row.Id,
            row.Kind == LibraryModelKind.Voice ? "voice" : "text",
            ModelProviderId(row),
            row.DisplayName,
            // `installed` means "configured and selectable", not "proven to
            // work". A custom endpoint that has never been tested is Untested
            // rather than Enabled since #509; it is still a usable row, so it
            // keeps reporting installed and the wire contract does not move.
            // The HW-Cloud-routed rows are always Enabled in the library (no
            // BYOK key), but /transcribe and Cloud post-processing refuse them
            // with MISSING_API_KEY when no account key is stored, so they are
            // installed only with one, the same check as /health key_present
            // (issue #650).
            (row.StatusKind is LibraryModelStatusKind.Enabled or LibraryModelStatusKind.Untested)
                && (cloudAccountKeyPresent || !UsesCloudAccountKey(row)),
            ExtractSizeMb(row.SizeDescription));

    private static bool UsesCloudAccountKey(LibraryModel row) => row.Payload switch
    {
        CloudTranscriptionModel model => UsesCloudAccountKey(model.Provider),
        LanguageModelInfo model => model.Provider == PostProcessingProvider.HyperWhisperCloud,
        _ => false
    };

    private static string ModelProviderId(LibraryModel row)
        => row.LocationKind == LibraryModelLocationKind.Offline
            ? "local"
            : row.ProviderName;

    /// <summary>
    /// Library rows store size as a human string ("1.5 GB", "474 MB"). Parse
    /// it back into megabytes for `/models`. Returns null when no size is
    /// known (cloud models, custom endpoints).
    /// </summary>
    private static double? ExtractSizeMb(string? sizeDescription)
    {
        if (string.IsNullOrWhiteSpace(sizeDescription)) return null;
        var trimmed = sizeDescription.Trim();

        int i = 0;
        while (i < trimmed.Length && (char.IsDigit(trimmed[i]) || trimmed[i] == '.' || trimmed[i] == ',')) i++;
        if (i == 0) return null;

        var numericPart = trimmed[..i].Replace(",", ".", StringComparison.Ordinal);
        if (!double.TryParse(numericPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = trimmed[i..].TrimStart().ToUpperInvariant();
        return unit switch
        {
            var u when u.StartsWith("GB", StringComparison.Ordinal) => value * 1024.0,
            var u when u.StartsWith("MB", StringComparison.Ordinal) => value,
            var u when u.StartsWith("KB", StringComparison.Ordinal) => value / 1024.0,
            _ => value
        };
    }

    // =========================================================================
    // GET /recordings, /recordings/search, /recordings/{id}
    // =========================================================================

    /// <summary>
    /// A read-only projection over the Transcript history table. <c>q</c>,
    /// <c>since</c>, <c>until</c>, the count and <c>limit</c> run in SQL through
    /// <see cref="HistoryService.QueryPage"/> (issue #1123). There is
    /// intentionally no DELETE.
    /// </summary>
    public ValueTask<SharedApi.RecordingPage> GetRecordingsAsync(
        SharedApi.RecordingQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (page, total) = HistoryService.Instance.QueryPage(
            (query.Search ?? "").Trim(), query.Since, query.Until, query.Limit);
        return ValueTask.FromResult(new SharedApi.RecordingPage(page.Select(ToRecordingEntry).ToList(), total));
    }

    /// <summary>
    /// The recording, or null for an id that is not a GUID or names no row,
    /// which the shared server answers as <c>MODE_NOT_FOUND</c>. macOS reuses
    /// that code for a missing recording, and the closed set has no other.
    /// </summary>
    public ValueTask<SharedApi.RecordingEntry?> GetRecordingAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid)) return ValueTask.FromResult<SharedApi.RecordingEntry?>(null);
        var recording = HistoryService.Instance.GetTranscript(guid);
        return ValueTask.FromResult(recording is null ? null : ToRecordingEntry(recording));
    }

    internal static SharedApi.RecordingEntry ToRecordingEntry(Transcript t) => new(
        t.Id.ToString("D"),
        t.Text ?? "",
        DateTime.SpecifyKind(t.Date, DateTimeKind.Utc),
        t.Duration,
        t.Mode,
        t.Status.ToString().ToLowerInvariant(),
        t.PostProcessedText,
        t.TranscribedText,
        t.TranscriptionProvider,
        t.PostProcessingProvider,
        t.AudioFilePath);

    // =========================================================================
    // POST /recording/toggle, /recording/cancel
    // =========================================================================

    /// <summary>
    /// Not offered on Windows. These two routes are the shared server's and the
    /// Windows head never had them, so they refuse as a capability the platform
    /// lacks rather than reaching into the GUI's recording state machine.
    /// </summary>
    public ValueTask<SharedApi.RecordingState> ToggleRecordingAsync(CancellationToken cancellationToken) =>
        throw RecordingControlUnavailable();

    /// <inheritdoc cref="ToggleRecordingAsync"/>
    public ValueTask<SharedApi.RecordingState> CancelRecordingAsync(CancellationToken cancellationToken) =>
        throw RecordingControlUnavailable();

    internal const string RecordingControlUnavailableMessage =
        "Recording control is not available through the Local API on Windows.";

    private static SharedApi.LocalApiFailureException RecordingControlUnavailable() =>
        WindowsLocalApiFailure.Business(
            LocalApiErrorCode.EngineUnavailable,
            RecordingControlUnavailableMessage,
            "Start and stop recording with the HyperWhisper hotkey or tray menu.");
}
