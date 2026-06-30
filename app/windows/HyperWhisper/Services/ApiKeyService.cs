// API KEY SERVICE
// Secure storage for API keys using Windows Credential Manager.
// Keys are stored in the Windows Credential Vault and only accessible by the current user.
//
// STORAGE: Windows Credential Manager (visible in Windows Settings > Credential Manager)
//
// SECURITY:
// - Uses Windows PasswordVault - system-level secure credential storage
// - Keys are encrypted at rest by Windows
// - Each key stored as a separate credential under "HyperWhisper" resource

using Windows.Security.Credentials;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Manages secure storage and retrieval of API keys for post-processing providers.
/// Uses Windows Credential Manager (PasswordVault) for encryption at rest.
/// </summary>
public class ApiKeyService
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static ApiKeyService? _instance;
    private static readonly object _lock = new();

    /// <summary>Thread-safe singleton instance.</summary>
    public static ApiKeyService Instance
    {
        get
        {
            lock (_lock)
            {
                return _instance ??= new ApiKeyService();
            }
        }
    }

    // =========================================================================
    // STORAGE
    // =========================================================================

    private static string VaultResource => AppPaths.CredentialResource;
    private readonly PasswordVault _vault = new();

    public event EventHandler? ApiKeysChanged;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private ApiKeyService()
    {
        // PasswordVault doesn't need initialization - credentials are loaded on-demand
        LoggingService.Info("ApiKeyService: Initialized with Windows Credential Manager");
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Gets the API key for a provider.
    /// </summary>
    /// <param name="provider">The post-processing provider.</param>
    /// <returns>The API key, or null if not set.</returns>
    public string? GetApiKey(PostProcessingProvider provider)
    {
        lock (_lock)
        {
            var settingName = provider.GetApiKeySettingName();
            if (string.IsNullOrEmpty(settingName)) return null;
            return RetrieveFromVault(settingName);
        }
    }

    /// <summary>
    /// Sets or removes the API key for a provider.
    /// </summary>
    /// <param name="provider">The post-processing provider.</param>
    /// <param name="apiKey">The API key to store, or null/empty to remove.</param>
    public void SetApiKey(PostProcessingProvider provider, string? apiKey)
    {
        lock (_lock)
        {
            var settingName = provider.GetApiKeySettingName();
            if (string.IsNullOrEmpty(settingName)) return;
            SaveToVault(settingName, apiKey);
            ApiKeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Checks if an API key is configured for a provider.
    /// </summary>
    public bool HasApiKey(PostProcessingProvider provider)
    {
        return !string.IsNullOrEmpty(GetApiKey(provider));
    }

    /// <summary>
    /// Validates the format of an API key for a provider.
    /// This is a basic format check, not a validity check against the API.
    /// </summary>
    /// <param name="provider">The provider to validate against.</param>
    /// <param name="key">The API key to validate.</param>
    /// <returns>True if the key format appears valid.</returns>
    public static bool IsValidKeyFormat(PostProcessingProvider provider, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        return provider switch
        {
            // OpenAI keys start with "sk-" and are typically 51+ characters
            PostProcessingProvider.OpenAI => key.StartsWith("sk-") && key.Length > 20,

            // Anthropic keys start with "sk-ant-" and are typically 100+ characters
            PostProcessingProvider.Anthropic => key.StartsWith("sk-ant-") && key.Length > 20,

            // Groq keys start with "gsk_" and are typically 50+ characters
            PostProcessingProvider.Groq => key.StartsWith("gsk_") && key.Length > 20,

            // xAI Grok keys start with "xai-" and are shared with Grok STT
            PostProcessingProvider.Grok => key.StartsWith("xai-") && key.Length >= 20,

            // Gemini keys start with "AIza" and are typically 39 characters
            PostProcessingProvider.Gemini => key.StartsWith("AIza") && key.Length >= 30,

            // Cerebras keys start with "csk-" and are typically 64+ characters
            PostProcessingProvider.Cerebras => key.StartsWith("csk-") && key.Length > 20,

            // Mistral keys have no fixed prefix; match the Mistral STT key rule
            // (TranscriptionApiKeyType.Mistral: min length 20). PP and STT share
            // the same MistralApiKey store.
            PostProcessingProvider.Mistral => key.Length >= 20,

            _ => false
        };
    }

    /// <summary>
    /// Gets a masked version of the API key for display (e.g., "sk-...abc123").
    /// </summary>
    public string? GetMaskedApiKey(PostProcessingProvider provider)
    {
        var key = GetApiKey(provider);
        return MaskKey(key);
    }

    public static string MaskKeyForDisplay(string? key) => MaskKey(key) ?? "";

    // =========================================================================
    // TRANSCRIPTION API KEY METHODS
    // These overloads handle providers that do not have a primary post-processing provider.
    // Shared providers should use PostProcessingProvider methods instead.
    // =========================================================================

    /// <summary>
    /// Gets the API key for a transcription provider without a primary post-processing provider.
    /// </summary>
    /// <param name="type">The transcription API key type.</param>
    /// <returns>The API key, or null if not set.</returns>
    public string? GetApiKey(TranscriptionApiKeyType type)
    {
        lock (_lock)
        {
            var settingName = type.GetSettingName();
            if (string.IsNullOrEmpty(settingName)) return null;
            return RetrieveFromVault(settingName);
        }
    }

    /// <summary>
    /// Sets or removes the API key for a transcription provider without a primary post-processing provider.
    /// </summary>
    /// <param name="type">The transcription API key type.</param>
    /// <param name="apiKey">The API key to store, or null/empty to remove.</param>
    public void SetApiKey(TranscriptionApiKeyType type, string? apiKey)
    {
        lock (_lock)
        {
            var settingName = type.GetSettingName();
            if (string.IsNullOrEmpty(settingName)) return;
            SaveToVault(settingName, apiKey);
            ApiKeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Checks if an API key is configured for a transcription provider without a primary post-processing provider.
    /// </summary>
    public bool HasApiKey(TranscriptionApiKeyType type)
    {
        return !string.IsNullOrEmpty(GetApiKey(type));
    }

    /// <summary>
    /// Validates the format of an API key for a transcription provider without a primary post-processing provider.
    /// </summary>
    /// <param name="type">The transcription API key type to validate against.</param>
    /// <param name="key">The API key to validate.</param>
    /// <returns>True if the key format appears valid.</returns>
    public static bool IsValidKeyFormat(TranscriptionApiKeyType type, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var prefix = type.GetKeyPrefix();
        var minLength = type.GetMinLength();

        // Check minimum length
        if (key.Length < minLength) return false;

        // Check prefix if required
        if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets a masked version of the API key for a transcription provider without a primary post-processing provider.
    /// </summary>
    public string? GetMaskedApiKey(TranscriptionApiKeyType type)
    {
        var key = GetApiKey(type);
        return MaskKey(key);
    }

    // =========================================================================
    // CUSTOM ENDPOINT API KEY METHODS
    // =========================================================================

    /// <summary>
    /// Gets the API key for a custom endpoint.
    /// </summary>
    public string? GetCustomEndpointApiKey(Guid endpointId)
    {
        lock (_lock)
        {
            return RetrieveFromVault($"CustomEndpoint_{endpointId}");
        }
    }

    /// <summary>
    /// Sets or removes the API key for a custom endpoint.
    /// </summary>
    public void SetCustomEndpointApiKey(Guid endpointId, string? apiKey)
    {
        lock (_lock)
        {
            SaveToVault($"CustomEndpoint_{endpointId}", apiKey);
            ApiKeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Checks if an API key is configured for a custom endpoint.
    /// </summary>
    public bool HasCustomEndpointApiKey(Guid endpointId)
    {
        return !string.IsNullOrEmpty(GetCustomEndpointApiKey(endpointId));
    }

    /// <summary>
    /// Gets a masked version of the API key for a custom endpoint.
    /// </summary>
    public string? GetMaskedCustomEndpointApiKey(Guid endpointId)
    {
        var key = GetCustomEndpointApiKey(endpointId);
        return MaskKey(key);
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    /// <summary>
    /// Masks an API key for display (e.g., "sk-...abc123").
    /// </summary>
    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        if (key.Length <= 10) return "***";

        // Show first 5 chars and last 4 chars
        return $"{key[..5]}...{key[^4..]}";
    }

    // =========================================================================
    // CREDENTIAL VAULT OPERATIONS
    // =========================================================================

    /// <summary>
    /// Retrieves an API key from Windows Credential Manager.
    /// </summary>
    /// <param name="settingName">The credential username (provider identifier).</param>
    /// <returns>The API key, or null if not found.</returns>
    private string? RetrieveFromVault(string settingName)
    {
        try
        {
            var credential = _vault.Retrieve(VaultResource, settingName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            // Credential not found - this is normal for unconfigured providers
            return null;
        }
    }

    /// <summary>
    /// Saves or removes an API key in Windows Credential Manager.
    /// </summary>
    /// <param name="settingName">The credential username (provider identifier).</param>
    /// <param name="apiKey">The API key to store, or null/empty to remove.</param>
    private void SaveToVault(string settingName, string? apiKey)
    {
        try
        {
            // Remove existing credential first (if any)
            try
            {
                var existing = _vault.Retrieve(VaultResource, settingName);
                _vault.Remove(existing);
                LoggingService.Debug($"ApiKeyService: Removed existing credential for {settingName}");
            }
            catch
            {
                // Credential didn't exist - that's fine
            }

            // Add new credential if value is not empty
            if (!string.IsNullOrEmpty(apiKey))
            {
                _vault.Add(new PasswordCredential(VaultResource, settingName, apiKey));
                LoggingService.Info($"ApiKeyService: Saved API key for {settingName}");
            }
            else
            {
                LoggingService.Info($"ApiKeyService: Cleared API key for {settingName}");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error($"ApiKeyService: Failed to save credential for {settingName}: {ex.Message}");
        }
    }
}
