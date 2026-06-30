//
//  MicrophonePermissionManager.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import AVFoundation

/// Manages microphone permission state and requests
///
/// **Purpose:**
/// Handles all microphone permission logic for macOS audio recording:
/// - Checks current permission status
/// - Requests permission from user (shows system dialog)
/// - Tracks permission state for UI updates
/// - Provides error messages for permission denial
///
/// **macOS Permissions:**
/// macOS requires explicit user consent to access the microphone.
/// On first request, the system shows a permission dialog.
/// If denied, the user must manually enable in System Settings > Privacy & Security.
///
/// **Permission States:**
/// - `.authorized`: User granted permission, recording allowed
/// - `.notDetermined`: Permission not yet requested
/// - `.denied`: User explicitly denied permission
/// - `.restricted`: System-level restriction (parental controls, MDM)
///
/// **UI Integration:**
/// Published properties allow views to reactively show:
/// - Permission request buttons
/// - Error alerts for denied permissions
/// - Links to System Settings
///
/// **Thread Safety:**
/// All methods run on main actor for UI consistency.
@MainActor
class MicrophonePermissionManager {

    // MARK: - Published Properties

    /// Current microphone permission status
    @Published var hasMicrophonePermission: Bool = false

    /// Whether to show permission denied alert
    @Published var showPermissionDeniedAlert: Bool = false

    /// Error message for permission issues
    @Published var errorMessage: String = ""

    // MARK: - Initialization

    init() {
        // Check initial permission status
        checkMicrophonePermission()
    }

    // MARK: - Permission Management

    /// Request microphone permission from the user
    ///
    /// **What This Does:**
    /// 1. Checks current authorization status
    /// 2. If authorized: updates state and returns true
    /// 3. If not determined: shows system permission dialog
    /// 4. If denied/restricted: updates state and returns false
    /// 5. Updates hasMicrophonePermission for UI binding
    ///
    /// **System Dialog:**
    /// On first request (notDetermined), macOS shows a system dialog:
    /// "HyperWhisper would like to access the microphone"
    /// [Don't Allow] [OK]
    ///
    /// **When to Call:**
    /// - Before starting recording
    /// - When user clicks "Request Permission" button
    /// - During app initialization
    ///
    /// **Returns:**
    /// true if permission granted, false otherwise
    ///
    /// **Thread Safety:**
    /// Async method that can be awaited from main actor
    func requestMicrophonePermission() async -> Bool {
        // Check current status
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        switch status {
        case .authorized:
            // Already granted
            hasMicrophonePermission = true
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Microphone permission already authorized",
                    category: "audio.permission",
                    data: ["status": Self.authorizationStatusString(status)]
                )
            }
            return true

        case .notDetermined:
            // Request permission - this shows the system dialog
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Microphone permission prompt shown",
                    category: "audio.permission",
                    data: ["status": Self.authorizationStatusString(status)]
                )
            }
            let granted = await AVCaptureDevice.requestAccess(for: .audio)
            hasMicrophonePermission = granted

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Microphone permission prompt resolved",
                    category: "audio.permission",
                    data: ["granted": granted]
                )
            }

            if !granted {
                errorMessage = "audio.error.permissionDenied".localized
                showPermissionDeniedAlert = true
            }

            return granted

        case .denied, .restricted:
            // User denied or system restricted
            hasMicrophonePermission = false
            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "Microphone permission denied or restricted",
                    category: "audio.permission",
                    level: .warning,
                    data: ["status": Self.authorizationStatusString(status)]
                )
            }
            errorMessage = "audio.error.permissionDenied".localized
            showPermissionDeniedAlert = true
            return false

        @unknown default:
            // Future-proof for new permission states
            hasMicrophonePermission = false
            errorMessage = "audio.error.permissionDenied".localized
            return false
        }
    }

    /// Check current microphone permission status without requesting
    ///
    /// **What This Does:**
    /// Queries the current authorization status and updates published properties.
    /// Does NOT show any dialogs or request permission.
    ///
    /// **When to Call:**
    /// - During initialization
    /// - When app becomes active (to detect permission changes in Settings)
    /// - Before checking if recording is possible
    ///
    /// **Why Separate from Request:**
    /// Sometimes we just want to check status without prompting the user.
    /// For example, showing a "grant permission" button vs auto-requesting.
    func checkMicrophonePermission() {
        let status = AVCaptureDevice.authorizationStatus(for: .audio)

        switch status {
        case .authorized:
            hasMicrophonePermission = true
            errorMessage = ""

        case .denied, .restricted:
            hasMicrophonePermission = false
            errorMessage = "audio.error.permissionDenied".localized

        case .notDetermined:
            hasMicrophonePermission = false
            errorMessage = ""

        @unknown default:
            hasMicrophonePermission = false
            errorMessage = "audio.error.unknown".localized
        }
    }

    /// Returns a privacy-safe string for the current authorization status.
    func currentAuthorizationStatusString() -> String {
        Self.authorizationStatusString(AVCaptureDevice.authorizationStatus(for: .audio))
    }

    private static func authorizationStatusString(_ status: AVAuthorizationStatus) -> String {
        switch status {
        case .authorized:
            return "authorized"
        case .notDetermined:
            return "not_determined"
        case .denied:
            return "denied"
        case .restricted:
            return "restricted"
        @unknown default:
            return "unknown"
        }
    }
}
