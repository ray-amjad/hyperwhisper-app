// NETWORK CONFIGURATION
// Central configuration for all network-related settings and API endpoints.
// Uses conditional compilation to switch between dev/prod environments.
//
// ENVIRONMENT SWITCHING:
// - DEBUG builds: Use development endpoints (transcribe-dev-v1)
// - RELEASE builds: Use production endpoints (transcribe-prod-v2)
//
// This mirrors the macOS implementation in NetworkConfig.swift which uses
// #if DEBUG conditional compilation for the same purpose.

namespace HyperWhisper.Configuration;

/// <summary>
/// Network configuration for HyperWhisper API communication.
/// Automatically switches between development and production based on build configuration.
/// </summary>
public static class NetworkConfig
{
    // =========================================================================
    // HYPERWHISPER CLOUD ENDPOINTS
    // =========================================================================

    /// <summary>
    /// Base URL for HyperWhisper Cloud transcription service.
    /// Switches between dev/prod based on DEBUG compilation symbol.
    /// </summary>
    public static string HyperWhisperCloudBaseUrl =>
#if DEBUG
        "https://transcribe-dev-v2.hyperwhisper.com";
#else
        "https://transcribe-prod-v2.hyperwhisper.com";
#endif

    /// <summary>
    /// Full transcription endpoint URL.
    /// POST /transcribe - Binary audio streaming with query params.
    /// </summary>
    public static string TranscribeEndpoint => $"{HyperWhisperCloudBaseUrl}/transcribe";

    /// <summary>
    /// Usage/credits endpoint URL.
    /// GET /usage - Fetch credit balance.
    /// </summary>
    public static string UsageEndpoint => $"{HyperWhisperCloudBaseUrl}/usage";

    /// <summary>
    /// Post-processing endpoint URL.
    /// POST /post-process - AI text correction.
    /// </summary>
    public static string PostProcessEndpoint => $"{HyperWhisperCloudBaseUrl}/post-process";

    // =========================================================================
    // ENVIRONMENT DETECTION
    // =========================================================================

    /// <summary>
    /// Whether the app is running in development mode.
    /// </summary>
    public static bool IsDevelopment =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Current environment name for logging.
    /// </summary>
    public static string EnvironmentName => IsDevelopment ? "Development" : "Production";
}
