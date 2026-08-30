using System.Security.Cryptography;
using HyperWhisper.Platform.Abstractions;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.LocalApi;

public sealed class LocalApiTokenStore(IPrivateFileService privateFiles, string tokenPath)
{
    public string LoadOrCreate()
    {
        var existing = privateFiles.ReadAllText(tokenPath);
        if (existing.IsFailure)
        {
            throw new InvalidOperationException("Unable to read the Local API credential store.");
        }

        var token = existing.Value?.Trim();
        if (IsValidToken(token))
        {
            var restricted = privateFiles.IsRestrictedToCurrentUser(tokenPath);
            if (restricted.IsFailure || restricted.Value != true)
            {
                throw new InvalidOperationException("The Local API credential file is not private.");
            }
            return token!;
        }

        token = NewToken();
        var written = privateFiles.WriteAllTextAtomically(tokenPath, token);
        if (written.IsFailure)
        {
            throw new InvalidOperationException("Unable to persist the Local API credential.");
        }
        var verified = privateFiles.IsRestrictedToCurrentUser(tokenPath);
        if (verified.IsFailure || verified.Value != true)
        {
            _ = privateFiles.Delete(tokenPath);
            throw new InvalidOperationException("The Local API credential file could not be restricted to the current user.");
        }
        return token;
    }

    public string Regenerate()
    {
        var token = NewToken();
        var written = privateFiles.WriteAllTextAtomically(tokenPath, token);
        if (written.IsFailure)
            throw new InvalidOperationException("Unable to replace the Local API credential.");
        var verified = privateFiles.IsRestrictedToCurrentUser(tokenPath);
        if (verified.IsFailure || verified.Value != true)
        {
            _ = privateFiles.Delete(tokenPath);
            throw new InvalidOperationException("The replacement Local API credential file could not be restricted to the current user.");
        }
        return token;
    }

    /// <summary>
    /// Whether an <c>Authorization</c> header presents the stored token.
    ///
    /// THE PARSE AND THE COMPARE LIVE IN RUST (issue #289). All three platforms
    /// did this differently, and this head split the work in two: the caller
    /// matched the <c>Bearer </c> prefix itself and passed the remainder here.
    /// The header parse is part of the decision, so it moved across with the
    /// compare — that is what stops the three from disagreeing about a trailing
    /// space again.
    ///
    /// An empty stored token denies everything, so a credential store that came
    /// back empty cannot be authorized against.
    /// </summary>
    public static bool Authorize(string? authorizationHeader, string expectedToken) =>
        HyperwhisperCoreMethods.LocalApiAuthorize(authorizationHeader, expectedToken ?? "");

    /// <summary>
    /// Whether a stored string has the shape of a Local API token: 43 URL-safe
    /// base64 characters, no padding.
    /// </summary>
    private static bool IsValidToken(string? value) =>
        value is not null && HyperwhisperCoreMethods.LocalApiIsWellFormedToken(value);

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
    private static string NewToken() => HyperwhisperCoreMethods.LocalApiGenerateToken(
        RandomNumberGenerator.GetBytes((int)HyperwhisperCoreMethods.LocalApiTokenEntropyBytes()));
}
