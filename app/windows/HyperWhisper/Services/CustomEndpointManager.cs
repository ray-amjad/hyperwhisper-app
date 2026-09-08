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
using System.Text.Json;
using System.Threading.Tasks;
using HyperWhisper.Models;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Services;

/// <summary>
/// Manages custom OpenAI-compatible endpoints for post-processing.
/// Singleton pattern matching other services (ApiKeyService, SettingsService).
/// </summary>
public class CustomEndpointManager : IDisposable
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
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
        LoggingService.Info($"CustomEndpointManager: Initialized with {GetAllEndpoints().Count} endpoints");
    }

    /// <summary>
    /// Test seam: build a manager over a caller-supplied transport.
    /// </summary>
    /// <remarks>
    /// The singleton owns a real <see cref="HttpClient"/> aimed at the open
    /// internet, so <see cref="TestEndpointAsync(string, string, string?)"/> —
    /// the "Test" button in the Add/Edit endpoint window — could not be
    /// exercised at all. Every branch of it (the parsed upstream error, the
    /// accepted completion, the non-JSON body) needed a live provider and a
    /// live key. This constructor takes the transport instead, so a test can
    /// script the upstream response. It changes nothing for the app: the
    /// private constructor above still supplies the same 30-second client.
    /// </remarks>
    internal CustomEndpointManager(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // =========================================================================
    // PUBLIC API - CRUD
    // =========================================================================

    /// <summary>
    /// Add a new custom endpoint.
    /// </summary>
    /// <param name="lastTestSuccess">
    /// The outcome of a Test Connection run against exactly this URL, model and
    /// key, or null when the configuration being saved has never been tested.
    /// The Add window used to show its test result and drop it, so a saved
    /// endpoint always started life with no recorded outcome however many times
    /// the user had tested it (#509).
    /// </param>
    /// <param name="validationError">
    /// Why the endpoint was refused, or null when it was accepted. The caller is
    /// expected to SHOW this. It used to go only to the log, so a malformed Base
    /// URL made the Add button do nothing at all — no message, no highlight, and
    /// the one sentence that would have explained it written to a file the user
    /// never opens (#507).
    /// </param>
    /// <returns>The created endpoint, or null if validation fails.</returns>
    public CustomPostProcessingEndpoint? AddEndpoint(
        string name,
        string endpointURL,
        string modelName,
        out string? validationError,
        string? apiKey = null,
        bool? lastTestSuccess = null)
    {
        var endpoint = new CustomPostProcessingEndpoint
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            EndpointURL = endpointURL.Trim(),
            ModelName = modelName.Trim(),
            CreatedAt = DateTime.UtcNow,
            LastTestSuccess = lastTestSuccess,
            LastTestedAt = lastTestSuccess.HasValue ? DateTime.UtcNow : null
        };

        validationError = endpoint.Validate();
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
    /// <param name="lastTestSuccess">
    /// The outcome of a Test Connection run against exactly the configuration
    /// being saved, or null when this save has no test behind it. Null does not
    /// preserve an older outcome that a URL or model change has invalidated —
    /// see the clearing below.
    /// </param>
    /// <param name="validationError">
    /// Why the change was refused, or null when it was accepted. As on
    /// <see cref="AddEndpoint"/>, the caller is expected to show it: Save
    /// Changes used to close over a refused edit in silence (#507).
    /// </param>
    public bool UpdateEndpoint(
        Guid id,
        out string? validationError,
        string? name = null,
        string? endpointURL = null,
        string? modelName = null,
        string? apiKey = null,
        bool? lastTestSuccess = null)
    {
        validationError = null;

        var endpoints = SettingsService.Instance.CustomEndpoints;
        var index = endpoints.FindIndex(e => e.Id == id);
        if (index < 0)
        {
            LoggingService.Warn($"CustomEndpointManager: Endpoint not found: {id}");
            validationError = "This endpoint no longer exists.";
            return false;
        }

        var endpoint = endpoints[index];

        var newName = name?.Trim() ?? endpoint.Name;
        var newURL = endpointURL?.Trim() ?? endpoint.EndpointURL;
        var newModel = modelName?.Trim() ?? endpoint.ModelName;

        // Judged BEFORE anything is written back. The list this came from is the
        // live one — the settings getter hands out the same List, and an endpoint
        // is a class — so mutating first and validating afterwards left the
        // rejected URL sitting in memory on an edit that "did nothing" (#507).
        validationError = new CustomPostProcessingEndpoint
        {
            Id = endpoint.Id,
            Name = newName,
            EndpointURL = newURL,
            ModelName = newModel,
            CreatedAt = endpoint.CreatedAt
        }.Validate();

        if (validationError != null)
        {
            LoggingService.Warn($"CustomEndpointManager: Validation failed: {validationError}");
            return false;
        }

        // A recorded verdict describes one URL, one model and one key. Change any
        // of the three and it no longer describes what is being saved.
        var retiresVerdict =
            newURL != endpoint.EndpointURL ||
            newModel != endpoint.ModelName ||
            (apiKey != null && !string.Equals(apiKey, GetApiKey(id) ?? "", StringComparison.Ordinal));

        endpoint.Name = newName;
        endpoint.EndpointURL = newURL;
        endpoint.ModelName = newModel;

        if (retiresVerdict)
        {
            endpoint.LastTestedAt = null;
            endpoint.LastTestSuccess = null;
        }

        // Applied after the clearing above, so a test that really did run
        // against the values being saved survives an edit to them.
        if (lastTestSuccess.HasValue)
        {
            endpoint.LastTestedAt = DateTime.UtcNow;
            endpoint.LastTestSuccess = lastTestSuccess;
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
        // The URL rule and the probe body are the shared ones (#282), so a test
        // that passes here means the real post-processing call will work — the
        // two used to be built separately and could disagree.
        //
        // Judged leniently, like the runtime: the button must test the request
        // the app would really send. Saving is where the strict rule applies.
        var verdict = LlmPostProcessing.ValidateExistingCustomEndpoint(endpointURL ?? "", modelName ?? "");
        if (!verdict.IsUsable)
        {
            return (false, verdict.Suggestion is { } suggestion
                ? $"{verdict.Message} — did you mean {suggestion}?"
                : verdict.Message ?? "Invalid URL");
        }

        try
        {
            using var request = LlmPostProcessing.BuildCustomEndpointTestRequest(
                verdict.Url, verdict.Model, apiKey);

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
        catch (PortableLlmRequestException ex)
        {
            return (false, ex.Message);
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
    /// <remarks>
    /// The rule lives in the shared core (#282). It was written twice before —
    /// here as <c>\s\(copy(?:\s(\d+))?\)$</c> and again in the macOS manager as
    /// the same regex — and two copies of one naming convention is exactly the
    /// kind of thing that drifts without anyone noticing.
    /// </remarks>
    public static string GenerateCopyName(string originalName) =>
        LlmPostProcessing.NextCopyName(originalName);

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
