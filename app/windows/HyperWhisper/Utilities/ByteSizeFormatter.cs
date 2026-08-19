namespace HyperWhisper.Utilities;

/// <summary>
/// Formats byte counts into human-readable strings.
/// </summary>
public static class ByteSizeFormatter
{
    /// <summary>
    /// Formats bytes as "{size:F2} {suffix}" using B/KB/MB/GB suffixes.
    /// </summary>
    public static string FormatDecimal(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:F2} {suffixes[suffixIndex]}";
    }

    /// <summary>
    /// Formats bytes as GB (1 decimal), MB (0 decimals), or KB (0 decimals) - whichever tier fits.
    /// </summary>
    public static string FormatCompact(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F0} MB";
        }

        return $"{bytes / 1024.0:F0} KB";
    }
}
