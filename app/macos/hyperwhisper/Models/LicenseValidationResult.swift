//
//  LicenseValidationResult.swift
//  hyperwhisper
//
//  LICENSE VALIDATION RESULT MODEL
//  Data structure returned from license validation operations.
//
//  This struct encapsulates the response from license validation operations.
//  It contains all relevant information about the validation attempt and
//  the associated customer account.
//
//  SIMPLIFIED (as of 2025-12):
//  - Removed activationId - no longer tracking device activations
//  - Fair usage policy via device_validations table instead
//
//  Used by:
//  - LicenseNetworkService: Returns this after API calls
//  - LicenseManager: Processes this to update app state
//  - UI Views: Displays validation results to users
//

import Foundation

/// Result returned from license validation operations
///
/// This struct contains all information needed to update the app state
/// after a license validation attempt.
///
/// FIELDS:
/// - isValid: Whether the license is currently valid and active
/// - status: The license status (trial, active, expired, invalid)
/// - customerId: Unique customer identifier from the backend
/// - customerEmail: Email address associated with the license
/// - customerName: Customer name for personalization
/// - errorMessage: Human-readable error if validation failed
struct LicenseValidationResult {
    /// Whether the license is valid and active
    /// true = user can use licensed features
    /// false = user is in trial mode or has invalid license
    let isValid: Bool

    /// The resulting license status after validation
    /// Determines UI display and feature availability
    let status: LicenseStatus

    /// Unique customer ID from the backend
    /// Used for support and account management
    let customerId: String?

    /// Customer email address
    /// Displayed in settings and used for correspondence
    let customerEmail: String?

    /// Customer name for UI personalization
    /// Shows "Welcome back, John" type messages
    let customerName: String?

    /// Human-readable error message if validation failed
    /// Displayed to users when operations fail
    /// nil when operation succeeds
    let errorMessage: String?

    /// `true` when this result was served from a cached (or Invalid, if no
    /// cache exists) verdict because the live validation call exhausted its
    /// retries due to a genuine connectivity failure — as opposed to a real
    /// server-issued verdict (a 200 response, a terminal non-2xx error, or
    /// exhausted retries against repeated 5xx/429 responses).
    ///
    /// Distinct from the normal "24h cache is still fresh, no live call was
    /// even attempted" path (`LicenseManager.loadStoredLicense()`'s
    /// `getCachedLicenseStatus()` branch), where no revalidation is needed.
    /// `LicenseManager` uses this flag to schedule a short, one-shot
    /// background retry after a launch-time validation falls back this way —
    /// a merely-slow-but-live network (weak wifi, VPN overhead) shouldn't
    /// leave a legitimately-licensed user riding a stale cached verdict for up
    /// to the full 7-day offline grace period. HYPERWHISPER-F4 (review round 2).
    ///
    /// Defaults to `false`; only `LicenseNetworkService.validateLicense`'s
    /// offline-fallback catch branch sets it `true`.
    let networkFailureFallback: Bool

    /// `true` when the server returned a verdict but the secure store could not
    /// commit it. `status` still carries that authoritative server verdict.
    /// `LicenseManager` keeps an existing Active state only when the server also
    /// returned Active; an Invalid or Expired verdict revokes it immediately.
    let storagePersistenceFailed: Bool

    init(
        isValid: Bool,
        status: LicenseStatus,
        customerId: String?,
        customerEmail: String?,
        customerName: String?,
        errorMessage: String?,
        networkFailureFallback: Bool = false,
        storagePersistenceFailed: Bool = false
    ) {
        self.isValid = isValid
        self.status = status
        self.customerId = customerId
        self.customerEmail = customerEmail
        self.customerName = customerName
        self.errorMessage = errorMessage
        self.networkFailureFallback = networkFailureFallback
        self.storagePersistenceFailed = storagePersistenceFailed
    }
}
