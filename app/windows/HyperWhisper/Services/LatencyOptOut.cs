using System.Collections.Generic;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// ANONYMOUS SPEED DATA — THE OPT-OUT HEADER
///
/// HyperWhisper Cloud times each provider call and publishes the aggregate at
/// hyperwhisper.com/en/latency, so people can see which provider is actually
/// fastest from their part of the world before they pick one.
///
/// A user who does not want to be part of that turns off "Share anonymous speed
/// data" in Settings → General. This class turns that setting into the header
/// the backend reads (hyperwhisper-cloud/src/routes/transcribe.ts →
/// isLatencyOptOut).
/// </summary>
internal static class LatencyOptOut
{
    /// <summary>
    /// Header name agreed with the backend. Sent only when the user opted out —
    /// there is no "yes please" header, because sharing is the default.
    /// </summary>
    internal const string HeaderName = "X-Latency-Opt-Out";

    /// <summary>True when the user turned sharing off.</summary>
    internal static bool IsEnabled => !SettingsService.Instance.ShareAnonymousSpeedData;

    /// <summary>
    /// Returns the core-built request with the opt-out header appended when,
    /// and only when, the user asked to be left out. Called per retry attempt,
    /// so it reads the setting fresh and mutates only that attempt's request.
    /// </summary>
    internal static HttpRequest Apply(HttpRequest request)
    {
        if (!IsEnabled)
        {
            return request;
        }

        // The core hands back a fresh List each build, so appending here cannot
        // leak into another request.
        var headers = new List<Header>(request.@headers) { new Header(HeaderName, "1") };
        return request with { @headers = headers };
    }
}
