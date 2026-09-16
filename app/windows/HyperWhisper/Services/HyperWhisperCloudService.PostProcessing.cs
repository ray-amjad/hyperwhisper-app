// HYPERWHISPER CLOUD SERVICE — POST-PROCESSING
// The POST /post-process endpoint for AI text correction. A standalone
// endpoint, separate from transcription, and still native HttpClient I/O with
// its own retry loop (the transcribe path uses the core's retry instead).

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using HyperWhisper.Configuration;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public partial class HyperWhisperCloudService
{
    /// <summary>
    /// Calls the /post-process endpoint for AI text correction.
    /// This is a standalone endpoint separate from transcription.
    /// Matches macOS implementation in HyperWhisperCloudProvider.performPostProcess().
    /// </summary>
    /// <param name="text">Raw transcription text to correct.</param>
    /// <param name="prompt">System prompt for AI processing instructions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>AI-corrected text.</returns>
    /// <exception cref="TranscriptionException">Thrown on API errors.</exception>
    public async Task<string> PostProcessAsync(
        string text,
        string prompt,
        string? llmProviderHeader = null,
        string? llmModelHeader = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Get fresh credentials at request time (matches macOS behavior)
        var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();

        LoggingService.Info("========== HYPERWHISPER CLOUD POST-PROCESS ==========");
        LoggingService.Info($"  Auth: {(isLicensed ? "License Key" : "Device Credits")}");
        LoggingService.Info($"  Text length: {text.Length} chars");
        LoggingService.Debug($"  Prompt length: {prompt.Length} chars");

        // Build JSON body with fresh credentials
        var body = new Dictionary<string, string>
        {
            ["text"] = text,
            ["prompt"] = prompt
        };

        // Add authentication using fresh credentials
        if (isLicensed)
        {
            body["license_key"] = identifier;
        }
        else
        {
            body["device_id"] = identifier;
        }

        var jsonBody = JsonSerializer.Serialize(body);

        // Send request with retry logic
        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                LoggingService.Info($"  Attempt {attempt}/{MaxRetries}...");

                using var request = CreateRequest(HttpMethod.Post, NetworkConfig.PostProcessEndpoint);
                // Fresh content per attempt: `using var request` disposes its Content,
                // so a shared instance would be disposed after attempt 1 and the next
                // SendAsync would throw ObjectDisposedException — killing retries.
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                if (!string.IsNullOrEmpty(llmProviderHeader))
                {
                    request.Headers.TryAddWithoutValidation("X-LLM-Provider", llmProviderHeader);
                }
                if (!string.IsNullOrEmpty(llmModelHeader))
                {
                    request.Headers.TryAddWithoutValidation("X-LLM-Model", llmModelHeader);
                }

                // Snapshot the current client so a concurrent rebuild can't dispose it mid-flight.
                var client = Volatile.Read(ref _httpClient);
                var response = await client.SendAsync(request, cancellationToken);
                LoggingService.Debug($"  Post-process response: status={(int)response.StatusCode} · httpVersion={response.Version}");

                if (!response.IsSuccessStatusCode)
                {
                    await HandleErrorResponseAsync(response, cancellationToken);
                }

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(responseJson);

                // Extract corrected text
                if (doc.RootElement.TryGetProperty("corrected", out var correctedElement))
                {
                    var corrected = correctedElement.GetString();

                    // Log cost if present
                    if (doc.RootElement.TryGetProperty("cost", out var costElement))
                    {
                        if (costElement.TryGetProperty("credits", out var creditsEl))
                        {
                            LoggingService.Info($"  Post-process credits used: {creditsEl.GetDouble():F1}");
                        }
                    }

                    LoggingService.Info("========== POST-PROCESS COMPLETE ==========");
                    LoggingService.Info($"  Output length: {corrected?.Length ?? 0} chars");

                    return corrected ?? text;
                }

                throw new TranscriptionException(
                    TranscriptionErrorCode.InvalidRequest,
                    "Invalid response format from post-process endpoint",
                    "HyperWhisper Cloud");
            }
            catch (TranscriptionException ex) when (ex.Code == TranscriptionErrorCode.RateLimited && attempt < MaxRetries)
            {
                var delay = ex.RetryAfterSeconds ?? (int)Math.Pow(2, attempt);
                LoggingService.Warn($"  Rate limited, waiting {delay}s before retry...");
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                lastException = ex;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                var delay = (int)Math.Pow(2, attempt);
                LoggingService.Warn($"  Network error: {ex.Message}, retrying in {delay}s...");
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                lastException = ex;
            }
        }

        throw lastException ?? new TranscriptionException(
            TranscriptionErrorCode.Unknown,
            "Post-processing failed after max retries",
            "HyperWhisper Cloud");
    }
}
