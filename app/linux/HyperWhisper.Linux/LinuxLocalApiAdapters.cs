using System.Diagnostics;
using HyperWhisper.Data.Entities;
using HyperWhisper.LocalApi;
using HyperWhisper.ModelManagement;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.CloudPostProcessing;
using HyperWhisper.Platform.Abstractions;
using System.Security.Cryptography;

namespace HyperWhisper.Linux;

internal sealed class LinuxLocalApiCapabilityCatalog(
    PortableModelManager models,
    IRecordedAudioTranscriber transcriber,
    ICredentialStore credentials,
    IDeviceIdentityProvider deviceIdentity,
    PortableSettingsService settings) : ILocalApiCapabilityCatalog
{
    public IReadOnlyList<ModelEntry> Models => PortableModelCatalog.All.Select(model => new ModelEntry(
        model.Id, model.Kind == ManagedModelKind.LocalLlm ? "text" : "voice", "local",
        model.DisplayName, models.IsInstalled(model), model.ApproximateSizeBytes / 1_000_000d)).ToArray();

    public IReadOnlyList<ProviderStatus> TranscriptionProviders =>
    [
        new("local", false, transcriber.Capability.IsAvailable,
            transcriber.Capability.IsAvailable ? "ready" : "unavailable"),
    ];

    public IReadOnlyList<ProviderStatus> PostProcessingProviders
    {
        get
        {
            var statuses = new List<ProviderStatus>
            {
                new("local_llm", false, PortableModelCatalog.LocalLlm.Any(models.IsInstalled),
                    PortableModelCatalog.LocalLlm.Any(models.IsInstalled) ? "ready" : "model_required"),
            };
            foreach (var (id, provider) in BuiltInPostProcessingProviders)
            {
                var available = provider == CloudPostProcessingProvider.HyperWhisperCloud
                    ? HasCredential("LicenseKey") || deviceIdentity.GetDeviceIdentity().IsSuccess
                    : HasCredential(CredentialStorePostProcessingCredentialSource.AccountFor(provider));
                statuses.Add(new(id, true, available, available ? "ready" : "credential_required"));
            }
            foreach (var endpoint in settings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? [])
                statuses.Add(new($"custom:{endpoint.Id:D}", true, true, "configured"));
            return statuses;
        }
    }

    public object LocalModels => new
    {
        whisper = PortableModelCatalog.Whisper.Select(Status).ToArray(),
        parakeet = PortableModelCatalog.Parakeet.Select(Status).ToArray(),
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

    private static readonly (string Id, CloudPostProcessingProvider Provider)[] BuiltInPostProcessingProviders =
    [
        ("hyperwhispercloud", CloudPostProcessingProvider.HyperWhisperCloud),
        ("openai", CloudPostProcessingProvider.OpenAi),
        ("anthropic", CloudPostProcessingProvider.Anthropic),
        ("groq", CloudPostProcessingProvider.Groq),
        ("grok", CloudPostProcessingProvider.Grok),
        ("gemini", CloudPostProcessingProvider.Gemini),
        ("cerebras", CloudPostProcessingProvider.Cerebras),
        ("mistral", CloudPostProcessingProvider.Mistral),
    ];
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
