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
}
