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

}
