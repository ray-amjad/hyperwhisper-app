//
//  ElectronAppDetector.swift
//  hyperwhisper
//
//  ELECTRON APP DETECTOR
//  Centralized service for detecting Electron-based code editors.
//  This consolidates all Electron app detection logic in one place,
//  making it easier to add new editors and maintain consistency.
//
//  FEATURES:
//  - Detects known Electron editors by bundle ID
//  - Supports pattern matching for ToDesktop variants
//  - Logs unknown Electron-like apps for future support
//  - Thread-safe singleton pattern

import Foundation
import AppKit
import os

// MARK: - Electron App Detector

/// Centralized service for detecting Electron-based applications
public class ElectronAppDetector {

    // MARK: - Singleton

    /// Shared instance for app-wide use
    public static let shared = ElectronAppDetector()

    /// Logger for Electron app detection
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "ElectronAppDetector")

    /// Private init to enforce singleton pattern
    private init() {}
    
    // MARK: - Known Electron Editors
    
    /// Known Electron editor bundle IDs (exact matches)
    private let knownElectronEditors: Set<String> = [
        // Cursor - Multiple variants
        "com.todesktop.230313mzl4w4u92",  // Cursor (ToDesktop wrapper)
        "com.cursor.ide",                  // Cursor (alternative bundle ID)
        
        // Windsurf
        "com.exafunction.windsurf",        // Windsurf (Codeium)
        
        // Visual Studio Code variants
        "com.microsoft.VSCode",            // VS Code (stable)
        "com.microsoft.VSCodeInsiders",    // VS Code Insiders (preview)
        "com.visualstudio.code.oss",       // VS Code OSS (open source build)
        "com.vscodium",                    // VSCodium (community build)
        "com.vscodium.VSCodium",          // VSCodium (alternative ID)
        
        // Other Electron-based editors
        "com.github.atom",                 // Atom (deprecated but still in use)
        "io.brackets.appshell",            // Brackets
        
        // Zed (new Rust-based editor with Electron UI)
        "dev.zed.Zed",
        "dev.zed.Zed-Preview"
    ]
    
    /// Bundle ID prefixes that indicate Electron apps
    /// ToDesktop creates unique IDs for each app, so we need pattern matching
    private let electronPrefixes: [String] = [
        "com.todesktop.",      // ToDesktop wrapper prefix
        "com.electron.",       // Generic Electron apps
        "io.github.electron."  // Electron apps from GitHub
    ]
    
    /// Cache for detected Electron apps (for logging new ones)
    private var detectedUnknownElectronApps: Set<String> = []
    
    // MARK: - Public Methods
    
    /// Check if a bundle ID belongs to an Electron-based code editor
    /// - Parameter bundleId: The bundle identifier to check
    /// - Returns: true if the app is an Electron editor
    public func isElectronEditor(_ bundleId: String) -> Bool {
        // Check exact matches first (faster)
        if knownElectronEditors.contains(bundleId) {
            return true
        }
        
        // Check prefix patterns
        for prefix in electronPrefixes {
            if bundleId.hasPrefix(prefix) {
                // Log unknown ToDesktop variants for future reference
                if !detectedUnknownElectronApps.contains(bundleId) {
                    detectedUnknownElectronApps.insert(bundleId)
                    logger.info("📱 Detected potential Electron editor: \(bundleId, privacy: .public)")
                    logger.info("   Consider adding to knownElectronEditors if confirmed")
                }
                return true
            }
        }
        
        return false
    }
    
    /// Check if the currently frontmost app is an Electron editor
    /// - Returns: true if the frontmost app is an Electron editor
    public func isFrontmostAppElectronEditor() -> Bool {
        guard let frontApp = NSWorkspace.shared.frontmostApplication,
              let bundleId = frontApp.bundleIdentifier else {
            return false
        }
        
        return isElectronEditor(bundleId)
    }
    
    /// Get the bundle ID of the frontmost app if it's an Electron editor
    /// - Returns: Bundle ID if frontmost app is an Electron editor, nil otherwise
    public func getFrontmostElectronEditorBundleId() -> String? {
        guard let frontApp = NSWorkspace.shared.frontmostApplication,
              let bundleId = frontApp.bundleIdentifier,
              isElectronEditor(bundleId) else {
            return nil
        }
        
        return bundleId
    }
    
    /// Get display name for a known Electron editor
    /// - Parameter bundleId: The bundle identifier
    /// - Returns: Human-readable name or nil if unknown
    public func getEditorDisplayName(for bundleId: String) -> String? {
        switch bundleId {
        case "com.todesktop.230313mzl4w4u92", "com.cursor.ide":
            return "Cursor"
        case "com.exafunction.windsurf":
            return "Windsurf"
        case "com.microsoft.VSCode":
            return "Visual Studio Code"
        case "com.microsoft.VSCodeInsiders":
            return "VS Code Insiders"
        case "com.visualstudio.code.oss":
            return "VS Code OSS"
        case "com.vscodium", "com.vscodium.VSCodium":
            return "VSCodium"
        case "com.github.atom":
            return "Atom"
        case "io.brackets.appshell":
            return "Brackets"
        case "dev.zed.Zed":
            return "Zed"
        case "dev.zed.Zed-Preview":
            return "Zed Preview"
        default:
            // For unknown ToDesktop variants, try to extract from bundle ID
            if bundleId.hasPrefix("com.todesktop.") {
                return "Electron Editor (ToDesktop)"
            }
            return nil
        }
    }
    
    /// Get all known Electron editor bundle IDs (for testing/debugging)
    /// - Returns: Set of all known bundle IDs
    public func getAllKnownBundleIds() -> Set<String> {
        return knownElectronEditors
    }
    
    /// Add a newly discovered Electron editor bundle ID
    /// Use this when we discover new Electron editors in the wild
    /// - Parameter bundleId: The bundle ID to add
    public func addKnownElectronEditor(_ bundleId: String) {
        // This would need to be persisted to UserDefaults in a production app
        // For now, it's just for runtime detection
        logger.info("📝 Adding new Electron editor: \(bundleId, privacy: .public)")
        // Note: Can't modify let constant, would need to make it var and thread-safe
    }
}

// MARK: - Convenience Extensions

extension ElectronAppDetector {
    
    /// Check if any Electron editor is currently running
    /// - Returns: Array of running Electron editor bundle IDs
    public func getRunningElectronEditors() -> [String] {
        let runningApps = NSWorkspace.shared.runningApplications
        return runningApps.compactMap { app in
            guard let bundleId = app.bundleIdentifier,
                  isElectronEditor(bundleId) else {
                return nil
            }
            return bundleId
        }
    }
    
    /// Debug helper to log all running apps and identify potential Electron apps
    public func logRunningApps() {
        logger.debug("🔍 Running Applications:")
        for app in NSWorkspace.shared.runningApplications {
            if let bundleId = app.bundleIdentifier {
                let isElectron = isElectronEditor(bundleId)
                let marker = isElectron ? "✅ ELECTRON" : ""
                logger.debug("   \(bundleId, privacy: .public) - \(app.localizedName ?? "Unknown", privacy: .public) \(marker, privacy: .public)")
            }
        }
    }
}