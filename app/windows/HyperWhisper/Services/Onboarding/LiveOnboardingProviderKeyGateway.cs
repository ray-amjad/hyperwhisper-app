// The live IOnboardingProviderKeyGateway adapter for the first-run flow. Part of the onboarding
// composition described in OnboardingLiveDependencies.cs; split out of that file (#941).

using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

// =============================================================================
// Provider keys
// =============================================================================

/// <summary>
/// Bring-your-own-key providers.
/// </summary>
public sealed class LiveOnboardingProviderKeyGateway : IOnboardingProviderKeyGateway
{
    private readonly ApiKeyService _apiKeys;
    private readonly CloudProviderHealthService _health;
    private string? _validationError;

    public LiveOnboardingProviderKeyGateway(ApiKeyService apiKeys, CloudProviderHealthService health)
    {
        _apiKeys = apiKeys;
        _health = health;
    }

    /// <inheritdoc />
    public IReadOnlyList<CloudTranscriptionProvider> Providers => ByokProviders;

    /// <summary>
    /// The reason the LAST credential write failed, or null. Deliberately local to
    /// this gateway rather than read from an app-global validation field: an
    /// unrelated failure from earlier in the session must never appear as this
    /// step's error.
    /// </summary>
    public string? ValidationError => _validationError;

    /// <summary>
    /// Grades the key the user just typed, WITHOUT saving it first, so a failed
    /// credential write cannot be masked by a health check that passed against a
    /// temporary in-memory credential.
    /// </summary>
    public Task<ProviderHealth> ProbeAsync(
        CloudTranscriptionProvider provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        return _health.ProbeAsync(provider, apiKey, cancellationToken);
    }

    /// <summary>
    /// Writes the key to Windows Credential Manager. An empty string DELETES the
    /// entry, mirroring macOS, which is what makes "" round-trip as "there was no
    /// key here" on rollback.
    /// </summary>
    public bool Persist(string key, CloudTranscriptionProvider provider)
    {
        _validationError = null;

        try
        {
            // SetApiKey trims the key and treats null/blank as a delete, and it
            // already registers the change with the health service on the way
            // through - so unlike macOS there is no second registerAPIKeyChange call
            // here. The read-back below compares against the same normalised value,
            // so a restore point holding a key saved with padding still round-trips.
            var value = ApiKeyService.NormalizeKey(key);

            var postProcessing = provider.GetApiKeyProvider();
            HyperWhisper.Platform.Abstractions.PlatformResult written;
            if (postProcessing != PostProcessingProvider.None)
            {
                written = _apiKeys.SetApiKey(postProcessing, value);
            }
            else if (provider.GetTranscriptionApiKeyType() is { } type)
            {
                written = _apiKeys.SetApiKey(type, value);
            }
            else
            {
                _validationError = Loc.S("onboarding.setup.provider.saveFailed");
                return false;
            }

            // The write now reports its own failure (#742); the read-back below
            // stays as the second line of defence.
            if (written.IsFailure)
            {
                _validationError = Loc.S("onboarding.setup.provider.saveFailed");
                return false;
            }

            // Read it back. A vault write that silently did nothing must not be
            // reported as a pass; the seam's contract is that a healthy probe alone
            // is never enough.
            var stored = CurrentKey(provider);
            var persisted = value is null
                ? string.IsNullOrEmpty(stored)
                : string.Equals(stored, value, StringComparison.Ordinal);

            if (!persisted)
            {
                _validationError = Loc.S("onboarding.setup.provider.saveFailed");
            }

            return persisted;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LiveOnboardingProviderKeyGateway: credential write failed: {ex.Message}", ex);
            _validationError = ex.Message;
            return false;
        }
    }

    public bool HasKey(CloudTranscriptionProvider provider) => !string.IsNullOrEmpty(CurrentKey(provider));

    /// <summary>Whatever is stored right now, or "" when nothing is.</summary>
    public string CurrentKey(CloudTranscriptionProvider provider)
    {
        try
        {
            var postProcessing = provider.GetApiKeyProvider();
            if (postProcessing != PostProcessingProvider.None)
            {
                return _apiKeys.GetApiKey(postProcessing) ?? string.Empty;
            }

            return provider.GetTranscriptionApiKeyType() is { } type
                ? _apiKeys.GetApiKey(type) ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingProviderKeyGateway: credential read failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// The providers offered on the "Your API key" branch, in a stable order.
    ///
    /// Every BYOK vendor, minus the three that need no key: HyperWhisper Cloud
    /// (its own step), and Microsoft Azure Speech / Google Speech, whose health
    /// probe short-circuits to Healthy WITHOUT a key. Offering those two would
    /// open the setup gate on a pass that proves nothing.
    ///
    /// It stays a HAND-WRITTEN list because the order is the order the chips are
    /// drawn in, and a catalog flag carries no order. What stops it drifting is
    /// the smoke case "the BYOK list is every key-taking vendor the app
    /// supports", which derives the expected set from
    /// <c>CloudTranscriptionProviderExtensions.RequiresApiKey</c> - the same
    /// predicate ProviderApiKeyWindow uses - so a vendor added to the enum with
    /// no entry here fails the suite instead of quietly disappearing from first
    /// run. Meta MuseSTT is what that case would have caught: #393 shipped it
    /// with full Windows BYOK support and this list never grew.
    /// </summary>
    public static IReadOnlyList<CloudTranscriptionProvider> ByokProviders { get; } = new[]
    {
        CloudTranscriptionProvider.OpenAI,
        CloudTranscriptionProvider.Groq,
        CloudTranscriptionProvider.Deepgram,
        CloudTranscriptionProvider.AssemblyAI,
        CloudTranscriptionProvider.ElevenLabs,
        CloudTranscriptionProvider.Mistral,
        CloudTranscriptionProvider.Soniox,
        CloudTranscriptionProvider.Gemini,
        CloudTranscriptionProvider.GeminiTranscribe,
        CloudTranscriptionProvider.Grok,
        CloudTranscriptionProvider.Meta,
    };

    /// <summary>
    /// The vendors this branch deliberately does not offer, because their health
    /// probe passes without a key (or, for HyperWhisper Cloud, because the flow
    /// has a whole step for it).
    /// </summary>
    internal static readonly IReadOnlyList<CloudTranscriptionProvider> KeylessProviders = new[]
    {
        CloudTranscriptionProvider.HyperWhisperCloud,
        CloudTranscriptionProvider.MicrosoftAzureSpeech,
        CloudTranscriptionProvider.GoogleSpeech,
    };
}
