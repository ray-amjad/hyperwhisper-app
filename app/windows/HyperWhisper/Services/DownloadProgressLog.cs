namespace HyperWhisper.Services;

/// <summary>
/// Decides when a model download writes its "Download progress" log line: once
/// each time the percent enters a new 10% band (0, 10, 20 ... 100). A download
/// reads an 80 KB buffer at a time, so a rule that logs while the percent sits on
/// a multiple of 10 wrote one line per buffer, thousands per download (#1600).
/// </summary>
internal static class DownloadProgressLog
{
    /// <summary>The band value to start a download with: no band logged yet.</summary>
    public const int NoBandLogged = -1;

    /// <summary>
    /// True when <paramref name="currentPercent"/> is in a band not logged yet, and
    /// then records that band in <paramref name="lastLoggedBand"/>.
    /// </summary>
    public static bool ShouldLog(int currentPercent, ref int lastLoggedBand)
    {
        var band = currentPercent / 10;
        if (band == lastLoggedBand)
        {
            return false;
        }

        lastLoggedBand = band;
        return true;
    }
}
