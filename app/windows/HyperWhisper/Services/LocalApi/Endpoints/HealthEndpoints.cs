using System.Reflection;
using System.Runtime.Versioning;
using HyperWhisper.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace HyperWhisper.Services.LocalApi.Endpoints;

/// <summary>
/// `GET /health` — unauthenticated liveness probe. Returns app version, API
/// version, the bound port, PID, and a snapshot of provider keys and local
/// models. Used by the Settings status row and by MCP wrappers as a warmup
/// check before issuing real requests.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HealthEndpoints
{
    public static void Map(IEndpointRouteBuilder app, LocalApiServer server)
    {
        app.MapGet("/health", () =>
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";

            var response = new HealthResponse
            {
                AppVersion = version,
                Port = server.ListeningPort,
                Pid = Environment.ProcessId,
                Providers = BuildTranscriptionProviders(server),
                PostProcessingProviders = BuildPostProcessingProviders(server),
                LocalModels = BuildLocalModels(server)
            };
            return LocalApiResponder.Ok(response);
        });
    }

    private static List<HealthProviderStatus> BuildTranscriptionProviders(LocalApiServer server)
    {
        var list = new List<HealthProviderStatus>();
        var apiKeys = server.ApiKeys;
        var health = server.CloudHealth;
        if (apiKeys == null) return list;

        foreach (CloudTranscriptionProvider provider in Enum.GetValues<CloudTranscriptionProvider>())
        {
            if (provider == CloudTranscriptionProvider.None) continue;

            var keyPresent = HasKeyForTranscriptionProvider(apiKeys, provider);
            var status = health?.GetStatus(provider) ?? ProviderHealth.Unknown;

            list.Add(new HealthProviderStatus
            {
                Id = provider.GetIdentifier(),
                KeyPresent = keyPresent,
                Reachable = status == ProviderHealth.Healthy,
                Status = StatusString(status)
            });
        }
        return list;
    }

    private static bool HasKeyForTranscriptionProvider(ApiKeyService apiKeys, CloudTranscriptionProvider provider)
    {
        // OpenAI / Groq / Gemini / Grok share keys with PostProcessingProvider;
        // Deepgram / AssemblyAI / ElevenLabs / Mistral / Soniox are
        // transcription-only. HyperWhisperCloud is keyless.
        return provider switch
        {
            CloudTranscriptionProvider.OpenAI => apiKeys.HasApiKey(PostProcessingProvider.OpenAI),
            CloudTranscriptionProvider.Groq => apiKeys.HasApiKey(PostProcessingProvider.Groq),
            CloudTranscriptionProvider.Gemini => apiKeys.HasApiKey(PostProcessingProvider.Gemini),
            CloudTranscriptionProvider.Grok => apiKeys.HasApiKey(PostProcessingProvider.Grok),
            CloudTranscriptionProvider.Deepgram => apiKeys.HasApiKey(TranscriptionApiKeyType.Deepgram),
            CloudTranscriptionProvider.AssemblyAI => apiKeys.HasApiKey(TranscriptionApiKeyType.AssemblyAI),
            CloudTranscriptionProvider.ElevenLabs => apiKeys.HasApiKey(TranscriptionApiKeyType.ElevenLabs),
            CloudTranscriptionProvider.Mistral => apiKeys.HasApiKey(TranscriptionApiKeyType.Mistral),
            CloudTranscriptionProvider.Soniox => apiKeys.HasApiKey(TranscriptionApiKeyType.Soniox),
            // HW-Cloud-routed providers are always "configured" — no API key
            // is required (the Fly backend authenticates with license/device).
            CloudTranscriptionProvider.HyperWhisperCloud => true,
            CloudTranscriptionProvider.MicrosoftAzureSpeech => true,
            CloudTranscriptionProvider.GoogleSpeech => true,
            _ => false
        };
    }

    private static List<HealthProviderStatus> BuildPostProcessingProviders(LocalApiServer server)
    {
        var list = new List<HealthProviderStatus>();
        var apiKeys = server.ApiKeys;
        var health = server.CloudHealth;
        if (apiKeys == null) return list;

        foreach (PostProcessingProvider provider in Enum.GetValues<PostProcessingProvider>())
        {
            if (provider == PostProcessingProvider.None) continue;
            if (provider == PostProcessingProvider.LocalLlm) continue; // surfaced under local_models.local_llm

            var status = health?.GetStatus(provider) ?? ProviderHealth.Unknown;
            list.Add(new HealthProviderStatus
            {
                Id = provider.ToStringValue(),
                KeyPresent = apiKeys.HasApiKey(provider),
                Reachable = status == ProviderHealth.Healthy,
                Status = StatusString(status)
            });
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

}
