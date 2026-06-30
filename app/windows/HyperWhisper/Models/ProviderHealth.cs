// PROVIDER HEALTH STATUS ENUM
// Tracks the health/availability status of cloud providers based on API key validation.
// Used by CloudProviderHealthService to cache and display provider status in Settings UI.

namespace HyperWhisper.Models;

/// <summary>
/// Health status of a cloud provider's API key and connectivity.
/// Used to show status indicators (green/yellow/red/gray) in the Settings UI.
/// </summary>
public enum ProviderHealth
{
    /// <summary>
    /// Status has not been checked yet.
    /// Displayed as gray indicator in UI.
    /// </summary>
    Unknown,

    /// <summary>
    /// Health check is currently in progress.
    /// Displayed as spinning/pulsing indicator in UI.
    /// </summary>
    Checking,

    /// <summary>
    /// API key is valid and provider is reachable.
    /// Displayed as green indicator in UI.
    /// </summary>
    Healthy,

    /// <summary>
    /// API key is invalid or expired (HTTP 401/403).
    /// Displayed as red indicator in UI.
    /// User should update their API key.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// Provider endpoint is unreachable (network error or 5xx).
    /// Displayed as yellow/orange indicator in UI.
    /// May be a temporary issue - retry later.
    /// </summary>
    Unreachable
}

/// <summary>
/// Extension methods for ProviderHealth enum.
/// </summary>
public static class ProviderHealthExtensions
{
    /// <summary>
    /// Gets a user-friendly status message for the health state.
    /// </summary>
    public static string GetStatusMessage(this ProviderHealth health) => health switch
    {
        ProviderHealth.Unknown => "Not checked",
        ProviderHealth.Checking => "Checking...",
        ProviderHealth.Healthy => "Connected",
        ProviderHealth.Unauthorized => "Invalid API key",
        ProviderHealth.Unreachable => "Service unreachable",
        _ => "Unknown"
    };

    /// <summary>
    /// Determines if the health status indicates a working configuration.
    /// </summary>
    public static bool IsOperational(this ProviderHealth health) =>
        health == ProviderHealth.Healthy;

    /// <summary>
    /// Determines if the health status indicates an error that requires user action.
    /// </summary>
    public static bool RequiresUserAction(this ProviderHealth health) =>
        health == ProviderHealth.Unauthorized;
}
