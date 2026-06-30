// HYPERWHISPER ROUTED TRANSCRIPTION CLIENT
// Shared HTTP client for providers that route through the Fly transcribe
// service but pin a specific upstream via the `X-STT-Provider` header.
//
// Used by:
//  - AzureMAITranscriptionService (X-STT-Provider: azure-mai)
//  - GoogleChirpTranscriptionService (X-STT-Provider: google-chirp)
//
// Same /transcribe contract as HyperWhisperCloudService — same query params,
// same response shape, same 402/429/401/413 mapping.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using HyperWhisper.Configuration;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HttpRequest / HttpResponse / HwTranscript /
// HwTranscriptionException collide with System.Net.Http; qualify with
// uniffi.hyperwhisper_core. where ambiguous.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

internal static class HyperWhisperRoutedTranscriptionClient
{
    private const int DefaultTimeoutSeconds = 180;

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".wav", "audio/wav" },
        { ".mp3", "audio/mpeg" },
        { ".mp4", "audio/mp4" },
        { ".m4a", "audio/mp4" },
        { ".mpeg", "audio/mpeg" },
        { ".mpga", "audio/mpeg" },
        { ".webm", "audio/webm" },
        { ".ogg", "audio/ogg" },
        { ".flac", "audio/flac" }
    };

    /// <summary>
    /// Process-wide shared HttpClient for HW-Cloud-routed providers
    /// (Azure-MAI, Google-Chirp). One pooled SocketsHttpHandler across
    /// all routed services avoids paying TCP+TLS per provider and lets
    /// HTTP/2 multiplex across them to transcribe.hyperwhisper.com.
    /// </summary>
    public static readonly HttpClient SharedClient = CreateHttpClient();

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds),
        };
    }

    public static async Task<string> TranscribeAsync(
        HttpClient client,
        string sttProviderHeader,
        string providerDisplayName,
        string audioPath,
        string? language,
        IReadOnlyList<string>? vocabulary,
        CancellationToken cancellationToken,
        string? model = null,
        string? domain = null)
    {
        // WAVE 3 / Win-2: URL / header / vocabulary construction and request/
        // response handling now run through the Rust shared core's routed
        // builders (AzureMaiBuildTranscribeRequest / GoogleChirpBuildTranscribeRequest
        // and their parsers), which bake the X-STT-Provider header, pass through
        // X-STT-Model / X-STT-Domain, build the query (license_key/device_id,
        // language, initial_prompt), and encode the @raw raw-stream body. This
        // file keeps only the platform-owned I/O shell: the shared HttpClient, the
        // executor + core retry loop, file preflight, and cancellation.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var totalSw = Stopwatch.StartNew();

        var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();

        LoggingService.Info($"========== HW-ROUTED TRANSCRIPTION ({sttProviderHeader}) ==========");
        LoggingService.Info($"  Auth: {(isLicensed ? "License Key" : "Device Credits")}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");

        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                providerDisplayName);
        }

        var extension = Path.GetExtension(audioPath);
        var contentType = MimeTypes.GetValueOrDefault(extension, "audio/wav");

        // Pass the RAW term list — the core builds the CSV (trim + drop-empty, no
        // lowercase/dedup) AND owns the per-provider customVocabulary gating
        // (Chirp 3 drops initial_prompt server-side), so we no longer gate here.
        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: vocabulary ?? Array.Empty<string>(),
            // Core appends the `/transcribe` path itself — pass the BASE, not the
            // full endpoint, or the path would double up.
            baseUrl: NetworkConfig.HyperWhisperCloudBaseUrl,
            licenseKey: isLicensed ? identifier : null,
            deviceId: isLicensed ? null : identifier,
            routedProvider: sttProviderHeader,
            routedModel: string.IsNullOrEmpty(model) ? null : model,
            routedDomain: string.IsNullOrEmpty(domain) ? null : domain);

        uniffi.hyperwhisper_core.HttpResponse response;
        try
        {
            response = await RustRetry.PerformAsync(
                client,
                buildRequest: () => BuildRoutedRequest(sttProviderHeader, coreParams),
                parseError: resp => MapRoutedError(sttProviderHeader, providerDisplayName, resp),
                cancellationToken: cancellationToken);
        }
        catch (HwTranscriptionException ex)
        {
            // Thrown by the routed builder (request-build validation).
            throw RustCoreMapping.MapTranscriptionError(ex, providerDisplayName);
        }

        cancellationToken.ThrowIfCancellationRequested();

        HwTranscript transcript;
        try
        {
            transcript = ParseRoutedResponse(sttProviderHeader, response);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, providerDisplayName);
        }

        LoggingService.Info($"  Completed · totalMs={totalSw.ElapsedMilliseconds} · chars={transcript.@text.Length}");
        return transcript.@text;
    }

    /// <summary>
    /// Route to the correct core builder by the pinned X-STT-Provider value. Both
    /// builders force their own provider header; any other value falls back to the
    /// base HyperWhisper Cloud builder (defensive — callers pass only the two
    /// routed values today).
    /// </summary>
    private static uniffi.hyperwhisper_core.HttpRequest BuildRoutedRequest(string sttProviderHeader, TranscribeParams coreParams)
    {
        return sttProviderHeader switch
        {
            "azure-mai" => HyperwhisperCoreMethods.AzureMaiBuildTranscribeRequest(coreParams),
            "google-chirp" => HyperwhisperCoreMethods.GoogleChirpBuildTranscribeRequest(coreParams),
            _ => HyperwhisperCoreMethods.HyperwhisperCloudBuildTranscribeRequest(coreParams)
        };
    }

    /// <summary>Route to the correct core parser by the pinned X-STT-Provider value.</summary>
    private static HwTranscript ParseRoutedResponse(string sttProviderHeader, uniffi.hyperwhisper_core.HttpResponse resp)
    {
        return sttProviderHeader switch
        {
            "azure-mai" => HyperwhisperCoreMethods.AzureMaiParseTranscribeResponse(resp),
            "google-chirp" => HyperwhisperCoreMethods.GoogleChirpParseTranscribeResponse(resp),
            _ => HyperwhisperCoreMethods.HyperwhisperCloudParseTranscribeResponse(resp)
        };
    }

    /// <summary>
    /// Map a non-2xx routed response into a TranscriptionException, enriching the
    /// 402 credit context + 413 size context from the response body (mirrors the
    /// deleted native handleHTTPError). Called by the retry wrapper on give-up.
    /// </summary>
    private static TranscriptionException MapRoutedError(
        string sttProviderHeader, string providerDisplayName, uniffi.hyperwhisper_core.HttpResponse resp)
    {
        try
        {
            ParseRoutedResponse(sttProviderHeader, resp);
            return new TranscriptionException(
                TranscriptionErrorCode.Unknown, "Unexpected non-error response", providerDisplayName, (int)resp.@status);
        }
        catch (HwTranscriptionException ex)
        {
            var (remaining, required) = RustCoreMapping.CreditContext(resp);
            var (tooBigBytes, tooBigLimit) = RustCoreMapping.FileTooLargeContext(resp);
            return RustCoreMapping.MapTranscriptionError(
                ex,
                providerDisplayName,
                httpStatusCode: (int)resp.@status,
                insufficientCredits: resp.@status == 402,
                creditsRemaining: remaining,
                creditsRequired: required,
                fileTooLargeBytes: tooBigBytes,
                fileTooLargeLimit: tooBigLimit);
        }
    }

}
