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

using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Platform;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

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

    // The same backend WindowsCredentialStore uses: it narrows the expected miss
    // to ElementNotFound and lets every other Credential Manager fault through,
    // so a vault failure can be told apart from "no key configured" (#742).
    private readonly IWindowsCredentialBackend _credentials;

    public event EventHandler? ApiKeysChanged;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private ApiKeyService() : this(new PasswordVaultCredentialBackend())
    {
    }

    /// <summary>
    /// Test seam: the smoke suite injects a backend whose write throws, to prove a
    /// Credential Manager failure reaches the caller instead of being swallowed.
    /// </summary>
    internal ApiKeyService(IWindowsCredentialBackend credentials)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
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
            return ReadOrNull(settingName);
        }
    }

    /// <summary>
    /// Sets or removes the API key for a provider.
    /// </summary>
    /// <param name="provider">The post-processing provider.</param>
    /// <param name="apiKey">The API key to store, or null/empty to remove.</param>
    /// <returns>
    /// Success when Credential Manager holds the new value (or no longer holds a
    /// cleared one). A failure means the key was NOT stored; the caller must say so.
    /// </returns>
    public PlatformContracts.PlatformResult SetApiKey(PostProcessingProvider provider, string? apiKey)
    {
        lock (_lock)
        {
            var settingName = provider.GetApiKeySettingName();
            if (string.IsNullOrEmpty(settingName)) return NoKeySlot();
            var result = SaveToVault(settingName, apiKey);
            if (result.IsSuccess)
            {
                RegisterTranscriptionApiKeyChange(CloudProviderHealthService.Instance, provider, apiKey);
            }
            // Raised on failure too: the backend deletes before it adds, so a failed
            // add can already have removed the previous key. Listeners re-read.
            ApiKeysChanged?.Invoke(this, EventArgs.Empty);
            return result;
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
            return ReadOrNull(settingName);
        }
    }

    /// <summary>
    /// Sets or removes the API key for a transcription provider without a primary post-processing provider.
    /// </summary>
    /// <param name="type">The transcription API key type.</param>
    /// <param name="apiKey">The API key to store, or null/empty to remove.</param>
    /// <returns>Success, or a failure meaning the key was NOT stored.</returns>
    public PlatformContracts.PlatformResult SetApiKey(TranscriptionApiKeyType type, string? apiKey)
    {
        lock (_lock)
        {
            var settingName = type.GetSettingName();
            if (string.IsNullOrEmpty(settingName)) return NoKeySlot();
            var result = SaveToVault(settingName, apiKey);
            if (result.IsSuccess)
            {
                RegisterTranscriptionApiKeyChange(CloudProviderHealthService.Instance, type, apiKey);
            }
            ApiKeysChanged?.Invoke(this, EventArgs.Empty);
            return result;
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
            return ReadOrNull($"CustomEndpoint_{endpointId}");
        }
    }

    /// <summary>
    /// Sets or removes the API key for a custom endpoint.
    /// </summary>
    public PlatformContracts.PlatformResult SetCustomEndpointApiKey(Guid endpointId, string? apiKey)
    {
        lock (_lock)
        {
            var result = SaveToVault($"CustomEndpoint_{endpointId}", apiKey);
            ApiKeysChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    /// <summary>
    /// Connects the production key-write seam to transcription health. The
    /// injected service overload also gives the smoke suite a vault-free way to
    /// prove that the exact mapping used by SetApiKey advances only the affected
    /// provider's credential generation.
    /// </summary>
    internal static void RegisterTranscriptionApiKeyChange(
        CloudProviderHealthService healthService,
        PostProcessingProvider provider,
        string? apiKey)
    {
        var transcriptionProvider = provider switch
        {
            PostProcessingProvider.OpenAI => CloudTranscriptionProvider.OpenAI,
            PostProcessingProvider.Groq => CloudTranscriptionProvider.Groq,
            PostProcessingProvider.Grok => CloudTranscriptionProvider.Grok,
            PostProcessingProvider.Gemini => CloudTranscriptionProvider.Gemini,
            PostProcessingProvider.Mistral => CloudTranscriptionProvider.Mistral,
            _ => CloudTranscriptionProvider.None
        };

        if (transcriptionProvider != CloudTranscriptionProvider.None)
        {
            healthService.RegisterApiKeyChange(transcriptionProvider, apiKey);
        }
    }

    internal static void RegisterTranscriptionApiKeyChange(
        CloudProviderHealthService healthService,
        TranscriptionApiKeyType type,
        string? apiKey)
    {
        var transcriptionProvider = type switch
        {
            TranscriptionApiKeyType.Deepgram => CloudTranscriptionProvider.Deepgram,
            TranscriptionApiKeyType.AssemblyAI => CloudTranscriptionProvider.AssemblyAI,
            TranscriptionApiKeyType.ElevenLabs => CloudTranscriptionProvider.ElevenLabs,
            TranscriptionApiKeyType.Mistral => CloudTranscriptionProvider.Mistral,
            TranscriptionApiKeyType.Soniox => CloudTranscriptionProvider.Soniox,
            TranscriptionApiKeyType.Grok => CloudTranscriptionProvider.Grok,
            TranscriptionApiKeyType.GeminiTranscribe => CloudTranscriptionProvider.GeminiTranscribe,
            _ => CloudTranscriptionProvider.None
        };

        if (transcriptionProvider != CloudTranscriptionProvider.None)
        {
            healthService.RegisterApiKeyChange(transcriptionProvider, apiKey);
        }
    }

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
    /// Reads a provider's key, or null when none is stored. A Credential Manager
    /// fault is logged as an error here, so it is no longer indistinguishable from
    /// an unconfigured provider in the log (#742); callers that only ask "is there
    /// a usable key" still get null, because there is none they can use.
    /// </summary>
    private string? ReadOrNull(string settingName)
    {
        var result = RetrieveFromVault(settingName);
        return result.IsSuccess ? result.Value : null;
    }

    /// <summary>
    /// Retrieves an API key from Windows Credential Manager.
    /// </summary>
    /// <param name="settingName">The credential username (provider identifier).</param>
    /// <returns>
    /// Success(null) when no credential exists (ElementNotFound, the normal state
    /// of an unconfigured provider); Failure for every other Credential Manager fault.
    /// </returns>
    private PlatformContracts.PlatformResult<string?> RetrieveFromVault(string settingName)
    {
        try
        {
            return PlatformContracts.PlatformResult<string?>.Success(
                _credentials.TryRead(VaultResource, settingName, out var value) ? value : null);
        }
        catch (Exception ex)
        {
            LoggingService.Error($"ApiKeyService: Failed to read credential for {settingName}", ex);
            return PlatformContracts.PlatformResult<string?>.Failure(
                "credential.read_failed", $"Windows Credential Manager could not read the credential for {settingName}.");
        }
    }

    /// <summary>
    /// Saves or removes an API key in Windows Credential Manager.
    /// </summary>
    /// <param name="settingName">The credential username (provider identifier).</param>
    /// <param name="apiKey">The API key to store, or null/empty to remove.</param>
    /// <returns>Success, or a failure meaning Credential Manager did not take the change.</returns>
    private PlatformContracts.PlatformResult SaveToVault(string settingName, string? apiKey)
    {
        var clearing = string.IsNullOrEmpty(apiKey);
        try
        {
            if (clearing)
            {
                // A missing credential is not an error (ElementNotFound is swallowed
                // by the backend); any other fault is.
                _credentials.Delete(VaultResource, settingName);
                LoggingService.Info($"ApiKeyService: Cleared API key for {settingName}");
            }
            else
            {
                // Write removes any existing credential, then adds the new one.
                _credentials.Write(VaultResource, settingName, apiKey!);
                LoggingService.Info($"ApiKeyService: Saved API key for {settingName}");
            }

            return PlatformContracts.PlatformResult.Success();
        }
        catch (Exception ex)
        {
            LoggingService.Error($"ApiKeyService: Failed to save credential for {settingName}", ex);
            return clearing
                ? PlatformContracts.PlatformResult.Failure(
                    "credential.delete_failed", $"Windows Credential Manager could not delete the credential for {settingName}.")
                : PlatformContracts.PlatformResult.Failure(
                    "credential.write_failed", $"Windows Credential Manager could not write the credential for {settingName}.");
        }
    }

    private static PlatformContracts.PlatformResult NoKeySlot()
        => PlatformContracts.PlatformResult.Failure(
            "credential.no_slot", "This provider has no API key slot.");
}
