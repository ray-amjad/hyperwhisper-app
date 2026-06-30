// LICENSE VALIDATION RESULT
// Represents the response from the license validation API.
//
// API ENDPOINT: POST /api/license/validate
// REQUEST: { license_key: string, device_id: string }
// RESPONSE: {
//   valid: boolean,
//   status: "active" | "expired" | "revoked" | "invalid",
//   customer_id?: string,
//   customer_email?: string,
//   subscription_id?: string,
//   expires_at?: string (ISO 8601),
//   error?: string
// }
//
// CACHING:
// - Success responses are cached locally for 24 hours
// - This allows offline usage with a 7-day grace period
// - Cache metadata is stored in %LOCALAPPDATA%\HyperWhisper\license.json
// - Raw license key is stored in Windows Credential Manager

using System;
using System.Text.Json.Serialization;

namespace HyperWhisper.Models;

/// <summary>
/// Represents the result of a license validation request.
/// </summary>
public class LicenseValidationResult
{
    /// <summary>
    /// Whether the license is valid and active.
    /// </summary>
    [JsonPropertyName("valid")]
    public bool IsValid { get; set; }

    /// <summary>
    /// The parsed license status from the API response.
    /// </summary>
    [JsonIgnore]
    public LicenseStatus Status { get; set; } = LicenseStatus.Trial;

    /// <summary>
    /// Raw status string from the API (e.g., "active", "expired", "revoked").
    /// </summary>
    [JsonPropertyName("status")]
    public string? RawStatus { get; set; }

    /// <summary>
    /// Polar customer ID (for analytics/support).
    /// </summary>
    [JsonPropertyName("customer_id")]
    public string? CustomerId { get; set; }

    /// <summary>
    /// Customer's email address (for display in UI).
    /// </summary>
    [JsonPropertyName("customer_email")]
    public string? CustomerEmail { get; set; }

    /// <summary>
    /// Polar subscription ID.
    /// </summary>
    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; set; }

    /// <summary>
    /// When the license expires (for subscriptions).
    /// </summary>
    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    [JsonPropertyName("error")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When this validation result was fetched (for cache expiry).
    /// </summary>
    [JsonPropertyName("validated_at")]
    public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Parses the raw status string into a LicenseStatus enum.
    /// Call this after deserialization.
    /// </summary>
    public void ParseStatus()
    {
        Status = RawStatus?.ToLowerInvariant() switch
        {
            "active" => LicenseStatus.Active,
            "expired" => LicenseStatus.Expired,
            "revoked" => LicenseStatus.Invalid,
            "invalid" => LicenseStatus.Invalid,
            _ => IsValid ? LicenseStatus.Active : LicenseStatus.Invalid
        };
    }

    /// <summary>
    /// Creates a result for a successful validation.
    /// </summary>
    public static LicenseValidationResult Success(string? customerId = null, string? email = null)
    {
        return new LicenseValidationResult
        {
            IsValid = true,
            Status = LicenseStatus.Active,
            RawStatus = "active",
            CustomerId = customerId,
            CustomerEmail = email,
            ValidatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a result for a failed validation.
    /// </summary>
    public static LicenseValidationResult Failed(string errorMessage, LicenseStatus status = LicenseStatus.Invalid)
    {
        return new LicenseValidationResult
        {
            IsValid = false,
            Status = status,
            RawStatus = status.ToString().ToLowerInvariant(),
            ErrorMessage = errorMessage,
            ValidatedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Cached license information persisted to disk.
/// Enables offline license validation with grace period.
/// </summary>
public class CachedLicenseInfo
{
    /// <summary>
    /// Legacy JSON-stored license key. New writes keep this null because the
    /// raw key is stored in Windows Credential Manager.
    /// </summary>
    [JsonPropertyName("license_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LicenseKey { get; set; }

    /// <summary>
    /// The last successful validation result.
    /// </summary>
    [JsonPropertyName("validation_result")]
    public LicenseValidationResult? ValidationResult { get; set; }

    /// <summary>
    /// When the license was last successfully validated online.
    /// </summary>
    [JsonPropertyName("last_online_validation")]
    public DateTime LastOnlineValidation { get; set; }

    /// <summary>
    /// Whether the cached data is still within the offline grace period.
    /// Default: 7 days
    /// </summary>
    [JsonIgnore]
    public bool IsWithinGracePeriod =>
        LastOnlineValidation.AddDays(7) > DateTime.UtcNow;

    /// <summary>
    /// Whether the cached data needs revalidation (older than 24 hours).
    /// </summary>
    [JsonIgnore]
    public bool NeedsRevalidation =>
        LastOnlineValidation.AddHours(24) < DateTime.UtcNow;
}
