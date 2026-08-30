using System.IO;
using System.Security.Cryptography;
using System.Text;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// Bearer-token storage for the Local API. 32 random bytes → base64-url
/// (43 ASCII chars, no padding). Persisted as a DPAPI-protected blob at
/// %LOCALAPPDATA%\HyperWhisper\local-api-token.bin so it survives across
/// launches but is keyed to the current Windows user account — equivalent
/// threat model to macOS Keychain at user scope.
/// </summary>
internal static class LocalApiAuth
{
    private static readonly string TokenFilePath = AppPaths.Combine("local-api-token.bin");

    // Optional entropy mixed into DPAPI — defence-in-depth against another
    // process running as the same user that tries to unprotect arbitrary blobs.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("com.hyperwhisper.app.localapi.v1");

    /// <summary>
    /// Return the existing token, or generate + persist a fresh one if none
    /// is stored. Failure to persist is logged but never blocks the server —
    /// we'd rather have an API up with a token that resets on next launch
    /// than no API at all.
    /// </summary>
    public static string LoadOrCreateToken()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                var encrypted = File.ReadAllBytes(TokenFilePath);
                if (encrypted.Length > 0)
                {
                    var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    var token = Encoding.UTF8.GetString(plaintext);
                    if (!string.IsNullOrEmpty(token))
                    {
                        return token;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiAuth: failed to read stored token — generating fresh: {ex.Message}");
        }

        var fresh = GenerateToken();
        TryStoreToken(fresh);
        return fresh;
    }

    /// <summary>
    /// Wipe the stored token and generate a new one. The caller is responsible
    /// for restarting the server so the new token gets written into
    /// local-api.json.
    /// </summary>
    public static string RegenerateToken()
    {
        TryDeleteToken();
        return LoadOrCreateToken();
    }

    /// <summary>
    /// Whether an <c>Authorization</c> header presents the stored token.
    ///
    /// THE PARSE AND THE COMPARE LIVE IN RUST (issue #289). All three platforms
    /// did this differently. This one compared UTF-8 bytes directly, and
    /// <c>FixedTimeEquals</c> returns false immediately on a length mismatch,
    /// so the length of the stored token leaked through timing. The shared
    /// version hashes both sides to a fixed 32 bytes first, so the compare runs
    /// the same number of iterations for every request whatever the caller
    /// sent. It also owns the header parse, which is what stops the three from
    /// disagreeing about a trailing space again.
    ///
    /// An empty stored token denies everything, so a credential store that came
    /// back empty cannot be authorized against.
    /// </summary>
    public static bool Authorize(string? authorizationHeader, string expectedToken) =>
        HyperwhisperCoreMethods.LocalApiAuthorize(authorizationHeader, expectedToken ?? "");

    /// <summary>
    /// 32 random bytes → base64-url, the encoding all three platforms wrote out
    /// separately.
    ///
    /// THE ENTROPY STAYS HERE AND THE ENCODING MOVED (issue #289). The shared
    /// core builds with <c>panic = "abort"</c>, so a random-number failure
    /// inside Rust would abort the whole app; keeping
    /// <c>RandomNumberGenerator</c> on this side is why <c>hw-localapi</c> has
    /// no <c>rand</c> dependency.
    /// </summary>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes((int)HyperwhisperCoreMethods.LocalApiTokenEntropyBytes());
        return HyperwhisperCoreMethods.LocalApiGenerateToken(bytes);
    }

    private static void TryStoreToken(string token)
    {
        try
        {
            var dir = Path.GetDirectoryName(TokenFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var plaintext = Encoding.UTF8.GetBytes(token);
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            AppPaths.ClearReadOnlyAttribute(TokenFilePath, "LocalApiAuth");
            File.WriteAllBytes(TokenFilePath, encrypted);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiAuth: failed to persist token: {ex.Message}");
        }
    }

    private static void TryDeleteToken()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                AppPaths.ClearReadOnlyAttribute(TokenFilePath, "LocalApiAuth");
                File.Delete(TokenFilePath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LocalApiAuth: failed to delete token: {ex.Message}");
        }
    }
}
