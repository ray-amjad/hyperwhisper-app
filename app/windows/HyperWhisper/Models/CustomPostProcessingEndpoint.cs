// CUSTOM POST-PROCESSING ENDPOINT MODEL
// Represents a user-configured OpenAI-compatible API endpoint for post-processing.
//
// This model allows users to add their own LLM endpoints (like Ollama, LM Studio,
// or any OpenAI-compatible API) for text post-processing.
//
// Key Features:
// - Each endpoint is a single URL + model combination
// - API keys are stored separately in Windows Credential Manager for security
// - Tracks test status to show users if the endpoint is working

using System;

namespace HyperWhisper.Models;

/// <summary>
/// Represents a custom OpenAI-compatible endpoint for post-processing.
/// Stored as JSON in settings.json (not in the database).
/// API keys are stored separately in Windows Credential Manager via ApiKeyService.
/// </summary>
public class CustomPostProcessingEndpoint
{
    /// <summary>
    /// Unique identifier for this endpoint configuration.
    /// Used to link API keys in Credential Manager and to reference in Mode settings.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// User-defined display name for this endpoint.
    /// Example: "My Ollama Server", "LM Studio Local"
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Full URL to the OpenAI-compatible chat completions endpoint.
    /// Example: "http://localhost:11434/v1/chat/completions"
    /// </summary>
    public string EndpointURL { get; set; } = "";

    /// <summary>
    /// Model identifier to use with this endpoint.
    /// Example: "llama3.2", "gpt-4", "mistral-7b-instruct"
    /// </summary>
    public string ModelName { get; set; } = "";

    /// <summary>When this endpoint configuration was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the endpoint was last tested (null if never tested).</summary>
    public DateTime? LastTestedAt { get; set; }

    /// <summary>Result of the last test (null if never tested).</summary>
    public bool? LastTestSuccess { get; set; }

    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>Prefix for custom endpoint provider strings.</summary>
    public const string ProviderPrefix = "custom:";

    // =========================================================================
    // COMPUTED PROPERTIES
    // =========================================================================

    /// <summary>
    /// Provider string used in Mode settings storage.
    /// Format: "custom:&lt;uuid&gt;" to distinguish from built-in providers.
    /// </summary>
    public string ProviderString => $"{ProviderPrefix}{Id}";

    /// <summary>
    /// Shortened URL for display in UI (removes protocol, truncates long paths).
    /// </summary>
    public string DisplayURL
    {
        get
        {
            var display = EndpointURL
                .Replace("https://", "")
                .Replace("http://", "");

            if (display.Length > 40)
                display = display[..37] + "...";

            return display;
        }
    }

    /// <summary>Whether the endpoint has been tested and passed.</summary>
    public bool IsVerified => LastTestSuccess == true;

    // =========================================================================
    // STATIC HELPERS
    // =========================================================================

    /// <summary>
    /// Check if a provider string represents a custom endpoint.
    /// </summary>
    public static bool IsCustomProviderString(string? providerString) =>
        providerString?.StartsWith(ProviderPrefix) == true;

    /// <summary>
    /// Parse a provider string to extract the custom endpoint UUID.
    /// </summary>
    /// <returns>The UUID if this is a valid custom provider string, null otherwise.</returns>
    public static Guid? ParseCustomProviderString(string? providerString)
    {
        if (providerString == null || !providerString.StartsWith(ProviderPrefix))
            return null;

        return Guid.TryParse(providerString[ProviderPrefix.Length..], out var id) ? id : null;
    }

    // =========================================================================
    // VALIDATION
    // =========================================================================

    /// <summary>
    /// Validate the endpoint configuration.
    /// </summary>
    /// <returns>Null if valid, or an error message string if invalid.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Name is required";

        var trimmedURL = EndpointURL?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmedURL))
            return "Endpoint URL is required";

        if (!Uri.TryCreate(trimmedURL, UriKind.Absolute, out _))
            return "Invalid URL format";

        if (string.IsNullOrWhiteSpace(ModelName))
            return "Model name is required";

        return null;
    }

    /// <summary>Check if the endpoint configuration is valid.</summary>
    public bool IsValid => Validate() == null;
}
