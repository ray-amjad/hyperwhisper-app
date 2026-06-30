using System.Net;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services;

public sealed class ModelLibraryManager
{
    private readonly WhisperModelService _whisper;
    private readonly ParakeetModelService _parakeet;
    private readonly LocalLlmModelService _localLlm;
    private readonly ApiKeyService _apiKeys;
    private readonly CloudProviderHealthService _health;

    public ModelLibraryManager(
        WhisperModelService whisper,
        ParakeetModelService parakeet,
        LocalLlmModelService localLlm,
        ApiKeyService apiKeys,
        CloudProviderHealthService health)
    {
        _whisper = whisper;
        _parakeet = parakeet;
        _localLlm = localLlm;
        _apiKeys = apiKeys;
        _health = health;
    }

    public List<LibraryModel> Rebuild()
    {
        var voiceRows = new List<LibraryModel>();
        voiceRows.AddRange(BuildCloudTranscriptionRows());
        voiceRows.AddRange(BuildWhisperRows());
        voiceRows.AddRange(BuildParakeetRows());

        var postRows = BuildPostProcessingRows().ToList();

        var rows = new List<LibraryModel>();
        rows.AddRange(RecommendedSort(voiceRows));
        rows.AddRange(RecommendedSort(postRows));
        rows.AddRange(BuildLocalLlmRows());

        rows.AddRange(BuildCustomEndpointRows());

        return rows;
    }

    private static List<LibraryModel> RecommendedSort(IEnumerable<LibraryModel> models)
        => models
            .OrderByDescending(m => m.Speed + m.Accuracy)
            .ThenByDescending(m => m.Accuracy)
            .ThenByDescending(m => m.Speed)
            .ToList();

    private IEnumerable<LibraryModel> BuildCloudTranscriptionRows()
    {
        foreach (var model in CloudTranscriptionModels.All.Where(m => m.IsAvailable))
        {
            var providerKey = SharedModelsCatalog.CatalogKey(model.Provider);
            var lang = SharedModelsCatalog.GetLanguageSupport(providerKey, CatalogKind.Voice, model.Id);
            yield return new LibraryModel
            {
                Id = $"cloud-tx-{model.Provider.GetIdentifier()}-{model.Id}",
                DisplayName = model.DisplayName,
                ProviderName = model.Provider.GetDisplayName(),
                ProviderAssetName = ProviderAssetName(model.Provider),
                Kind = LibraryModelKind.Voice,
                LocationKind = LibraryModelLocationKind.Cloud,
                StatusKind = StatusForCloud(model.Provider, out var message),
                StatusMessage = message,
                Source = LibraryModelSource.CloudTranscription,
                Speed = CloudSpeed(model.Id),
                Accuracy = CloudAccuracy(model.Id),
                Detail = model.Description,
                SupportsCustomVocabulary = SharedModelsCatalog.SupportsCustomVocabulary(providerKey, CatalogKind.Voice, model.Id),
                AvailableViaHyperWhisperCloud = SharedModelsCatalog.AvailableViaHyperWhisperCloud(providerKey, CatalogKind.Voice, model.Id),
                SupportedLanguages = lang.Codes.ToArray(),
                SupportsAllLanguages = lang.SupportsAll,
                IsHyperWhisperProvider = model.Provider == CloudTranscriptionProvider.HyperWhisperCloud,
                Payload = model
            };
        }
    }

    private IEnumerable<LibraryModel> BuildWhisperRows()
    {
        var vocab = SharedModelsCatalog.SupportsCustomVocabulary(SharedModelsCatalog.LocalWhisperKey, CatalogKind.Voice, "*");
        var cloud = SharedModelsCatalog.AvailableViaHyperWhisperCloud(SharedModelsCatalog.LocalWhisperKey, CatalogKind.Voice, "*");
        foreach (var model in WhisperModelInfo.AllModels)
        {
            var installed = _whisper.IsModelDownloaded(model);
            var unsupportedReason = PlatformHelper.SupportsWhisperTranscription ? null : WhisperUnsupportedReason();
            var status = OfflineStatus(installed, unsupportedReason);
            yield return new LibraryModel
            {
                Id = $"whisper-{model.Type}",
                DisplayName = model.DisplayName.Replace(" (English)", "", StringComparison.Ordinal),
                ProviderName = "Whisper",
                ProviderAssetName = "providerLocalWhisper",
                Kind = LibraryModelKind.Voice,
                LocationKind = LibraryModelLocationKind.Offline,
                StatusKind = status,
                StatusMessage = unsupportedReason != null ? UnsupportedArchitectureMessage() : null,
                Source = LibraryModelSource.Whisper,
                SizeDescription = model.Size,
                Speed = WhisperSpeed(model.Type),
                Accuracy = WhisperAccuracy(model.Type),
                Tag = model.IsEnglishOnly ? "EN" : null,
                Detail = $"Requires {model.RecommendedVramDisplay} VRAM",
                DetailToolTip = unsupportedReason ?? $"Requires {model.RecommendedVramDisplay} VRAM",
                SupportsCustomVocabulary = vocab,
                AvailableViaHyperWhisperCloud = cloud,
                // Multilingual Whisper covers the full base language set; the
                // `.en` variants are English-only.
                SupportedLanguages = model.IsEnglishOnly ? new[] { "en" } : Array.Empty<string>(),
                SupportsAllLanguages = !model.IsEnglishOnly,
                Payload = model
            };
        }
    }

    private IEnumerable<LibraryModel> BuildParakeetRows()
    {
        var vocab = SharedModelsCatalog.SupportsCustomVocabulary(SharedModelsCatalog.ParakeetKey, CatalogKind.Voice, "*");
        var cloud = SharedModelsCatalog.AvailableViaHyperWhisperCloud(SharedModelsCatalog.ParakeetKey, CatalogKind.Voice, "*");
        foreach (var model in ParakeetModelInfo.AllModels)
        {
            var installed = _parakeet.IsModelDownloaded(model);
            var unsupportedReason = PlatformHelper.SupportsParakeetTranscription ? null : ParakeetUnsupportedReason();
            var status = OfflineStatus(installed, unsupportedReason);
            var rating = ParakeetRatings.TryGetValue(model.Id, out var r) ? r : (speed: 5, accuracy: 3);
            // Parakeet v2 is English-only; v3 carries an explicit 25-language set
            // (base-normalized to match the filter's base codes).
            var parakeetLangs = model.IsEnglishOnly
                ? new[] { "en" }
                : LibraryLanguageFilter.BaseCodes(model.SupportedLanguages).ToArray();
            yield return new LibraryModel
            {
                Id = $"parakeet-{model.Id}",
                DisplayName = model.DisplayName.Replace(" (English)", "", StringComparison.Ordinal)
                    .Replace(" (Multilingual)", "", StringComparison.Ordinal),
                ProviderName = model.ProviderDisplayName,
                ProviderAssetName = model.ProviderAssetName,
                Kind = LibraryModelKind.Voice,
                LocationKind = LibraryModelLocationKind.Offline,
                StatusKind = status,
                StatusMessage = unsupportedReason != null ? UnsupportedArchitectureMessage() : null,
                Source = LibraryModelSource.Parakeet,
                SizeDescription = model.Size,
                Speed = rating.speed,
                Accuracy = rating.accuracy,
                Tag = model.IsEnglishOnly ? "EN" : "Multilingual",
                Detail = string.Join(", ", model.SupportedLanguages.Take(8)) + (model.SupportedLanguages.Length > 8 ? "..." : ""),
                DetailToolTip = unsupportedReason,
                SupportsCustomVocabulary = vocab,
                AvailableViaHyperWhisperCloud = cloud,
                SupportedLanguages = parakeetLangs,
                SupportsAllLanguages = false,
                Payload = model
            };
        }
    }

    private IEnumerable<LibraryModel> BuildPostProcessingRows()
    {
        foreach (var model in LanguageModelInfo.AvailableModels.Where(m => m.Provider != PostProcessingProvider.LocalLlm))
        {
            var providerKey = SharedModelsCatalog.CatalogKey(model.Provider);
            yield return new LibraryModel
            {
                Id = $"pp-{model.Provider.ToStringValue()}-{model.Id}",
                DisplayName = model.DisplayName,
                ProviderName = model.Provider.ToDisplayName(),
                ProviderAssetName = ProviderAssetName(model.Provider),
                Kind = LibraryModelKind.Text,
                LocationKind = LibraryModelLocationKind.Cloud,
                StatusKind = StatusForPostProcessing(model.Provider, out var message),
                StatusMessage = message,
                Source = LibraryModelSource.PostProcessing,
                Speed = PostSpeed(model.Id),
                Accuracy = PostAccuracy(model.Id),
                Detail = model.Description,
                SupportsCustomVocabulary = SharedModelsCatalog.SupportsCustomVocabulary(providerKey, CatalogKind.Text, model.Id),
                AvailableViaHyperWhisperCloud = SharedModelsCatalog.AvailableViaHyperWhisperCloud(providerKey, CatalogKind.Text, model.Id),
                IsHyperWhisperProvider = model.Provider == PostProcessingProvider.HyperWhisperCloud,
                Payload = model
            };
        }
    }

    private IEnumerable<LibraryModel> BuildLocalLlmRows()
    {
        var vocab = SharedModelsCatalog.SupportsCustomVocabulary(SharedModelsCatalog.LocalLlmKey, CatalogKind.Text, "*");
        var cloud = SharedModelsCatalog.AvailableViaHyperWhisperCloud(SharedModelsCatalog.LocalLlmKey, CatalogKind.Text, "*");
        var runtimePlan = LocalLlmGpuHelper.GetRuntimePlan();
        var runtimeGuidance = BuildLocalLlmRuntimeGuidance(runtimePlan);
        foreach (var model in LocalLlmModelInfo.AllModels)
        {
            var installed = _localLlm.IsModelDownloaded(model);
            var unsupportedReason = PlatformHelper.SupportsLocalLlmPostProcessing ? null : LocalLlmUnsupportedReason();
            var status = OfflineStatus(installed, unsupportedReason);
            var detail = $"{model.Size} - Requires {model.RecommendedVramDisplay} VRAM - Runtime: {runtimePlan.BackendSummary}";
            yield return new LibraryModel
            {
                Id = $"local-llm-{model.Id}",
                DisplayName = model.DisplayName.Replace(" (Recommended)", "", StringComparison.Ordinal),
                ProviderName = "Local LLM",
                ProviderAssetName = "providerLocalLLM",
                Kind = LibraryModelKind.Text,
                LocationKind = LibraryModelLocationKind.Offline,
                StatusKind = status,
                StatusMessage = unsupportedReason != null ? UnsupportedArchitectureMessage() : null,
                Source = LibraryModelSource.LocalLlm,
                SizeDescription = model.Size,
                Speed = 3,
                Accuracy = model.IsRecommended ? 4 : 3,
                Tag = model.IsRecommended ? "Recommended" : null,
                Detail = detail,
                DetailToolTip = unsupportedReason != null
                    ? $"{model.Description} {unsupportedReason}"
                    : $"{model.Description} Requires {model.RecommendedVramDisplay} VRAM. {runtimeGuidance}",
                SupportsCustomVocabulary = vocab,
                AvailableViaHyperWhisperCloud = cloud,
                Payload = model
            };
        }
    }

    private static string UnsupportedArchitectureMessage()
        => $"Unavailable on {FormatArchitectureName()}";

    private static LibraryModelStatusKind OfflineStatus(bool installed, string? unsupportedReason)
    {
        if (unsupportedReason != null) return LibraryModelStatusKind.Error;
        return installed ? LibraryModelStatusKind.Enabled : LibraryModelStatusKind.Downloadable;
    }

    private static string WhisperUnsupportedReason()
        => PlatformHelper.IsArm64
            ? $"Unavailable on {FormatArchitectureName()}: Whisper.net local transcription on Windows ARM64 requires Windows 11 or Windows Server 2022 or newer."
            : $"Unavailable on {FormatArchitectureName()}: this build does not include a compatible Whisper runtime for this architecture.";

    private static string ParakeetUnsupportedReason()
        => $"Unavailable on {FormatArchitectureName()}: this build does not include a native {FormatArchitectureName()} sherpa-onnx engine daemon for Parakeet, Qwen3 ASR, and Nemotron.";

    private static string LocalLlmUnsupportedReason()
        => $"Unavailable on {FormatArchitectureName()}: this build does not include a compatible local-LLM runtime for this architecture.";

    private static string FormatArchitectureName()
        => PlatformHelper.ArchitectureName.Equals("Arm64", StringComparison.OrdinalIgnoreCase)
            ? "ARM64"
            : PlatformHelper.ArchitectureName;

    private static string BuildLocalLlmRuntimeGuidance(LocalLlmGpuHelper.RuntimePlan plan)
    {
        if (plan.Gpu == null)
        {
            return Loc.S("settings.models.localLlm.hardware.cpuFallback");
        }

        var guidance = plan.WillTryCuda
            ? Loc.S("settings.models.localLlm.hardware.cudaFirst")
            : Loc.S("settings.models.localLlm.hardware.cudaRequiresNvidia");

        var summary = Loc.S(
            "settings.models.localLlm.hardware.runtimeFormat",
            plan.BackendSummary,
            plan.Gpu.Name,
            guidance);

        return plan.SharesGpuWithWhisper
            ? $"{summary} {Loc.S("settings.models.localLlm.hardware.sharedGpu")}"
            : summary;
    }

    private IEnumerable<LibraryModel> BuildCustomEndpointRows()
    {
        foreach (var endpoint in CustomEndpointManager.Instance.GetAllEndpoints())
        {
            var isLocalEndpoint = IsLocalEndpoint(endpoint.EndpointURL);
            yield return new LibraryModel
            {
                Id = $"custom-{endpoint.Id}",
                DisplayName = endpoint.Name,
                ProviderName = isLocalEndpoint ? "Local OpenAI-compatible" : "OpenAI-compatible",
                ProviderAssetName = isLocalEndpoint ? "providerLocalLLM" : "providerOpenAI",
                Kind = LibraryModelKind.Text,
                LocationKind = isLocalEndpoint ? LibraryModelLocationKind.Offline : LibraryModelLocationKind.Cloud,
                StatusKind = endpoint.LastTestSuccess == false ? LibraryModelStatusKind.Error : LibraryModelStatusKind.Enabled,
                StatusMessage = endpoint.LastTestSuccess == false ? "Test failed" : null,
                Source = LibraryModelSource.CustomEndpoint,
                SizeDescription = isLocalEndpoint ? "Local" : null,
                Speed = 3,
                Accuracy = 3,
                Tag = endpoint.LastTestSuccess == true ? "Verified" : null,
                Detail = $"{endpoint.DisplayURL} - {endpoint.ModelName}",
                SupportsCustomVocabulary = false,
                AvailableViaHyperWhisperCloud = false,
                Payload = endpoint
            };
        }
    }

    private static bool IsLocalEndpoint(string endpointUrl)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri)) return false;

        if (uri.IsLoopback) return true;

        var host = uri.Host.TrimEnd('.');
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;

        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => IsPrivateIPv4(bytes),
            System.Net.Sockets.AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal || address.IsIPv6SiteLocal,
            _ => false
        };
    }

    private static bool IsPrivateIPv4(byte[] bytes)
        => bytes.Length == 4
            && (bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254));

    private LibraryModelStatusKind StatusForCloud(CloudTranscriptionProvider provider, out string? message)
    {
        message = null;
        if (!provider.RequiresApiKey())
        {
            return LibraryModelStatusKind.Enabled;
        }

        var hasKey = HasCloudApiKey(provider);
        var status = _health.GetStatus(provider);
        return StatusForHealth(status, hasKey, out message);
    }

    private LibraryModelStatusKind StatusForPostProcessing(PostProcessingProvider provider, out string? message)
    {
        message = null;
        if (!provider.RequiresApiKey())
        {
            return LibraryModelStatusKind.Enabled;
        }

        var status = _health.GetStatus(provider);
        return StatusForHealth(status, _apiKeys.HasApiKey(provider), out message);
    }

    private static LibraryModelStatusKind StatusForHealth(ProviderHealth health, bool hasKey, out string? message)
    {
        message = null;
        return health switch
        {
            ProviderHealth.Healthy => LibraryModelStatusKind.Enabled,
            ProviderHealth.Unauthorized => WithMessage(LibraryModelStatusKind.Error, "Key invalid", out message),
            ProviderHealth.Unreachable => WithMessage(LibraryModelStatusKind.Error, "Provider unreachable", out message),
            ProviderHealth.Checking => hasKey ? LibraryModelStatusKind.Enabled : LibraryModelStatusKind.Locked,
            ProviderHealth.Unknown => hasKey ? LibraryModelStatusKind.Enabled : LibraryModelStatusKind.Locked,
            _ => hasKey ? LibraryModelStatusKind.Enabled : LibraryModelStatusKind.Locked
        };
    }

    private static LibraryModelStatusKind WithMessage(LibraryModelStatusKind status, string value, out string? message)
    {
        message = value;
        return status;
    }

    private bool HasCloudApiKey(CloudTranscriptionProvider provider)
    {
        // Providers that route through HW Cloud (HW Cloud itself, Azure-MAI,
        // Google-Chirp) don't take a user-supplied key — they're always
        // "configured" from the library's perspective.
        if (!provider.RequiresApiKey()) return true;

        var paired = provider.GetApiKeyProvider();
        if (paired != PostProcessingProvider.None)
        {
            return _apiKeys.HasApiKey(paired);
        }

        // Explicit `_ => false` fallthrough: a newly added keyed provider that
        // forgets to wire up its TranscriptionApiKeyType lookup here will
        // correctly report "not configured" instead of silently true.
        return provider switch
        {
            CloudTranscriptionProvider.Deepgram => _apiKeys.HasApiKey(TranscriptionApiKeyType.Deepgram),
            CloudTranscriptionProvider.AssemblyAI => _apiKeys.HasApiKey(TranscriptionApiKeyType.AssemblyAI),
            CloudTranscriptionProvider.ElevenLabs => _apiKeys.HasApiKey(TranscriptionApiKeyType.ElevenLabs),
            CloudTranscriptionProvider.Mistral => _apiKeys.HasApiKey(TranscriptionApiKeyType.Mistral),
            CloudTranscriptionProvider.Soniox => _apiKeys.HasApiKey(TranscriptionApiKeyType.Soniox),
            _ => false
        };
    }

    private static string ProviderAssetName(CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.OpenAI => "providerOpenAI",
        CloudTranscriptionProvider.Groq => "providerGroq",
        CloudTranscriptionProvider.Deepgram => "providerDeepgram",
        CloudTranscriptionProvider.AssemblyAI => "providerAssemblyAI",
        CloudTranscriptionProvider.ElevenLabs => "providerElevenLabs",
        CloudTranscriptionProvider.Mistral => "providerMistral",
        CloudTranscriptionProvider.Soniox => "providerSoniox",
        CloudTranscriptionProvider.Gemini => "providerGemini",
        CloudTranscriptionProvider.Grok => "providerGrok",
        CloudTranscriptionProvider.MicrosoftAzureSpeech => "providerMicrosoft",
        CloudTranscriptionProvider.GoogleSpeech => "providerGoogle",
        _ => "providerLocalWhisper"
    };

    private static string ProviderAssetName(PostProcessingProvider provider) => provider switch
    {
        PostProcessingProvider.OpenAI => "providerOpenAI",
        PostProcessingProvider.Anthropic => "providerAnthropic",
        PostProcessingProvider.Groq => "providerGroq",
        PostProcessingProvider.Grok => "providerGrok",
        PostProcessingProvider.Gemini => "providerGemini",
        PostProcessingProvider.Cerebras => "providerCerebras",
        PostProcessingProvider.Mistral => "providerMistral",
        PostProcessingProvider.LocalLlm => "providerLocalLLM",
        _ => "providerLocalLLM"
    };

    // Empirical (speed, accuracy) ratings sourced from the macOS benchmark
    // in `benchmarks/`. Unknown ids fall back to (3, 3) — "average,
    // unmeasured". Mirror of `cloudRatings` in
    // app/macos/hyperwhisper/Managers/ModelLibraryManager.swift.
    private static readonly Dictionary<string, (int speed, int accuracy)> CloudRatings = new()
    {
        // OpenAI
        ["gpt-4o-mini-transcribe-2025-12-15"] = (4, 3),
        ["gpt-4o-transcribe"]                 = (4, 2),
        ["gpt-4o-mini-transcribe"]            = (4, 3),
        ["whisper-1"]                         = (3, 3),
        // Groq
        ["whisper-large-v3-turbo"]            = (5, 4),
        ["whisper-large-v3"]                  = (5, 3),
        // Deepgram
        ["nova-3-general"]                    = (3, 3),
        ["nova-3-medical"]                    = (3, 4),
        ["nova-2-general"]                    = (3, 2),
        ["nova-2-medical"]                    = (3, 2),
        // AssemblyAI
        ["universal-2"]                       = (3, 4),
        ["universal-3-pro"]                   = (2, 5),
        ["universal-2-medical"]               = (2, 4),
        ["universal-3-pro-medical"]           = (2, 5),
        // ElevenLabs
        ["scribe_v1"]                         = (3, 5),
        ["scribe_v2"]                         = (3, 5),
        // Mistral
        ["voxtral-mini-latest"]               = (4, 2),
        // Soniox
        ["stt-async-v4"]                      = (1, 4),
        // Gemini
        ["gemini-2.5-flash"]                  = (2, 4),
        ["gemini-2.5-flash-lite"]             = (3, 3),
        ["gemini-2.5-pro"]                    = (1, 5),
        ["gemini-3.1-flash-lite-preview"]     = (3, 4),
        ["gemini-3-flash-preview"]            = (2, 1),
        ["gemini-3.1-pro-preview"]            = (1, 5),
    };

    private static readonly Dictionary<string, (int speed, int accuracy)> WhisperRatings = new()
    {
        ["tiny"]           = (5, 1),
        ["tiny.en"]        = (5, 1),
        ["base"]           = (5, 1),
        ["base.en"]        = (5, 2),
        ["small"]          = (4, 2),
        ["small.en"]       = (5, 2),
        ["medium"]         = (4, 3),
        ["medium.en"]      = (4, 2),
        ["large-v2"]       = (3, 3),
        ["large-v3"]       = (3, 3),
        ["large-v3-turbo"] = (4, 3),
    };

    private static readonly Dictionary<string, (int speed, int accuracy)> ParakeetRatings = new()
    {
        ["parakeet-v2"] = (5, 3),
        ["parakeet-v3"] = (5, 3),
        // Qwen3 decodes autoregressively on CPU — slower than the transducer, but
        // strong on Japanese/CJK.
        ["qwen3-asr-0.6b"] = (3, 4),
        // Nemotron-3.5 streaming transducer: fast (real-time-class on CPU),
        // multilingual. Accuracy rating is PROVISIONAL until measured against v3
        // on a real clip — per-VAD-segment streaming may trail offline beam search.
        ["nemotron-3.5-ml-560ms"] = (5, 4),
    };

    private static readonly Dictionary<string, (int speed, int accuracy)> PostProcessingRatings = new()
    {
        ["gpt-4.1"]                                       = (4, 5),
        ["gpt-5.1"]                                       = (4, 5),
        ["gpt-4.1-mini"]                                  = (4, 5),
        ["gpt-5.2"]                                       = (4, 5),
        ["gpt-5.4"]                                       = (3, 5),
        ["gpt-5.4-nano"]                                  = (4, 4),
        ["gpt-5.4-mini"]                                  = (4, 4),
        ["gpt-5-mini"]                                    = (1, 4),
        ["gpt-5"]                                         = (1, 4),
        ["gpt-5-nano"]                                    = (1, 4),
        ["gpt-4.1-nano"]                                  = (4, 4),
        ["claude-sonnet-4-6"]                             = (4, 5),
        ["claude-sonnet-4-5"]                             = (3, 5),
        ["claude-sonnet-4-0"]                             = (3, 5),
        ["claude-haiku-4-5"]                              = (4, 4),
        ["gemini-2.5-flash"]                              = (2, 5),
        ["gemini-3.5-flash"]                              = (2, 5),
        ["gemini-2.5-flash-lite"]                         = (4, 5),
        ["gemini-2.5-pro"]                                = (2, 4),
        ["gemini-3-flash-preview"]                        = (1, 4),
        ["gemini-3-pro-preview"]                          = (2, 3),
        ["gemini-3.1-flash-lite-preview"]                 = (4, 3),
        ["openai/gpt-oss-120b"]                           = (4, 4),
        ["openai/gpt-oss-20b"]                            = (4, 4),
        ["meta-llama/llama-4-maverick-17b-128e-instruct"] = (2, 3),
        ["moonshotai/kimi-k2-instruct"]                   = (2, 3),
        ["grok-4.3"]                                      = (2, 5),
        ["mistral-small-latest"]                          = (2, 3),
        ["open-mistral-nemo"]                             = (2, 2),
        ["zai-glm-4.7"]                                   = (4, 5),
        ["gpt-oss-120b"]                                  = (5, 3),
        ["llama3.1-8b"]                                   = (2, 3),
        ["qwen-3-235b-a22b-instruct-2507"]                = (2, 3),
        ["gemma-4-E2B-it-Q4_K_M.gguf"]                    = (5, 1),
        ["gemma-4-E4B-it-Q4_K_M.gguf"]                    = (4, 2),
        ["gemma-4-26B-A4B-it-UD-Q4_K_M.gguf"]             = (2, 4),
        ["gemma-4-31B-it-Q4_K_M.gguf"]                    = (1, 5),
    };

    private static int CloudSpeed(string id) => CloudRatings.TryGetValue(id, out var r) ? r.speed : 3;
    private static int CloudAccuracy(string id) => CloudRatings.TryGetValue(id, out var r) ? r.accuracy : 3;
    private static int WhisperSpeed(string id) => WhisperRatings.TryGetValue(id, out var r) ? r.speed : 3;
    private static int WhisperAccuracy(string id) => WhisperRatings.TryGetValue(id, out var r) ? r.accuracy : 3;
    private static int PostSpeed(string id) => PostProcessingRatings.TryGetValue(id, out var r) ? r.speed : 3;
    private static int PostAccuracy(string id) => PostProcessingRatings.TryGetValue(id, out var r) ? r.accuracy : 3;
}
