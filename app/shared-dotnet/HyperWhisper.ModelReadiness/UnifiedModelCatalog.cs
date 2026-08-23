using System.Text.Json;
using HyperWhisper.ModelManagement;

namespace HyperWhisper.ModelReadiness;

public static class UnifiedModelCatalog
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    public static IReadOnlyList<ModelCapability> LoadBundled(
        IEnumerable<CustomEndpointDefinition>? customEndpoints = null)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Catalogs");
        using var models = File.OpenRead(Path.Combine(root, "models-catalog.json"));
        using var stt = File.OpenRead(Path.Combine(root, "cloud-stt-catalog.json"));
        using var postProcessing = File.OpenRead(Path.Combine(root, "cloud-pp-catalog.json"));
        return Load(models, stt, postProcessing, customEndpoints);
    }

    public static IReadOnlyList<ModelCapability> Load(
        Stream modelsCatalog,
        Stream cloudSttCatalog,
        Stream cloudPostProcessingCatalog,
        IEnumerable<CustomEndpointDefinition>? customEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(modelsCatalog);
        ArgumentNullException.ThrowIfNull(cloudSttCatalog);
        ArgumentNullException.ThrowIfNull(cloudPostProcessingCatalog);

        var result = new List<ModelCapability>();
        AddLocalModels(result);

        using var models = JsonDocument.Parse(modelsCatalog, JsonOptions);
        using var stt = JsonDocument.Parse(cloudSttCatalog, JsonOptions);
        using var postProcessing = JsonDocument.Parse(cloudPostProcessingCatalog, JsonOptions);

        AddCloudStt(result, stt.RootElement);
        AddStreaming(result, stt.RootElement);
        AddPostProcessing(result, models.RootElement, postProcessing.RootElement);
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
                model.Id.StartsWith("nemotron-", StringComparison.Ordinal),
                runtime, model.RecommendedVramBytes,
                model.ApproximateSizeBytes, model.IsEnglishOnly,
                RequiresCredential: false));
        }
    }

    private static void AddCloudStt(List<ModelCapability> result, JsonElement root)
    {
        foreach (var provider in RequiredArray(root, "providers"))
        {
            var providerId = RequiredString(provider, "id");
            var sttProvider = RequiredString(provider, "sttProvider");
            var display = RequiredString(provider, "displayName");
            var access = provider.GetProperty("access");
            var vocabDefault = provider.GetProperty("customVocabulary").GetProperty("supported").GetBoolean();
            var languages = ReadLanguages(provider);
            var allLanguages = false;
            var streaming = provider.GetProperty("features").GetProperty("streaming").GetBoolean();
            foreach (var model in RequiredArray(provider, "models"))
            {
                var modelId = RequiredStringAllowEmpty(model, "id");
                var modelName = RequiredString(model, "displayName");
                var vocab = model.TryGetProperty("supportsCustomVocabulary", out var supports)
                    ? supports.GetBoolean() : vocabDefault;
                result.Add(new ModelCapability(
                    $"cloud/stt/{providerId}/{NormalizeEmpty(modelId)}", $"{display} — {modelName}",
                    sttProvider, modelId, ModelDeployment.Cloud, ModelWorkload.Voice,
                    ModelSurface.BatchTranscription, vocab, allLanguages, languages, streaming,
                    CloudTierEligible: access.GetProperty("cloudTierEligible").GetBoolean(),
                    ByokEligible: access.GetProperty("byokEligible").GetBoolean(),
                    CredentialAccount: CredentialAccountFor(sttProvider)));
            }
        }
    }

    private static void AddStreaming(List<ModelCapability> result, JsonElement root)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in RequiredArray(root, "providers"))
        {
            if (!provider.GetProperty("features").GetProperty("streaming").GetBoolean()) continue;
            var sttProvider = RequiredString(provider, "sttProvider");
            var model = RequiredArray(provider, "models").First();
            AddStreamingRow(result, emitted, sttProvider, RequiredStringAllowEmpty(model, "id"),
                RequiredString(provider, "displayName"), ReadLanguages(provider),
                provider.GetProperty("customVocabulary").GetProperty("supported").GetBoolean());
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

    private static void AddPostProcessing(List<ModelCapability> result, JsonElement modelsRoot, JsonElement ppRoot)
    {
        var cloudTier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in RequiredArray(ppRoot, "providers"))
        foreach (var model in RequiredArray(provider, "models"))
        {
            if (provider.TryGetProperty("enabled", out var providerEnabled) && !providerEnabled.GetBoolean()) continue;
            if (model.TryGetProperty("enabled", out var modelEnabled) && !modelEnabled.GetBoolean()) continue;
            var providerId = RequiredString(provider, "llmProvider");
            var modelId = RequiredString(model, "id");
            cloudTier.Add($"{providerId}\n{modelId}");
            result.Add(new ModelCapability(
                $"cloud/pp-tier/{providerId}/{modelId}",
                $"{RequiredString(provider, "displayName")} — {RequiredString(model, "displayName")}",
                providerId, modelId, ModelDeployment.Cloud, ModelWorkload.Text,
                ModelSurface.PostProcessing, false, true, [], false,
                CloudTierEligible: true, CredentialAccount: "LicenseKey"));
        }

        foreach (var model in RequiredArray(modelsRoot, "models"))
        {
            if (!string.Equals(RequiredString(model, "kind"), "text", StringComparison.Ordinal)) continue;
            var provider = RequiredString(model, "provider");
            if (provider is "localLLM") continue;
            var modelId = RequiredStringAllowEmpty(model, "id");
            result.Add(new ModelCapability(
                $"cloud/pp-byok/{provider}/{NormalizeEmpty(modelId)}", modelId,
                provider, modelId, ModelDeployment.Cloud, ModelWorkload.Text,
                ModelSurface.PostProcessing, false,
                model.TryGetProperty("supportsAllLanguages", out var all) && all.GetBoolean(),
                ReadSupportedLanguages(model), false,
                CloudTierEligible: cloudTier.Contains($"{provider}\n{modelId}"), ByokEligible: true,
                CredentialAccount: CredentialAccountFor(provider)));
        }
    }

    private static void AddCustomEndpoints(List<ModelCapability> result, IEnumerable<CustomEndpointDefinition> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            if (!endpoint.Endpoint.IsAbsoluteUri || endpoint.Endpoint.Scheme is not ("https" or "http")
                || !string.IsNullOrEmpty(endpoint.Endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Endpoint.Fragment))
                throw new InvalidDataException("Custom endpoint must be an absolute HTTP(S) URI.");
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

    private static IReadOnlyList<string> ReadLanguages(JsonElement provider) =>
        provider.TryGetProperty("languages", out var languages)
            && languages.TryGetProperty("codes", out var codes)
            && codes.ValueKind == JsonValueKind.Array
            ? codes.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
            : [];

    private static IReadOnlyList<string> ReadSupportedLanguages(JsonElement model) =>
        model.TryGetProperty("supportedLanguages", out var languages)
            && languages.ValueKind == JsonValueKind.Array
            ? languages.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
            : [];

    private static JsonElement.ArrayEnumerator RequiredArray(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray();

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Catalog property '{property}' is required.") : value;
    }

    private static string RequiredStringAllowEmpty(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"Catalog property '{property}' must be a string.");

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
        "grok" or "xai" => "GrokApiKey",
        "azure-mai" or "microsoftazurespeech" => "LicenseKey",
        "google-chirp" or "googlespeech" => "LicenseKey",
        "hyperwhisper" => "LicenseKey",
        "anthropic" => "AnthropicApiKey",
        "cerebras" => "CerebrasApiKey",
        _ => $"Provider_{provider}",
    };
}
