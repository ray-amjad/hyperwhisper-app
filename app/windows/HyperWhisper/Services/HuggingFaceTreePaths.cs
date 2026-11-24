using System.IO;

namespace HyperWhisper.Services;

/// <summary>
/// Guards for a model downloaded from a Hugging Face repo tree (Qwen3). The tree
/// API's file paths and its <c>Link: rel="next"</c> URL come from the network, so
/// neither is trusted: every path must stay inside the model folder and every
/// page must stay on the same tree endpoint.
///
/// Ported from the Linux head's <c>PortableModelManager</c>
/// (<c>ValidateRelativePath</c> / <c>Contained</c> / the tree pagination pin),
/// with the Windows path forms that check alone does not reject on Linux:
/// a drive letter (<c>C:/x</c>), a UNC or device prefix (<c>//host/share</c>,
/// <c>\\?\</c>), and an NTFS alternate data stream (<c>tokenizer:ads</c>).
/// </summary>
internal static class HuggingFaceTreePaths
{
    private const string Host = "huggingface.co";

    /// <summary>Most tree pages one download follows (the Linux head's limit).</summary>
    internal const int MaximumTreePages = 32;

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> unless <paramref name="path"/> is a
    /// plain repo-relative path: no root, no drive, no ':' anywhere, no leading
    /// '/' or '\', and no empty, '.', '..' or Windows-trimmed (trailing '.' or
    /// space) segment, with either separator.
    /// </summary>
    internal static void ValidateRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains(':')
            || path.Contains('\0')
            || path[0] is '/' or '\\'
            || Path.IsPathRooted(path)
            || path.Split('/', '\\').Any(segment =>
                segment is "" or "." or ".."
                || segment.EndsWith('.')
                || segment.EndsWith(' ')))
        {
            throw new InvalidDataException("Model artifact path is invalid.");
        }
    }

    /// <summary>
    /// The full path of <paramref name="relative"/> under <paramref name="root"/>,
    /// after <see cref="ValidateRelativePath"/>. Throws when the resolved path is
    /// not strictly inside <paramref name="root"/>.
    /// </summary>
    internal static string Contained(string root, string relative)
    {
        ValidateRelativePath(relative);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.Ordinal))
            throw new InvalidDataException("Model path escapes the model directory.");
        return candidate;
    }

    /// <summary>The first tree page for <paramref name="repo"/>.</summary>
    internal static Uri FirstTreePage(string repo) =>
        new($"https://{Host}/api/models/{repo}/tree/main?recursive=true");

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> unless <paramref name="page"/> is
    /// https on huggingface.co:443 at exactly this repo's tree path, has not been
    /// fetched before, and is within <see cref="MaximumTreePages"/>. Call
    /// <see cref="ValidateTreeResponse"/> on the response too.
    /// </summary>
    internal static void ValidateTreePage(Uri page, string repo, int pageNumber, ISet<string> visited)
    {
        if (pageNumber > MaximumTreePages)
            throw new InvalidDataException("Model repository pagination exceeded the allowed limit.");

        if (!IsTreeEndpoint(page, repo) || !visited.Add(page.AbsoluteUri))
            throw new InvalidDataException("Model repository pagination was invalid.");
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> unless the URI a tree page was
    /// actually served from (<c>response.RequestMessage.RequestUri</c>, which is
    /// the last hop after any redirect the client followed) is still this repo's
    /// tree endpoint. <see cref="ValidateTreePage"/> only checks the URL asked for.
    /// </summary>
    internal static void ValidateTreeResponse(Uri servedFrom, string repo)
    {
        if (!IsTreeEndpoint(servedFrom, repo))
            throw new InvalidDataException("Model repository tree was served from an unexpected location.");
    }

    private static bool IsTreeEndpoint(Uri page, string repo) =>
        page.IsAbsoluteUri
        && page.Scheme == Uri.UriSchemeHttps
        && page.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)
        && page.Port == 443
        && string.IsNullOrEmpty(page.UserInfo)
        && page.AbsolutePath.Equals($"/api/models/{repo}/tree/main", StringComparison.Ordinal);

    /// <summary>
    /// The <c>/resolve/main/</c> URL of a validated repo path, each segment escaped
    /// so a '?', '#' or '%' in a name cannot change the request.
    /// </summary>
    internal static string ResolveUrl(string repo, string relativePath) =>
        $"https://{Host}/{repo}/resolve/main/" +
        string.Join('/', relativePath.Split('/', '\\').Select(Uri.EscapeDataString));
}
