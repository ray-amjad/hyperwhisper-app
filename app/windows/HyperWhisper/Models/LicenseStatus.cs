// LICENSE STATUS ENUM
// Represents the current state of the user's license.
//
// STATES:
// - Trial: User is on trial, subject to daily transcription limits and model download limits
// - Active: User has a valid, active license - unlimited usage
// - Expired: License was valid but has expired - reverts to trial limits
// - Invalid: License key is malformed or doesn't exist on server
//
// STATE TRANSITIONS:
// Trial -> Active: User enters a valid license key
// Active -> Expired: License expiration date passed, or license deactivated on server
// Active -> Trial: User deactivates their license
// Expired -> Active: User renews or reactivates their license
// Invalid -> Trial: Clearing an invalid license key
// Any -> Invalid: Entering a malformed or non-existent license key

namespace HyperWhisper.Models;

/// <summary>
/// Represents the current state of the user's HyperWhisper license.
/// </summary>
public enum LicenseStatus
{
    /// <summary>
    /// User is on trial mode with usage limits.
    /// - 300 seconds (5 minutes) of transcription per day in production
    /// - 1800 seconds (30 minutes) of transcription per day in debug builds
    /// - Maximum 3 model downloads
    /// </summary>
    Trial,

    /// <summary>
    /// User has a valid, active license.
    /// Unlimited transcription and model downloads.
    /// </summary>
    Active,

    /// <summary>
    /// License was valid but has expired.
    /// User reverts to trial limits until renewal.
    /// </summary>
    Expired,

    /// <summary>
    /// License key is invalid (malformed or doesn't exist).
    /// User should clear and try a different key.
    /// </summary>
    Invalid
}
