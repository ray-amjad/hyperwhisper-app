// RUST LICENSE CORE BOUNDARY (Win-3)
//
// TODO-verify (Windows/CI): Rust shared-core swap. UNVERIFIED / compile-only.
//
// Thin boundary between the C# license/usage services and the `hw-license` Rust
// core (via the generated `HyperwhisperCoreMethods` binding). Centralizes:
//   - the single shared KeyValueStore instance,
//   - now-injection,
//   - HwLicenseStatus <-> HyperWhisper.Models.LicenseStatus mapping.
//
// now-INJECTION (confirmed UTC-only for Windows):
//   The native Windows daily-usage reset bucketed by the UTC calendar day
//   (LicenseUsageTracker.CheckDailyReset used `DateTime.UtcNow.Date`). The Rust
//   core buckets by `now_unix_secs / 86400`, which is exactly the UTC day index
//   for a plain UTC `now`. So — UNLIKE macOS, which had to offset `now` by the
//   local GMT offset to preserve its LOCAL-midnight reset — Windows passes PLAIN
//   UTC `now` for BOTH usage and cache calls. No local offset.

using uniffi.hyperwhisper_core;
using AppLicenseStatus = HyperWhisper.Models.LicenseStatus;

namespace HyperWhisper.Services;

/// <summary>
/// Shared accessors for driving the Rust license/usage core.
/// </summary>
internal static class RustLicenseCore
{
    /// <summary>The single shared store passed to every <c>License*</c> call.</summary>
    public static KeyValueStore Store => RustCoreKeyValueStore.Instance;

    /// <summary>Plain UTC unix seconds — used for usage AND cache calls on Windows.</summary>
    public static long Now() => System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---- Status mapping ----

    public static AppLicenseStatus ToApp(HwLicenseStatus status) => status switch
    {
        HwLicenseStatus.Active => AppLicenseStatus.Active,
        HwLicenseStatus.Trial => AppLicenseStatus.Trial,
        HwLicenseStatus.Expired => AppLicenseStatus.Expired,
        HwLicenseStatus.Invalid => AppLicenseStatus.Invalid,
        _ => AppLicenseStatus.Invalid
    };

    public static HwLicenseStatus ToCore(AppLicenseStatus status) => status switch
    {
        AppLicenseStatus.Active => HwLicenseStatus.Active,
        AppLicenseStatus.Trial => HwLicenseStatus.Trial,
        AppLicenseStatus.Expired => HwLicenseStatus.Expired,
        AppLicenseStatus.Invalid => HwLicenseStatus.Invalid,
        _ => HwLicenseStatus.Invalid
    };

    /// <summary>
    /// Convert a core <see cref="ValidationOutcome"/> into the app-facing
    /// <see cref="HyperWhisper.Models.LicenseValidationResult"/>.
    /// </summary>
    public static Models.LicenseValidationResult ToResult(ValidationOutcome outcome)
    {
        var status = ToApp(outcome.status);
        return new Models.LicenseValidationResult
        {
            IsValid = outcome.isValid,
            Status = status,
            RawStatus = status.ToString().ToLowerInvariant(),
            CustomerId = outcome.customerId,
            CustomerEmail = outcome.customerEmail,
            ExpiresAt = ParseExpiry(outcome.expiresAt),
            ErrorMessage = outcome.errorMessage,
            ValidatedAt = System.DateTime.UtcNow
        };
    }

    private static System.DateTime? ParseExpiry(string? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(expiresAt))
        {
            return null;
        }
        return System.DateTime.TryParse(expiresAt, out var dt) ? dt : (System.DateTime?)null;
    }
}
