// CUSTOM ENDPOINT MANAGER
// Manages user-configured OpenAI-compatible API endpoints for post-processing.
//
// This manager handles:
// - CRUD operations for custom endpoints (stored in settings.json)
// - API key storage via Windows Credential Manager
// - Endpoint testing with a simple "Hello World" request

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Manages custom OpenAI-compatible endpoints for post-processing.
/// Singleton pattern matching other services (ApiKeyService, SettingsService).
/// </summary>
public partial class CustomEndpointManager : IDisposable
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static CustomEndpointManager? _instance;
    private static readonly object _lock = new();

    /// <summary>Thread-safe singleton instance.</summary>
    public static CustomEndpointManager Instance
    {
        get
        {
            lock (_lock)
            {
                return _instance ??= new CustomEndpointManager();
            }
        }
    }

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly HttpClient _httpClient;
    private bool _disposed;

    /// <summary>
    /// Raised when endpoints are added, updated, or deleted.
    /// </summary>
    public event EventHandler? EndpointsChanged;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private CustomEndpointManager()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        LoggingService.Info($"CustomEndpointManager: Initialized with {GetAllEndpoints().Count} endpoints");
    }

    // =========================================================================
    // PUBLIC API - CRUD
    // =========================================================================

    /// <summary>
    /// Add a new custom endpoint.
    /// </summary>
    /// <returns>The created endpoint, or null if validation fails.</returns>
    public CustomPostProcessingEndpoint? AddEndpoint(
        string name,
        string endpointURL,
        string modelName,
        string? apiKey = null)
    {
        var endpoint = new CustomPostProcessingEndpoint
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            EndpointURL = endpointURL.Trim(),
            ModelName = modelName.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        var validationError = endpoint.Validate();
        if (validationError != null)
        {
            LoggingService.Warn($"CustomEndpointManager: Validation failed: {validationError}");
            return null;
        }

        // Save API key if provided
        if (!string.IsNullOrEmpty(apiKey))
        {
            ApiKeyService.Instance.SetCustomEndpointApiKey(endpoint.Id, apiKey);
        }

        // Add to list and save
        var endpoints = SettingsService.Instance.CustomEndpoints;
        endpoints.Add(endpoint);
        SettingsService.Instance.CustomEndpoints = endpoints;

        LoggingService.Info($"CustomEndpointManager: Added endpoint '{endpoint.Name}'");
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return endpoint;
    }

    /// <summary>
    /// Update an existing custom endpoint.
    /// </summary>
    public bool UpdateEndpoint(
        Guid id,
        string? name = null,
        string? endpointURL = null,
        string? modelName = null,
        string? apiKey = null)
    {
        var endpoints = SettingsService.Instance.CustomEndpoints;
        var index = endpoints.FindIndex(e => e.Id == id);
        if (index < 0)
        {
            LoggingService.Warn($"CustomEndpointManager: Endpoint not found: {id}");
            return false;
        }

        var endpoint = endpoints[index];

        if (name != null)
            endpoint.Name = name.Trim();

        if (endpointURL != null)
        {
            var newURL = endpointURL.Trim();
            if (newURL != endpoint.EndpointURL)
            {
                endpoint.EndpointURL = newURL;
                // Clear test status when URL changes
                endpoint.LastTestedAt = null;
                endpoint.LastTestSuccess = null;
            }
        }

        if (modelName != null)
            endpoint.ModelName = modelName.Trim();

        var validationError = endpoint.Validate();
        if (validationError != null)
        {
            LoggingService.Warn($"CustomEndpointManager: Validation failed: {validationError}");
            return false;
        }

        // Update API key if provided
        if (apiKey != null)
        {
            if (string.IsNullOrEmpty(apiKey))
                ApiKeyService.Instance.SetCustomEndpointApiKey(id, null);
            else
                ApiKeyService.Instance.SetCustomEndpointApiKey(id, apiKey);
        }

        endpoints[index] = endpoint;
        SettingsService.Instance.CustomEndpoints = endpoints;

        LoggingService.Info($"CustomEndpointManager: Updated endpoint '{endpoint.Name}'");
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Delete a custom endpoint.
    /// </summary>
    public void DeleteEndpoint(Guid id)
    {
        var endpoints = SettingsService.Instance.CustomEndpoints;
        var endpoint = endpoints.FirstOrDefault(e => e.Id == id);
        if (endpoint == null)
        {
            LoggingService.Warn($"CustomEndpointManager: Attempted to delete non-existent endpoint: {id}");
            return;
        }

        var name = endpoint.Name;
        endpoints.RemoveAll(e => e.Id == id);

        // Delete API key
        ApiKeyService.Instance.SetCustomEndpointApiKey(id, null);

        SettingsService.Instance.CustomEndpoints = endpoints;

        LoggingService.Info($"CustomEndpointManager: Deleted endpoint '{name}'");
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Duplicate a custom endpoint with a new ID and smart copy suffix.
    /// Preserves endpoint settings, test status, and any stored API key.
    /// </summary>
    public CustomPostProcessingEndpoint? DuplicateEndpoint(Guid id)
    {
        var endpoints = SettingsService.Instance.CustomEndpoints;
        var original = endpoints.FirstOrDefault(e => e.Id == id);
        if (original == null)
        {
            LoggingService.Warn($"CustomEndpointManager: Attempted to duplicate non-existent endpoint: {id}");
            return null;
        }

        var duplicate = new CustomPostProcessingEndpoint
        {
            Id = Guid.NewGuid(),
            Name = GenerateCopyName(original.Name),
            EndpointURL = original.EndpointURL,
            ModelName = original.ModelName,
            CreatedAt = DateTime.UtcNow,
            LastTestedAt = original.LastTestedAt,
            LastTestSuccess = original.LastTestSuccess
        };

        var apiKey = GetApiKey(original.Id);
        if (!string.IsNullOrEmpty(apiKey))
        {
            ApiKeyService.Instance.SetCustomEndpointApiKey(duplicate.Id, apiKey);
        }

        endpoints.Add(duplicate);
        SettingsService.Instance.CustomEndpoints = endpoints;

        LoggingService.Info($"CustomEndpointManager: Duplicated endpoint '{original.Name}' as '{duplicate.Name}'");
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return duplicate;
    }

    /// <summary>
    /// Get a custom endpoint by ID.
    /// </summary>
    public CustomPostProcessingEndpoint? GetEndpoint(Guid id)
    {
        return SettingsService.Instance.CustomEndpoints.FirstOrDefault(e => e.Id == id);
    }

    /// <summary>
    /// Get all custom endpoints.
    /// </summary>
    public List<CustomPostProcessingEndpoint> GetAllEndpoints()
    {
        return SettingsService.Instance.CustomEndpoints;
    }

    /// <summary>
    /// Get API key for a custom endpoint.
    /// </summary>
    public string? GetApiKey(Guid endpointId)
    {
        return ApiKeyService.Instance.GetCustomEndpointApiKey(endpointId);
    }

    /// <summary>
    /// Get endpoint from a provider string (e.g., "custom:uuid").
    /// </summary>
    public CustomPostProcessingEndpoint? EndpointFromProviderString(string? providerString)
    {
        var id = CustomPostProcessingEndpoint.ParseCustomProviderString(providerString);
        if (id == null) return null;
        return GetEndpoint(id.Value);
    }

    // =========================================================================
    // TESTING
    // =========================================================================

    /// <summary>
    /// Test a saved custom endpoint with a simple "Hello World" request.
    /// Persists the test result to the endpoint's status.
    /// </summary>
    public async Task<(bool success, string message)> TestEndpointAsync(Guid id)
    {
        var endpoint = GetEndpoint(id);
        if (endpoint == null)
            return (false, "Endpoint not found");

        LoggingService.Info($"CustomEndpointManager: Testing endpoint '{endpoint.Name}' at {endpoint.DisplayURL}");

        var result = await TestEndpointAsync(endpoint.EndpointURL, endpoint.ModelName, GetApiKey(id));
        UpdateTestStatus(id, result.success);

        if (result.success)
            LoggingService.Info($"CustomEndpointManager: Test succeeded for '{endpoint.Name}': {result.message}");
        else
            LoggingService.Warn($"CustomEndpointManager: Test failed for '{endpoint.Name}': {result.message}");

        return result;
    }

    /// <summary>
    /// Test a custom endpoint configuration without saving it.
    /// Used by the Add/Edit window to test before saving.
    /// </summary>
    public async Task<(bool success, string message)> TestEndpointAsync(
        string endpointURL,
        string modelName,
        string? apiKey)
    {
        if (!Uri.TryCreate(endpointURL, UriKind.Absolute, out var uri))
            return (false, "Invalid URL");

        try
        {
            var requestBody = new
            {
                model = modelName,
                messages = new[]
                {
                    new { role = "user", content = "Say hello in one word." }
                },
                max_tokens = 10,
                temperature = 0.0
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                var errorMsg = ParseErrorMessage(errorBody) ?? $"HTTP {(int)response.StatusCode}";
                return (false, errorMsg);
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return (true, content);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Request timed out");
        }
        catch (JsonException)
        {
            return (false, "Invalid response format - expected OpenAI-compatible response");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // =========================================================================
    // PRIVATE METHODS
    // =========================================================================

    /// <summary>
    /// Update test status for an endpoint and save.
    /// </summary>
    private void UpdateTestStatus(Guid id, bool success)
    {
        var endpoints = SettingsService.Instance.CustomEndpoints;
        var index = endpoints.FindIndex(e => e.Id == id);
        if (index < 0) return;

        endpoints[index].LastTestedAt = DateTime.UtcNow;
        endpoints[index].LastTestSuccess = success;
        SettingsService.Instance.CustomEndpoints = endpoints;
    }

    /// <summary>
    /// Parse error message from OpenAI-style error response.
    /// </summary>
    private static string? ParseErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch
        {
            // Not JSON or unexpected format
        }
        return null;
    }

    /// <summary>
    /// Generate smart numbered copy name for duplicating an endpoint.
    /// "Name" → "Name (copy)", "Name (copy)" → "Name (copy 2)", etc.
    /// </summary>
    public static string GenerateCopyName(string originalName)
    {
        var match = CopyPatternRegex().Match(originalName);
        if (match.Success)
        {
            var baseName = originalName[..match.Index];
            if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var number))
            {
                return $"{baseName} (copy {number + 1})";
            }
            return $"{baseName} (copy 2)";
        }
        return $"{originalName} (copy)";
    }

    [GeneratedRegex(@"\s\(copy(?:\s(\d+))?\)$")]
    private static partial Regex CopyPatternRegex();

    // =========================================================================
    // IDISPOSABLE
    // =========================================================================

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
