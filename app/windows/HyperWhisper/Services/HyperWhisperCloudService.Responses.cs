// HYPERWHISPER CLOUD SERVICE — RESPONSES
// Reads what a cloud response carries: the credit headers, a single header
// value, and the mapping from a non-2xx body to a TranscriptionException.
// Both response shapes live here — the binding HttpResponse captured by the
// core executor (transcribe path) and the native HttpResponseMessage
// (post-process path).

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HwTranscriptionException / HttpResponse collide
// with System types; qualify uniffi.hyperwhisper_core.HttpResponse below.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

public partial class HyperWhisperCloudService
{
    /// <summary>
    /// Map a non-2xx HW-Cloud response into a TranscriptionException, enriching the
    /// 402 credit context + 413 size context from the body. Called by the retry
    /// wrapper on give-up.
    /// </summary>
    private static TranscriptionException MapCloudError(uniffi.hyperwhisper_core.HttpResponse resp)
    {
        try
        {
            HyperwhisperCoreMethods.HyperwhisperCloudParseTranscribeResponse(resp);
            return new TranscriptionException(
                TranscriptionErrorCode.Unknown, "Unexpected non-error response", "HyperWhisper Cloud", (int)resp.@status);
        }
        catch (HwTranscriptionException ex)
        {
            var (remaining, required) = RustCoreMapping.CreditContext(resp);
            var (tooBigBytes, tooBigLimit) = RustCoreMapping.FileTooLargeContext(resp);
            return RustCoreMapping.MapTranscriptionError(
                ex,
                "HyperWhisper Cloud",
                httpStatusCode: (int)resp.@status,
                insufficientCredits: resp.@status == 402,
                creditsRemaining: remaining,
                creditsRequired: required,
                fileTooLargeBytes: tooBigBytes,
                fileTooLargeLimit: tooBigLimit);
        }
    }

    /// <summary>Read a single header value from a captured binding response.</summary>
    private static string? HeaderValue(uniffi.hyperwhisper_core.HttpResponse response, string headerName)
    {
        foreach (var header in response.@headers)
        {
            if (string.Equals(header.@name, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return header.@value;
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts credit information from the captured binding response headers
    /// (transcribe path — response captured by the executor).
    /// </summary>
    private void ExtractCreditHeaders(uniffi.hyperwhisper_core.HttpResponse response)
    {
        var used = HeaderValue(response, "X-Credits-Used");
        if (int.TryParse(used, out var usedVal))
        {
            _lastCreditsUsed = usedVal;
        }

        var remaining = HeaderValue(response, "X-Device-Credits-Remaining");
        if (int.TryParse(remaining, out var remainingVal))
        {
            _remainingCredits = remainingVal;
        }
    }

    /// <summary>
    /// Extracts credit information from a raw <see cref="HttpResponseMessage"/>
    /// (post-process path — still native HttpClient I/O, kept out of the core).
    /// </summary>
    private void ExtractCreditHeaders(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Credits-Used", out var usedValues)
            && int.TryParse(usedValues.FirstOrDefault(), out var used))
        {
            _lastCreditsUsed = used;
        }

        if (response.Headers.TryGetValues("X-Device-Credits-Remaining", out var remainingValues)
            && int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
        {
            _remainingCredits = remaining;
        }
    }

    /// <summary>
    /// Handles error responses from HyperWhisper Cloud API.
    /// </summary>
    private async Task HandleErrorResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        LoggingService.Error($"HyperWhisper Cloud API error: {statusCode} · httpVersion={response.Version}");
        LoggingService.Error($"  Response: {responseBody}");

        // Extract credit info even from error responses
        ExtractCreditHeaders(response);

        // Try to parse error message
        string? errorMessage = null;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                errorMessage = errorElement.GetString();
            }
            else if (doc.RootElement.TryGetProperty("message", out var msgElement))
            {
                errorMessage = msgElement.GetString();
            }
        }
        catch { }

        // Map status code to error type
        // HyperWhisper Cloud specific codes: 401 (no auth), 402 (no credits), 429 (rate limit)
        var (code, message) = (HttpStatusCode)statusCode switch
        {
            HttpStatusCode.Unauthorized => (
                TranscriptionErrorCode.Unauthorized,
                errorMessage ?? "No device ID or invalid license key"),

            (HttpStatusCode)402 => (
                TranscriptionErrorCode.QuotaExceeded,
                errorMessage ?? $"Insufficient credits. Remaining: {_remainingCredits ?? 0}"),

            HttpStatusCode.TooManyRequests => (
                TranscriptionErrorCode.RateLimited,
                errorMessage ?? "IP rate limit exceeded. Try again later."),

            HttpStatusCode.BadRequest => (
                TranscriptionErrorCode.InvalidRequest,
                errorMessage ?? "Invalid request"),

            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout => (
                TranscriptionErrorCode.ProviderUnavailable,
                errorMessage ?? "HyperWhisper Cloud service unavailable"),

            _ => (TranscriptionErrorCode.Unknown, errorMessage ?? $"HTTP {statusCode}")
        };

        // Get retry-after header if present
        int? retryAfter = null;
        if (response.Headers.TryGetValues("Retry-After", out var retryValues))
        {
            if (int.TryParse(retryValues.FirstOrDefault(), out var seconds))
            {
                retryAfter = seconds;
            }
        }

        throw new TranscriptionException(code, message, "HyperWhisper Cloud", statusCode, retryAfter);
    }
}
