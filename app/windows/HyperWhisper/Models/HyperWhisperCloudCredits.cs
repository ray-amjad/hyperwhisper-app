// HYPERWHISPER CLOUD CREDITS MODEL
// Represents the user's credit balance and account status from HyperWhisper Cloud.
//
// CREDIT SYSTEM:
// - 1 credit = $0.001 USD (micro-dollar precision)
// - Default transcription cost: ~5.5 credits per audio minute (Deepgram Nova-3)
// - Trial users: 100 credits (~18 minutes on the default tier) + IP rate limiting
// - Licensed users: Credits purchased via Polar meters (pay-as-you-go)
//
// API ENDPOINT: GET /usage?identifier=<device_id_or_license_key>
//
// RESPONSE FORMAT:
// {
//   "credits_remaining": 150.5,
//   "minutes_remaining": 24,
//   "credits_per_minute": 6.3,
//   "is_licensed": false,
//   "is_anonymous": true,
//   "resets_at": "2024-01-16T00:00:00Z",
//   "customer_id": null,
//   "message": null
// }
//
// ACCOUNT TYPES:
// - Licensed: Has valid Polar license, unlimited usage (billed per use)
// - Trial: Uses device credits, limited balance
// - Anonymous: IP-based rate limiting, daily reset

using System;
using System.Text.Json.Serialization;

namespace HyperWhisper.Models;

/// <summary>
/// Represents HyperWhisper Cloud credit balance and account status.
/// </summary>
public class HyperWhisperCloudCredits
{
    // =========================================================================
    // CREDIT BALANCE
    // =========================================================================

    /// <summary>
    /// Current credit balance.
    /// 1 credit = $0.001 USD (micro-dollar precision).
    /// </summary>
    [JsonPropertyName("credits_remaining")]
    public double CreditsRemaining { get; set; }

    /// <summary>
    /// Estimated minutes of transcription remaining.
    /// Calculated server-side based on typical audio characteristics.
    /// </summary>
    [JsonPropertyName("minutes_remaining")]
    public int MinutesRemaining { get; set; }

    /// <summary>
    /// Current cost per audio minute in credits.
    /// Varies based on backend provider costs.
    /// Default Deepgram Nova-3 pricing is ~5.5 credits/minute.
    /// </summary>
    [JsonPropertyName("credits_per_minute")]
    public double CreditsPerMinute { get; set; }

    // =========================================================================
    // ACCOUNT STATUS
    // =========================================================================

    /// <summary>
    /// Whether the user has a valid Polar license.
    /// Licensed users have pay-as-you-go billing instead of device credits.
    /// </summary>
    [JsonPropertyName("is_licensed")]
    public bool IsLicensed { get; set; }

    /// <summary>
    /// Whether the user is anonymous (IP-based rate limiting).
    /// Anonymous users have daily reset of quota.
    /// </summary>
    [JsonPropertyName("is_anonymous")]
    public bool IsAnonymous { get; set; }

    /// <summary>
    /// When the daily quota resets (anonymous users only).
    /// Null for licensed/trial users.
    /// </summary>
    [JsonPropertyName("resets_at")]
    public DateTime? ResetsAt { get; set; }

    /// <summary>
    /// Polar customer ID (licensed users only).
    /// Useful for account management and support.
    /// </summary>
    [JsonPropertyName("customer_id")]
    public string? CustomerId { get; set; }

    /// <summary>
    /// Optional server message (e.g., promotional info, warnings).
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    // =========================================================================
    // COMPUTED PROPERTIES
    // =========================================================================

    /// <summary>
    /// Formatted balance string for display.
    /// Example: "$0.15 remaining (~24 minutes)"
    /// </summary>
    [JsonIgnore]
    public string FormattedBalance
    {
        get
        {
            var dollars = CreditsRemaining / 1000.0;
            return $"${dollars:F2} remaining (~{MinutesRemaining} minutes)";
        }
    }

    /// <summary>
    /// Short formatted balance for compact display.
    /// Example: "~24 min"
    /// </summary>
    [JsonIgnore]
    public string ShortFormattedBalance => $"~{MinutesRemaining} min";

    /// <summary>
    /// Whether credits are completely exhausted.
    /// </summary>
    [JsonIgnore]
    public bool IsExhausted => CreditsRemaining <= 0;

    /// <summary>
    /// Whether credits are running low (less than 10 minutes remaining).
    /// </summary>
    [JsonIgnore]
    public bool IsLow => MinutesRemaining < 10 && MinutesRemaining > 0;

    /// <summary>
    /// Dollar equivalent of current balance.
    /// </summary>
    [JsonIgnore]
    public double DollarBalance => CreditsRemaining / 1000.0;

    /// <summary>
    /// Account type display string.
    /// </summary>
    [JsonIgnore]
    public string AccountType
    {
        get
        {
            if (IsLicensed) return "Licensed";
            if (IsAnonymous) return "Anonymous";
            return "Trial";
        }
    }

    /// <summary>
    /// Formatted reset time for anonymous users.
    /// Example: "Resets at 12:00 AM"
    /// </summary>
    [JsonIgnore]
    public string? FormattedResetTime
    {
        get
        {
            if (ResetsAt == null) return null;
            var local = ResetsAt.Value.ToLocalTime();
            return $"Resets at {local:h:mm tt}";
        }
    }

    // =========================================================================
    // FACTORY METHODS
    // =========================================================================

    /// <summary>
    /// Creates an empty/unknown credits object for error states.
    /// </summary>
    public static HyperWhisperCloudCredits Empty()
    {
        return new HyperWhisperCloudCredits
        {
            CreditsRemaining = 0,
            MinutesRemaining = 0,
            CreditsPerMinute = 6.3, // Default estimate
            IsLicensed = false,
            IsAnonymous = false
        };
    }
}
