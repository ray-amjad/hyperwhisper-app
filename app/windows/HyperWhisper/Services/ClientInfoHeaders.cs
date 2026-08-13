using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// WHICH APP AND WHICH BUILD IS CALLING THE CLOUD
///
/// Every HyperWhisper Cloud request carries the platform and the app version:
///
///   X-HyperWhisper-Platform: windows
///   X-HyperWhisper-Version:  1.8.2
///
/// The backend reads both into its structured log lines
/// (hyperwhisper-cloud/src/lib/client-info.ts), so a regression can be scoped to
/// one platform and one build without asking the user for their version.
///
/// The macOS twin is app/macos/hyperwhisper/Utilities/HyperWhisperClientInfo.swift.
/// Keep the header names and the platform token in step with it.
///
/// These headers are additive: the existing User-Agent stays as it is, because
/// the backend still parses it as the fallback for builds shipped before this.
/// </summary>
internal static class ClientInfoHeaders
{
    /// <summary>Header names agreed with the backend.</summary>
    internal const string PlatformHeaderName = "X-HyperWhisper-Platform";
    internal const string VersionHeaderName = "X-HyperWhisper-Version";

    /// <summary>Platform token. Lowercase — the backend buckets on the exact string.</summary>
    internal const string Platform = "windows";

    /// <summary>
    /// App version, three parts (e.g. 1.8.2). Read once: the assembly version
    /// cannot change while the process runs.
    ///
    /// The backend drops any value with characters outside [A-Za-z0-9._-], so
    /// the fallback stays inside that alphabet.
    /// </summary>
    internal static string Version { get; } = ReadAppVersion();

    private static string ReadAppVersion()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Returns the core-built request with both headers appended. Called per
    /// retry attempt, so it mutates only that attempt's request.
    /// </summary>
    internal static HttpRequest Apply(HttpRequest request)
    {
        // The core hands back a fresh List each build, so appending here cannot
        // leak into another request.
        var headers = new List<Header>(request.@headers)
        {
            new Header(PlatformHeaderName, Platform),
            new Header(VersionHeaderName, Version),
        };
        return request with { @headers = headers };
    }

    /// <summary>Sets both headers on a natively built request.</summary>
    internal static void Apply(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(PlatformHeaderName, Platform);
        request.Headers.TryAddWithoutValidation(VersionHeaderName, Version);
    }

    /// <summary>Carries both headers into a WebSocket upgrade request.</summary>
    internal static void Apply(ClientWebSocket webSocket)
    {
        webSocket.Options.SetRequestHeader(PlatformHeaderName, Platform);
        webSocket.Options.SetRequestHeader(VersionHeaderName, Version);
    }
}
