// LOCAL AI MODEL FETCHER SERVICE
// Fetches available models from local AI providers like Ollama and LMStudio.
//
// Supported Providers:
// - Ollama: GET /api/tags
// - LMStudio: GET /v1/models (OpenAI-compatible)

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace HyperWhisper.Services;

/// <summary>
/// Service for fetching available models from local AI providers.
/// </summary>
public static class LocalModelFetcher
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// Fetch available models from Ollama.
    /// </summary>
    /// <param name="baseURL">Ollama base URL (default: http://localhost:11434)</param>
    /// <returns>List of model names, or empty list on failure.</returns>
    public static async Task<List<string>> FetchOllamaModelsAsync(string baseURL = "http://localhost:11434")
    {
        try
        {
            var normalizedBase = baseURL.TrimEnd('/');
            var endpoint = $"{normalizedBase}/api/tags";

            var response = await _httpClient.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                    {
                        var modelName = name.GetString();
                        if (!string.IsNullOrEmpty(modelName))
                            models.Add(modelName);
                    }
                }
            }

            LoggingService.Info($"LocalModelFetcher: Fetched {models.Count} Ollama models from {normalizedBase}");
            return models;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"LocalModelFetcher: Failed to fetch Ollama models: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Fetch available models from LMStudio.
    /// </summary>
    /// <param name="baseURL">LMStudio base URL (default: http://localhost:1234/v1)</param>
    /// <returns>List of model IDs, or empty list on failure.</returns>
    public static async Task<List<string>> FetchLMStudioModelsAsync(string baseURL = "http://localhost:1234/v1")
    {
        try
        {
            var normalizedBase = baseURL.TrimEnd('/');
            var endpoint = $"{normalizedBase}/models";

            var response = await _httpClient.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var model in dataArray.EnumerateArray())
                {
                    if (model.TryGetProperty("id", out var id))
                    {
                        var modelId = id.GetString();
                        if (!string.IsNullOrEmpty(modelId))
                            models.Add(modelId);
                    }
                }
            }

            LoggingService.Info($"LocalModelFetcher: Fetched {models.Count} LMStudio models from {normalizedBase}");
            return models;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"LocalModelFetcher: Failed to fetch LMStudio models: {ex.Message}");
            return [];
        }
    }
}
