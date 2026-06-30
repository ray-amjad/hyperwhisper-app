//
//  HomebrewDetection.swift
//  hyperwhisper
//
//  Created by AI Assistant on 28/01/2026.
//

import Foundation
import os

/// HOMEBREW INSTALLATION DETECTOR
/// Detects whether HyperWhisper was installed via Homebrew to prevent update conflicts.
///
/// PROBLEM CONTEXT:
/// HyperWhisper auto-updates itself via Sparkle, but when installed via Homebrew,
/// users may also run `brew upgrade hyperwhisper`, causing:
/// - Redundant updates (app updates itself, then brew updates it again)
/// - Version confusion (brew has no knowledge of self-updates)
/// - Wasted bandwidth and user confusion
///
/// SOLUTION:
/// Automatically detect Homebrew installations and disable Sparkle's auto-update feature.
/// Manual "Check for Updates" remains available, but auto-checks are disabled.
///
/// HOW IT WORKS:
/// Homebrew installs casks to standardized directories:
/// - Apple Silicon Macs: /opt/homebrew/Caskroom/hyperwhisper/{version}/HyperWhisper.app
/// - Intel Macs: /usr/local/Caskroom/hyperwhisper/{version}/HyperWhisper.app
///
/// We check if the app's bundle path contains these Homebrew-specific paths.
/// This is more reliable than checking for symlinks (which may not exist) or
/// environment variables (which only exist when brew is active).
class HomebrewDetection {

    // MARK: - Properties

    /// Logger for debugging detection logic
    private static let logger = Logger(subsystem: "com.hyperwhisper.app", category: "HomebrewDetection")

    /// Known Homebrew installation directories
    /// These are the standard paths where Homebrew installs applications
    private static let homebrewPaths = [
        "/opt/homebrew/Caskroom",      // Apple Silicon Macs (M1/M2/M3+)
        "/usr/local/Caskroom",         // Intel Macs
        "/usr/local/Cellar"            // Rare: formulae (not casks), but included for completeness
    ]

    /// Cached detection result to avoid repeated filesystem checks
    /// Uses a thread-safe lazy static so detection runs once per launch.
    private static let isBrewInstall: Bool = {
        // Get the app's installation path
        // Bundle.main.bundlePath returns the full path to HyperWhisper.app
        let bundlePath = Bundle.main.bundlePath

        // Check if the bundle path starts with any known Homebrew directory
        // If it does, the app was installed via Homebrew
        let isHomebrew = homebrewPaths.contains { homebrewPath in
            bundlePath.hasPrefix(homebrewPath)
        }

        // Log detection result with path for debugging
        // This helps diagnose false positives/negatives in production
        if isHomebrew {
            logger.info("Homebrew installation detected - auto-update will be disabled [bundlePath: \(bundlePath, privacy: .public), installationType: homebrew]")
        } else {
            logger.debug("Standard installation detected - auto-update available [bundlePath: \(bundlePath, privacy: .public), installationType: standard]")
        }

        return isHomebrew
    }()

    // MARK: - Public Methods

    /// Checks if the app was installed via Homebrew
    ///
    /// DETECTION LOGIC:
    /// 1. Get the app's bundle path from Bundle.main
    /// 2. Check if path starts with any known Homebrew directory
    /// 3. Cache the result (installation method doesn't change at runtime)
    /// 4. Log the detection for debugging
    ///
    /// PERFORMANCE:
    /// Thread-safe lazy static ensures the first call checks filesystem,
    /// subsequent calls return cached result (O(1)).
    ///
    /// - Returns: `true` if installed via Homebrew, `false` otherwise
    static func isInstalledViaBrew() -> Bool {
        return isBrewInstall
    }

    /// Get human-readable installation type for UI display
    ///
    /// USE CASE: Display in Settings or About window
    ///
    /// - Returns: "Homebrew" or "Standard"
    static func installationTypeDescription() -> String {
        return isInstalledViaBrew() ? "Homebrew" : "Standard"
    }

    /// Get recommendation message for updates
    ///
    /// USE CASE: Show user-friendly guidance in Settings or update dialogs
    ///
    /// - Returns: Update recommendation message appropriate for installation type
    static func updateRecommendation() -> String {
        if isInstalledViaBrew() {
            return "Use 'brew upgrade hyperwhisper' to update this app."
        } else {
            return "HyperWhisper will check for updates automatically."
        }
    }
}
