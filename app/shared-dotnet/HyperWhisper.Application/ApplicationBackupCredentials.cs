using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.Persistence;

public sealed partial class ApplicationBackupService
{
    private const string CredentialResource = "HyperWhisper";
    private const int MaximumProviderIdLength = 64;
    private const int MaximumCredentialBytes = 16 * 1024;

    private static readonly ImmutableDictionary<string, string> CredentialAccounts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["openai"] = "OpenAIApiKey",
            ["anthropic"] = "AnthropicApiKey",
            ["cerebras"] = "CerebrasApiKey",
            ["groq"] = "GroqApiKey",
            ["deepgram"] = "DeepgramApiKey",
            ["assemblyai"] = "AssemblyAIApiKey",
            ["elevenlabs"] = "ElevenLabsApiKey",
            ["mistral"] = "MistralApiKey",
            ["soniox"] = "SonioxApiKey",
            ["gemini"] = "GeminiApiKey",
            // A SEPARATE key from "gemini", which is the legacy Gemini
            // post-processing/transcription key: same "AIza" shape, different
            // API, and a user may hold different keys for the two. Squashed
            // lowercase to match the Windows `[JsonPropertyName]` and the macOS
            // member, so a backup round-trips across all three platforms.
            ["geminitranscribe"] = "GeminiTranscribeApiKey",
            ["grok"] = "GrokApiKey",
            ["meta"] = "MetaApiKey",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Every id in <see cref="CredentialAccounts"/>, indexed case-insensitively
    /// so an incoming member can be folded back onto its canonical spelling.
    ///
    /// Windows and macOS write `geminiTranscribe` in camelCase; this file's own
    /// map is squashed lowercase. Before this existed the mismatch was fatal
    /// twice over: the id charset below rejected the capital letter and failed
    /// the WHOLE restore — modes, vocabulary and settings included, not just the
    /// keys — and even past that check the direct `CredentialAccounts[...]`
    /// lookup in ImportCredentials would have thrown on the camelCase key.
    /// </summary>
    private static readonly ImmutableDictionary<string, string> CanonicalProviderIds =
        CredentialAccounts.Keys.ToImmutableDictionary(
            id => id, id => id, StringComparer.OrdinalIgnoreCase);

    private JsonObject ExportCredentials()
    {
        var store = _credentialStore
            ?? throw new InvalidOperationException("Secure credential storage is unavailable, so API keys cannot be exported.");
        var result = new JsonObject();
        foreach (var provider in CredentialAccounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var read = store.Read(CredentialResource, provider.Value);
            if (read.IsFailure)
                throw new InvalidOperationException("An API key could not be read from secure storage.");
            if (read.Value is not { Length: > 0 } bytes) continue;
            try
            {
                if (bytes.Length > MaximumCredentialBytes)
                    throw new InvalidOperationException("An API key exceeds the backup size limit.");
                var value = new UTF8Encoding(false, true).GetString(bytes).Trim();
                if (value.Length == 0) continue;
                if (Encoding.UTF8.GetByteCount(value) > MaximumCredentialBytes)
                    throw new InvalidOperationException("An API key exceeds the backup size limit.");
                result[provider.Key] = value;
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidOperationException("An API key in secure storage is not valid UTF-8.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        return result;
    }

    private static PlatformResult<ImmutableDictionary<string, string>> ParseCredentials(JsonObject root)
    {
        if (root["apiKeys"] is not JsonObject keys)
            return PlatformResult<ImmutableDictionary<string, string>>.Success(
                ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));

        var recognized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var entry in keys)
        {
            // Uppercase is accepted because the sibling platforms emit camelCase
            // ids. Rejecting one here fails the entire restore, not just the key.
            if (entry.Key.Length is 0 or > MaximumProviderIdLength
                || entry.Key.Any(character => character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9') and not '-' and not '_' and not '.'))
                return PlatformResult<ImmutableDictionary<string, string>>.Failure(
                    "backup.invalid_credentials", "The backup contains an invalid API-key provider identifier.");
            if (entry.Value is null) continue;
            // Fold onto the canonical spelling: ImportCredentials indexes
            // CredentialAccounts directly, so an un-folded camelCase id throws.
            var canonicalId = CanonicalProviderIds.TryGetValue(entry.Key, out var folded) ? folded : null;
            if (entry.Value is not JsonValue valueNode || !valueNode.TryGetValue<string>(out var value))
            {
                if (canonicalId is null) continue;
                return PlatformResult<ImmutableDictionary<string, string>>.Failure(
                    "backup.invalid_credentials", "The backup contains an invalid API-key value.");
            }
            value = value.Trim();
            if (value.Length == 0) continue;
            if (value.Length > MaximumCredentialBytes || Encoding.UTF8.GetByteCount(value) > MaximumCredentialBytes)
                return PlatformResult<ImmutableDictionary<string, string>>.Failure(
                    "backup.invalid_credentials", "An API key exceeds the backup size limit.");
            if (canonicalId is null) continue;
            recognized[canonicalId] = value;
        }
        return PlatformResult<ImmutableDictionary<string, string>>.Success(recognized.ToImmutable());
    }

    private PlatformResult<CredentialRestoreState> ImportCredentials(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        var store = _credentialStore;
        if (store is null)
            return PlatformResult<CredentialRestoreState>.Failure(
                "backup.credential_store_unavailable", "Secure credential storage is unavailable.");

        var state = new CredentialRestoreState(store);
        foreach (var credential in credentials.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = CredentialAccounts[credential.Key];
            var previous = store.Read(CredentialResource, account);
            if (previous.IsFailure)
            {
                state.Rollback();
                return PlatformResult<CredentialRestoreState>.Failure(
                    "backup.credential_store_failed", "An existing API key could not be read from secure storage.");
            }
            state.Track(account, previous.Value);

            var bytes = Encoding.UTF8.GetBytes(credential.Value);
            try
            {
                state.MarkWritten(account);
                var written = store.Write(CredentialResource, account, bytes);
                if (written.IsFailure)
                {
                    state.Rollback();
                    return PlatformResult<CredentialRestoreState>.Failure(
                        "backup.credential_store_failed", "An API key could not be written to secure storage.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        return PlatformResult<CredentialRestoreState>.Success(state);
    }

    private sealed class CredentialRestoreState(ICredentialStore store) : IDisposable
    {
        private readonly Dictionary<string, byte[]?> _previous = new(StringComparer.Ordinal);
        private readonly List<string> _written = [];
        private bool _completed;

        public void Track(string account, byte[]? value)
            => _previous[account] = value;

        public void MarkWritten(string account) => _written.Add(account);

        public void Complete()
        {
            _completed = true;
            Dispose();
        }

        public void Rollback()
        {
            if (_completed) return;
            foreach (var account in _written.AsEnumerable().Reverse())
            {
                var previous = _previous[account];
                if (previous is null) _ = store.Delete(CredentialResource, account);
                else _ = store.Write(CredentialResource, account, previous);
            }
            _completed = true;
            Dispose();
        }

        public void Dispose()
        {
            foreach (var value in _previous.Values)
                if (value is { Length: > 0 }) CryptographicOperations.ZeroMemory(value);
            _previous.Clear();
            _written.Clear();
        }
    }
}
