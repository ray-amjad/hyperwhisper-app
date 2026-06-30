//
//  AccessibilityHelper+Frontmost.swift
//  hyperwhisper
//
//  Created by Assistant on 16/08/2025.
//

import Foundation
import AppKit
import ApplicationServices

extension AccessibilityHelper {

    // MARK: - Frontmost App Helpers

    /// Returns the PID of the frontmost application, if available
    func frontmostPID() -> pid_t? {
        return NSWorkspace.shared.frontmostApplication?.processIdentifier
    }

    /// Returns the bundle identifier of the frontmost application, if available
    func frontmostBundleId() -> String? {
        return NSWorkspace.shared.frontmostApplication?.bundleIdentifier
    }

    /// Basic browser detection by bundle identifier
    func isBrowserBundleId(_ bundleId: String) -> Bool {
        let browsers: Set<String> = [
            "com.apple.Safari",
            "com.apple.SafariTechnologyPreview",
            "com.google.Chrome",
            "com.google.Chrome.canary",
            "com.brave.Browser",
            "com.microsoft.edgemac",
            "org.mozilla.firefox",
            "company.thebrowser.Arc",
            "com.operasoftware.Opera",              // Opera
            "com.vivaldi.Vivaldi",                  // Vivaldi
            "com.kagi.kagimacOS",                   // Orion by Kagi
            "app.zen-browser.zen",                  // Zen Browser
            "ru.yandex.desktop.yandex-browser"      // Yandex
        ]
        return browsers.contains(bundleId)
    }

    /// Terminal detection by bundle identifier.
    /// Streaming text injection should prefer whole-segment paste in terminals
    /// because HID Unicode typing is less reliable there than in standard AppKit
    /// text fields, especially for spaces during fast incremental dictation.
    func isTerminalBundleId(_ bundleId: String) -> Bool {
        let terminals: Set<String> = [
            "com.apple.Terminal",
            "com.googlecode.iterm2",
            "com.cmuxterm.app",
            "com.github.wez.wezterm",
            "com.mitchellh.ghostty",
            "dev.warp.Warp-Stable",
            "dev.warp.WarpPreview",
            "io.alacritty",
            "net.kovidgoyal.kitty"
        ]
        return terminals.contains(bundleId)
    }

    /// Whether the target app is unreliable for live streaming insertion.
    /// For these targets HyperWhisper shows a preview bubble during speech
    /// and commits the full transcript with a single paste at session end,
    /// rather than trying to type each delta as it arrives.
    func requiresStreamingPreviewFallback(bundleId: String?) -> Bool {
        guard let bundleId else { return false }
        return isTerminalBundleId(bundleId)
    }

    /// Remote desktop app detection by bundle identifier
    /// These apps render a remote desktop as a single view surface and don't expose
    /// remote text fields through the local Accessibility API. They also use clipboard
    /// forwarding that may skip concealed pasteboard items.
    func isRemoteDesktopBundleId(_ bundleId: String) -> Bool {
        let remoteDesktopApps: Set<String> = [
            "com.apple.ScreenSharing",
            "com.apple.RemoteDesktop",
        ]
        return remoteDesktopApps.contains(bundleId)
    }

    // MARK: - Window Title Extraction

    /// Get the title of the frontmost window
    /// This is useful for detecting which project is open in an IDE
    /// - Returns: The window title, or nil if unavailable
    /// NOTE: This may require Screen Recording permission on macOS 10.15+
    func getFrontmostWindowTitle() -> String? {
        guard let frontApp = NSWorkspace.shared.frontmostApplication,
              let pid = frontApp.processIdentifier as pid_t? else {
            return nil
        }

        // Get all visible windows (correct API usage for getting all windows)
        // Using .optionOnScreenOnly to get all windows that are currently visible
        let windowList = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []

        // Find windows belonging to the frontmost app
        let appWindows = windowList.filter { windowInfo in
            guard let windowPID = windowInfo[kCGWindowOwnerPID as String] as? pid_t else { return false }
            return windowPID == pid
        }

        // Get the main window (first one with a name)
        // Note: Reading window titles may fail without Screen Recording permission
        guard let mainWindow = appWindows.first(where: { info in
            if let windowName = info[kCGWindowName as String] as? String {
                return !windowName.isEmpty
            }
            return false
        }) else {
            // If we couldn't get the window title, it might be due to permissions
            // Return nil gracefully if window title couldn't be read
            return nil
        }

        return mainWindow[kCGWindowName as String] as? String
    }
}
