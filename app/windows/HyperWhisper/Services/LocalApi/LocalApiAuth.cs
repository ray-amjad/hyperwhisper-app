using System.IO;
using System.Security.Cryptography;
using System.Text;

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
    /// Length-stable byte comparison; required because the bearer check is the
    /// only thing standing between a local-network attacker and arbitrary
    /// transcription/post-processing on the user's API keys.
    /// </summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a ?? "");
        var bBytes = Encoding.UTF8.GetBytes(b ?? "");
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    // 32 random bytes → base64-url. Strips `=` padding and swaps `+/` for `-_`.
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("=", "", StringComparison.Ordinal)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
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
