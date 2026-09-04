using HyperWhisper.ModelManagement;
using HyperWhisper.SharedCore;
// Rust shared-core binding. The catalog rows below all come from here.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.ModelReadiness;

/// <summary>
/// Builds the unified model-capability list every .NET head reads.
///
/// This used to be a raw <c>JsonDocument</c> reader over the three shared
/// catalog files — a fourth decoder for <c>cloud-stt-catalog.json</c> alongside
/// macOS, Windows and Rust (issue #280), and one that would have crashed on the
/// documented <c>"unverified"</c> form of <c>customVocabulary.supported</c>,
/// because <c>GetBoolean()</c> throws on a string. Every catalog read now goes
/// through the shared Rust core, which owns the polymorphic decoding; the local
/// model rows and the credential-account mapping stay here because they are
/// .NET-side facts, not catalog data.
///
/// The catalogs are <c>include_str!</c>'d into the core at compile time, so
/// there is no file to open and no load ordering to get wrong.
/// </summary>
public static class UnifiedModelCatalog
{
    public static IReadOnlyList<ModelCapability> LoadBundled(
        IEnumerable<CustomEndpointDefinition>? customEndpoints = null)
        => Load(customEndpoints);

    public static IReadOnlyList<ModelCapability> Load(
        IEnumerable<CustomEndpointDefinition>? customEndpoints = null)
    {
        var result = new List<ModelCapability>();
        AddLocalModels(result);

        var sttEntries = HyperwhisperCoreMethods.CloudSttEntries();
        AddCloudStt(result, sttEntries);
        AddStreaming(result, sttEntries);
        AddPostProcessing(result);
        AddCustomEndpoints(result, customEndpoints ?? []);

        var duplicate = result.GroupBy(item => item.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Duplicate model capability key: {duplicate.Key}");
        return result.AsReadOnly();
    }

    private static void AddLocalModels(List<ModelCapability> result)
    {
        foreach (var model in PortableModelCatalog.All)
        {
            var provider = model.Kind switch
            {
                ManagedModelKind.Whisper => "localWhisper",
                ManagedModelKind.Parakeet => "parakeet",
                ManagedModelKind.LocalLlm => "localLLM",
                _ => throw new InvalidDataException("Unknown local model kind."),
            };
            var workload = model.Kind == ManagedModelKind.LocalLlm ? ModelWorkload.Text : ModelWorkload.Voice;
            var surface = workload == ModelWorkload.Text ? ModelSurface.PostProcessing : ModelSurface.BatchTranscription;
            var runtime = model.Kind switch
            {
                ManagedModelKind.Whisper => "whisper.cpp",
                ManagedModelKind.Parakeet => "sherpa-onnx",
                ManagedModelKind.LocalLlm => "llama.cpp",
                _ => null,
            };
            result.Add(new ModelCapability(
                $"local/{provider}/{model.Id}", model.DisplayName, provider, model.Id,
                ModelDeployment.Local, workload, surface,
                model.Kind == ManagedModelKind.Whisper,
                !model.IsEnglishOnly && model.SupportedLanguages.Count == 0,
                model.SupportedLanguages,
                model.SupportsStreaming,
                runtime, model.RecommendedVramBytes,
                model.ApproximateSizeBytes, model.IsEnglishOnly,
                RequiresCredential: false));

            if (!model.SupportsStreaming) continue;
            var streamingProvider = model.Id.StartsWith("nemotron-", StringComparison.Ordinal)
                ? "nemotronLocal"
                : "parakeetLocal";
            result.Add(new ModelCapability(
                $"local/streaming/{streamingProvider}/{model.Id}", model.DisplayName,
                streamingProvider, model.Id, ModelDeployment.Local, ModelWorkload.Voice,
                ModelSurface.StreamingTranscription, SupportsCustomVocabulary: false,
                SupportsAllLanguages: false, model.SupportedLanguages, SupportsStreaming: true,
                runtime, model.RecommendedVramBytes, model.ApproximateSizeBytes, model.IsEnglishOnly,
                RequiresCredential: false));
        }
    }

    private static void AddCloudStt(List<ModelCapability> result, IEnumerable<SttEntry> entries)
    {
        foreach (var provider in entries)
        {
            var providerId = Required(provider.@id, "id");
            var sttProvider = Required(provider.@sttProvider, "sttProvider");
            var display = Required(provider.@displayName, "displayName");
            var access = provider.@access
                ?? throw new InvalidDataException("Catalog property 'access' is required.");
            var vocabDefault = ProviderSupportsVocabulary(provider);
            var languages = LanguageCodes(providerId);
            var allLanguages = false;
            var streaming = provider.@features.@streaming;
            foreach (var model in provider.@models)
            {
                var modelId = model.@id;
                var modelName = Required(model.@displayName, "displayName");
                var vocab = model.@supportsCustomVocabulary ?? vocabDefault;
                var modelLanguages = ModelLanguageCodes(providerId, modelId, languages);
                result.Add(new ModelCapability(
                    $"cloud/stt/{providerId}/{NormalizeEmpty(modelId)}", $"{display} — {modelName}",
                    sttProvider, modelId, ModelDeployment.Cloud, ModelWorkload.Voice,
                    ModelSurface.BatchTranscription, vocab, allLanguages, modelLanguages, streaming,
                    CloudTierEligible: access.@cloudTierEligible,
                    ByokEligible: access.@byokEligible,
                    CredentialAccount: CredentialAccountFor(sttProvider)));
            }
        }
    }

    private static void AddStreaming(List<ModelCapability> result, IEnumerable<SttEntry> entries)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in entries)
        {
            if (!provider.@features.@streaming) continue;
            var sttProvider = Required(provider.@sttProvider, "sttProvider");
            if (provider.@models.Count == 0)
                throw new InvalidDataException($"Streaming provider '{sttProvider}' lists no models.");
            // Prefer the model the catalog marks `streaming: true` over models[0]:
            // geminiTranscribe lists the BATCH row first (gemini-3.5-transcribe) and
            // the live one second (gemini-3.5-transcribe-live), so models[0] would
            // publish a streaming capability under a model that has no live route.
            // For every pre-existing streaming vendor models[0] is either already the
            // streaming row (deepgram) or has no `streaming: true` row at all, so this
            // falls back to exactly today's behaviour.
            var liveModel = provider.@models.FirstOrDefault(model => model.@streaming == true);
            // Meta advertises upstream streaming capability, but HyperWhisper
            // implements only its batch/file route. Do not turn the provider-
            // level flag into a portable live picker row when no model opts in.
            if (liveModel is null && sttProvider.Equals("meta", StringComparison.OrdinalIgnoreCase))
                continue;
            liveModel ??= provider.@models[0];
            AddStreamingRow(result, emitted, sttProvider, liveModel.@id,
                Required(provider.@displayName, "displayName"), LanguageCodes(provider.@id),
                ProviderSupportsVocabulary(provider));
        }

        // Live clients expose these dedicated transports even where the batch catalog deliberately
        // marks the batch model non-streaming or catalogs streaming under a separate model name.
        AddStreamingRow(result, emitted, "openai", "gpt-live-transcribe", "OpenAI Realtime", [], true);
        AddStreamingRow(result, emitted, "elevenlabs", "scribe_v2_realtime", "ElevenLabs Realtime", [], true);
        AddStreamingRow(result, emitted, "hyperwhisper", "default", "HyperWhisper Cloud Live", [], true);
    }

    private static void AddStreamingRow(List<ModelCapability> result, HashSet<string> emitted,
        string provider, string model, string displayName, IReadOnlyList<string> languages, bool vocabulary)
    {
        if (!emitted.Add(provider)) return;
        result.Add(new ModelCapability(
            $"cloud/streaming/{provider}", displayName, provider, model,
            ModelDeployment.Cloud, ModelWorkload.Voice, ModelSurface.StreamingTranscription,
            vocabulary, languages.Count == 0, languages, true,
            CredentialAccount: CredentialAccountFor(provider)));
    }

    private static void AddPostProcessing(List<ModelCapability> result)
    {
        var cloudTier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in HyperwhisperCoreMethods.CloudPpProviders())
        {
            // The core resolves the rollout gate for both levels: `enabled` is
            // already `enabled != false`, and `models` already excludes the
            // models the gate hides.
            if (!provider.@enabled) continue;
            foreach (var model in provider.@models)
            {
                var providerId = Required(provider.@llmProvider, "llmProvider");
                var modelId = Required(model.@id, "id");
                cloudTier.Add($"{providerId}\n{modelId}");
                result.Add(new ModelCapability(
                    $"cloud/pp-tier/{providerId}/{modelId}",
                    $"{Required(provider.@displayName, "displayName")} — {Required(model.@displayName, "displayName")}",
                    providerId, modelId, ModelDeployment.Cloud, ModelWorkload.Text,
                    ModelSurface.PostProcessing, false, true, [], false,
                    CloudTierEligible: true, CredentialAccount: "LicenseKey"));
            }
        }

        foreach (var model in HyperwhisperCoreMethods.ModelsAllEntries())
        {
            if (!string.Equals(model.@kind, "text", StringComparison.Ordinal)) continue;
            var provider = Required(model.@provider, "provider");
            if (provider is "localLLM") continue;
            var modelId = model.@id;
            result.Add(new ModelCapability(
                $"cloud/pp-byok/{provider}/{NormalizeEmpty(modelId)}", modelId,
                provider, modelId, ModelDeployment.Cloud, ModelWorkload.Text,
                ModelSurface.PostProcessing, false,
                model.@supportsAllLanguages == true,
                model.@supportedLanguages,
                false,
                CloudTierEligible: cloudTier.Contains($"{provider}\n{modelId}"), ByokEligible: true,
                CredentialAccount: CredentialAccountFor(provider)));
        }
    }

    private static void AddCustomEndpoints(List<ModelCapability> result, IEnumerable<CustomEndpointDefinition> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            // One rule, in the Rust core (#282). This block used to be the fourth
            // copy of "is this custom endpoint valid", and the four copies did
            // not agree.
            var verdict = LlmPostProcessing.NormalizeCustomEndpoint(
                endpoint.Endpoint.OriginalString, endpoint.ModelId);
            if (verdict.Status != PortableEndpointStatus.Valid)
                throw new InvalidDataException(
                    verdict.Message ?? "Custom endpoint must be an absolute HTTP(S) URI.");
            if (endpoint.RequiresCredential && string.IsNullOrWhiteSpace(endpoint.CredentialAccount))
                throw new InvalidDataException("Custom endpoint credential account is required.");
            result.Add(new ModelCapability(
                $"custom/{endpoint.Id:D}", endpoint.DisplayName, "custom", endpoint.ModelId,
                ModelDeployment.Cloud, ModelWorkload.Text, ModelSurface.CustomEndpoint,
                false, true, [], false, Endpoint: endpoint.Endpoint,
                ByokEligible: true, CredentialAccount: endpoint.CredentialAccount,
                RequiresCredential: endpoint.RequiresCredential));
        }
    }

    /// <summary>
    /// Whether the provider as a whole declares vocabulary support. The catalog
    /// field is bool-or-<c>"unverified"</c>; the old raw reader called
    /// <c>GetBoolean()</c> on it and would have thrown on the next entry that
    /// used the documented string form. Only an explicit yes counts — the same
    /// conservative rule the pickers use.
    /// </summary>
    private static bool ProviderSupportsVocabulary(SttEntry provider)
        => provider.@customVocabulary?.@supported == VocabSupport.Yes;

    /// <summary>
    /// Raw upstream language codes for a provider; empty when the catalog leaves
    /// the set <c>"unverified"</c>. Blank entries are dropped, matching the old
    /// reader's <c>Where(item =&gt; item.Length &gt; 0)</c>.
    /// </summary>
    private static IReadOnlyList<string> LanguageCodes(string providerId)
        => HyperwhisperCoreMethods.CloudSttLanguageCodes(providerId)
               ?.Where(code => code.Length > 0).ToArray()
           ?? [];

    /// <summary>
    /// Cloud STT entry ids whose models do NOT share one language set, mapped to
    /// their <c>shared-models/models-catalog.json</c> provider key.
    ///
    /// <c>cloud-stt-catalog.json</c>'s <c>languages.codes</c> is provider-level
    /// by schema. For <c>azureMaiTranscribe</c> it is the UNION of
    /// MAI-Transcribe 2's 60 locales and 1.5's 42, so handing it to every model
    /// row makes the Model Library claim MAI-Transcribe 1.5 transcribes Hebrew
    /// and <c>SupportsLanguage("he")</c> answer true for it. The per-model split
    /// lives only in <c>models-catalog.json</c> — the same data the Windows Mode
    /// editor and macOS <c>STTLanguageTemplates</c> narrow on.
    ///
    /// Deliberately a short opt-in list rather than "always prefer the per-model
    /// set". The two files use different CODE SPACES: this file's rows are raw
    /// upstream codes (Deepgram's include <c>multi</c> and region variants like
    /// <c>ar-AE</c>), models-catalog's are folded picker codes. Swapping the
    /// space wholesale would change what <c>SupportsLanguage("en-US")</c> answers
    /// for four other vendors, none of which this change is about. Deepgram and
    /// AssemblyAI do have the same provider-level over-claim (a medical model
    /// listed as if it spoke every language) — pre-existing, and recorded as an
    /// open question rather than fixed here.
    /// </summary>
    private static readonly Dictionary<string, string> PerModelLanguageProviders =
        new(StringComparer.Ordinal)
        {
            ["azureMaiTranscribe"] = "microsoftAzureSpeech",
        };

    /// <summary>
    /// Language codes for ONE cloud STT model: the per-model set where the
    /// catalog pair above says the models differ, otherwise the provider list
    /// unchanged. A model the per-model file does not carry (a stale id, a new
    /// row landing in one file first) also keeps the provider list, so a row
    /// never ends up claiming NO languages.
    /// </summary>
    private static IReadOnlyList<string> ModelLanguageCodes(
        string providerId, string modelId, IReadOnlyList<string> providerCodes)
        => PerModelLanguageProviders.TryGetValue(providerId, out var modelsCatalogKey)
            ? SharedCoreBridge.SharedModelVoiceLanguageCodes(modelsCatalogKey, modelId) ?? providerCodes
            : providerCodes;

    private static string Required(string? value, string property)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Catalog property '{property}' is required.") : value;

    private static string NormalizeEmpty(string value) => value.Length == 0 ? "default" : value;

    public static string CredentialAccountFor(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" => "OpenAIApiKey",
        "groq" => "GroqApiKey",
        "deepgram" => "DeepgramApiKey",
        "assemblyai" => "AssemblyAIApiKey",
        "elevenlabs" => "ElevenLabsApiKey",
        "mistral" => "MistralApiKey",
        "soniox" => "SonioxApiKey",
        "gemini" => "GeminiApiKey",
        "geminitranscribe" or "gemini-transcribe" => "GeminiTranscribeApiKey",
        "grok" or "xai" => "GrokApiKey",
        "meta" => "MetaApiKey",
        "azure-mai" or "microsoftazurespeech" => "LicenseKey",
        "google-chirp" or "googlespeech" => "LicenseKey",
        "hyperwhisper" => "LicenseKey",
        "anthropic" => "AnthropicApiKey",
        "cerebras" => "CerebrasApiKey",
        _ => $"Provider_{provider}",
    };
}
