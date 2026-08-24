using System.Security.Cryptography;
using System.Text;
using HyperWhisper.Platform.Abstractions;

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

        token = Base64Url(RandomNumberGenerator.GetBytes(32));
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
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
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

    public static bool FixedTimeEquals(string supplied, string expected)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static bool IsValidToken(string? value) => value is { Length: 43 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
