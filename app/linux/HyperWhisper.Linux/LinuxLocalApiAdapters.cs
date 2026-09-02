using System.Diagnostics;
using HyperWhisper.Data.Entities;
using HyperWhisper.LocalApi;
using HyperWhisper.ModelManagement;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.ModelReadiness;
using System.Security.Cryptography;

namespace HyperWhisper.Linux;

internal sealed class LinuxLocalApiCapabilityCatalog(
    PortableModelManager models,
    IRecordedAudioTranscriber transcriber,
    ICredentialStore credentials,
    IDeviceIdentityProvider deviceIdentity,
    PortableSettingsService settings) : ILocalApiCapabilityCatalog
{
    private IReadOnlyList<ModelCapability> Capabilities => UnifiedModelCatalog.LoadBundled(
        (settings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? [])
        .Where(endpoint => Uri.TryCreate(endpoint.EndpointUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http")
        .Select(endpoint => new CustomEndpointDefinition(
            endpoint.Id, endpoint.Name, new Uri(endpoint.EndpointUrl), endpoint.ModelName,
            $"CustomEndpoint_{endpoint.Id:D}")));

    public IReadOnlyList<ModelEntry> Models => Capabilities
        .GroupBy(capability => $"{capability.ProviderId}\n{capability.ModelId}\n{capability.Workload}",
            StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(capability =>
            capability.Key.StartsWith("cloud/pp-byok/", StringComparison.Ordinal)).First())
        .Select(capability => new ModelEntry(
            capability.Key,
            capability.Workload == ModelWorkload.Text ? "text" : "voice",
            capability.Deployment == ModelDeployment.Local ? "local" : capability.ProviderId,
            capability.DisplayName,
            IsEnabled(capability),
            capability.ApproximateSizeBytes is { } size ? size / 1_000_000d : null)).ToArray();

    public IReadOnlyList<ProviderStatus> TranscriptionProviders => BuildProviders(
        Capabilities.Where(capability => capability.Workload == ModelWorkload.Voice),
        includeLocalRuntime: true);

    public IReadOnlyList<ProviderStatus> PostProcessingProviders
    {
        get
        {
            return BuildProviders(
                Capabilities.Where(capability => capability.Workload == ModelWorkload.Text),
                includeLocalRuntime: true);
        }
    }

    public object LocalModels => new
    {
        whisper = PortableModelCatalog.Whisper.Select(Status).ToArray(),
        parakeet = PortableModelCatalog.Parakeet.Select(Status).ToArray(),
        nemotron = PortableModelCatalog.Parakeet.Where(model => model.Id.StartsWith("nemotron", StringComparison.Ordinal)).Select(Status).ToArray(),
        qwen3_asr = PortableModelCatalog.Parakeet.Where(model => model.Id.StartsWith("qwen3", StringComparison.Ordinal)).Select(Status).ToArray(),
        apple_speech = Array.Empty<object>(),
        local_llm = PortableModelCatalog.LocalLlm.Select(Status).ToArray(),
    };

    private object Status(ManagedModel model) => new { id = model.Id, displayName = model.DisplayName, installed = models.IsInstalled(model) };

    private bool HasCredential(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return false;
        var result = credentials.Read("HyperWhisper", account);
        try { return result.IsSuccess && result.Value is { Length: > 0 }; }
        finally { if (result.Value is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
    }

    private IReadOnlyList<ProviderStatus> BuildProviders(
        IEnumerable<ModelCapability> source,
        bool includeLocalRuntime)
    {
        var result = new List<ProviderStatus>();
        foreach (var group in source.GroupBy(
            capability => capability.ProviderId,
            StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToArray();
            if (rows.All(row => row.Deployment == ModelDeployment.Local))
            {
                var available = group.Key.Equals("localLLM", StringComparison.OrdinalIgnoreCase)
                    ? PortableModelCatalog.LocalLlm.Any(models.IsInstalled)
                    : includeLocalRuntime && transcriber.Capability.IsAvailable;
                result.Add(new(group.Key, false, available, available ? "ready" : "model_required"));
                continue;
            }

            var keyPresent = rows.Any(row => !row.RequiresCredential)
                || rows.Any(row => HasCredential(row.CredentialAccount))
                || group.Key.Equals("hyperwhisper", StringComparison.OrdinalIgnoreCase)
                    && deviceIdentity.GetDeviceIdentity().IsSuccess;
            var snapshots = rows.Select(row => LinuxProviderReadinessSnapshot.Get(row.ProviderId, row.Surface))
                .Where(value => value is not null).Cast<ProviderHealthResponse>().ToArray();
            var outcome = snapshots.Select(value => value.Outcome).FirstOrDefault(value =>
                value is ProviderHealthOutcome.Healthy or ProviderHealthOutcome.Unauthorized
                    or ProviderHealthOutcome.RateLimited or ProviderHealthOutcome.Malformed
                    or ProviderHealthOutcome.Unreachable or ProviderHealthOutcome.Unsupported);
            result.Add(new(group.Key, keyPresent, outcome == ProviderHealthOutcome.Healthy,
                !keyPresent ? "credential_required" : Status(outcome, snapshots.Length > 0)));
        }
        return result;
    }

    private static string Status(ProviderHealthOutcome outcome, bool checkedProvider) => !checkedProvider ? "unknown" : outcome switch
    {
        ProviderHealthOutcome.Healthy => "healthy",
        ProviderHealthOutcome.Unauthorized => "unauthorized",
        ProviderHealthOutcome.RateLimited => "rate_limited",
        ProviderHealthOutcome.Malformed => "malformed",
        ProviderHealthOutcome.Unreachable => "unreachable",
        ProviderHealthOutcome.Unsupported => "unsupported",
        _ => "unknown",
    };

    private static bool TryManagedModel(string id, out ManagedModel model)
    {
        model = PortableModelCatalog.All.FirstOrDefault(candidate => candidate.Id == id)!;
        return model is not null;
    }

    private bool IsEnabled(ModelCapability capability)
    {
        if (capability.Deployment == ModelDeployment.Local)
            return TryManagedModel(capability.ModelId, out var model) && models.IsInstalled(model);
        var configured = !capability.RequiresCredential
            || HasCredential(capability.CredentialAccount)
            || capability.ProviderId.Equals("hyperwhisper", StringComparison.OrdinalIgnoreCase)
                && deviceIdentity.GetDeviceIdentity().IsSuccess;
        if (!configured) return false;
        return LinuxProviderReadinessSnapshot.Get(capability.ProviderId, capability.Surface)?.Outcome
            is not (ProviderHealthOutcome.Unauthorized or ProviderHealthOutcome.Unreachable
                or ProviderHealthOutcome.Malformed);
    }
}

internal sealed class LinuxLocalApiPostProcessor(
    ITranscriptionPostProcessor processor,
    ModeRepository modes) : ILocalApiPostProcessor
{
    public async ValueTask<PostProcessResult> ProcessAsync(PostProcessRequest request, CancellationToken cancellationToken)
    {
        var mode = await BuildWorkingModeAsync(request, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var result = await processor.ProcessAsync(
            request.Text, mode, request.ApplicationContext?.ToSnapshot(), cancellationToken);
        if (!result.WasApplied || string.IsNullOrWhiteSpace(result.Provider))
            throw new InvalidOperationException("Local post-processing is unavailable for this request.");
        return new(
            result.Text,
            result.Provider,
            mode.LanguageModel ?? mode.LocalPostProcessingModel ?? string.Empty,
            mode.Preset,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task<Mode> BuildWorkingModeAsync(
        PostProcessRequest request,
        CancellationToken cancellationToken)
    {
        var hasOverride = !string.IsNullOrWhiteSpace(request.Preset)
            || !string.IsNullOrWhiteSpace(request.Prompt)
            || !string.IsNullOrWhiteSpace(request.Provider)
            || !string.IsNullOrWhiteSpace(request.Model);
        Mode mode;
        if (!string.IsNullOrWhiteSpace(request.ModeId))
        {
            if (!Guid.TryParse(request.ModeId.Trim(), out var id))
                throw new ArgumentException("The requested mode ID is invalid.", nameof(request));
            var stored = (await modes.ListAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException("The requested mode does not exist.", nameof(request));
            if (!hasOverride && stored.PostProcessingMode == 0)
                throw new ArgumentException("The requested mode has post-processing disabled.", nameof(request));
            mode = CloneMode(stored);
        }
        else
        {
            mode = NewDefaultMode();
        }

        if (!string.IsNullOrWhiteSpace(request.Preset)) mode.Preset = request.Preset.Trim();
        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            mode.Preset = "custom";
            mode.CustomInstructions = request.Prompt.Trim();
        }
        if (!string.IsNullOrWhiteSpace(request.Provider))
        {
            var provider = NormalizeProvider(request.Provider);
            mode.PostProcessingProvider = provider;
            mode.PostProcessingMode = provider == "local_llm" ? 2 : 1;
        }
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            mode.LanguageModel = request.Model.Trim();
            if (mode.PostProcessingMode == 2) mode.LocalPostProcessingModel = mode.LanguageModel;
        }
        if (mode.PostProcessingMode == 0) mode.PostProcessingMode = 1;
        return mode;
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "localllm" or "local_llm" => "local_llm",
        "hyperwhisper" or "hyperwhisper_cloud" => "hyperwhispercloud",
        var value => value,
    };

    private static Mode NewDefaultMode() => new()
    {
        Id = Guid.NewGuid(),
        Name = "__local_api_pp_transient__",
        Preset = "hyper",
        Language = "en",
        Model = "base",
        Punctuation = true,
        Capitalization = true,
        PostProcessingMode = 1,
        PostProcessingProvider = "hyperwhispercloud",
        ProviderType = "cloud",
        CloudAccuracyTier = "elevenLabsScribeV2",
        CloudPostProcessingModel = "anthropic:claude-haiku-4-5",
        SortOrder = int.MaxValue,
        CreatedDate = DateTime.UtcNow,
        ModifiedDate = DateTime.UtcNow,
    };

    private static Mode CloneMode(Mode source) => new()
    {
        Id = Guid.NewGuid(), Name = "__local_api_pp_transient__", Preset = source.Preset,
        Language = source.Language, Model = source.Model, ModelType = source.ModelType,
        Punctuation = source.Punctuation, Capitalization = source.Capitalization,
        ProfanityFilter = source.ProfanityFilter, CustomInstructions = source.CustomInstructions,
        UserSystemPrompt = source.UserSystemPrompt, LanguageModel = source.LanguageModel,
        CloudProvider = source.CloudProvider, CloudTranscriptionModel = source.CloudTranscriptionModel,
        ProviderType = source.ProviderType, PostProcessingMode = source.PostProcessingMode,
        PostProcessingProvider = source.PostProcessingProvider, EnglishSpelling = source.EnglishSpelling,
        CloudAccuracyTier = source.CloudAccuracyTier, RemoveTrailingPeriod = source.RemoveTrailingPeriod,
        EnableScreenOCR = source.EnableScreenOCR, GeminiCustomPrompt = source.GeminiCustomPrompt,
        CloudPostProcessingModel = source.CloudPostProcessingModel, LocalEngine = source.LocalEngine,
        LocalParakeetModel = source.LocalParakeetModel,
        LocalPostProcessingModel = source.LocalPostProcessingModel,
        CustomVocabulary = source.CustomVocabulary?.ToList() ?? [], SortOrder = int.MaxValue,
        CreatedDate = DateTime.UtcNow, ModifiedDate = DateTime.UtcNow,
    };
}
