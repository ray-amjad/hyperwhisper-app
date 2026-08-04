//
//  NotesDestination.swift
//  hyperwhisper
//
//  Routes transcribed text into Apple Notes as a brand-new note.
//  Used by the Quick Capture shortcut. See plan: Quick Capture → Notes (v1).
//

import Foundation
import AppKit
import Combine

/// Published state for the Notes automation permission banner shown in the
/// Quick Capture settings card. Updated by NotesDestination whenever a send
/// attempt is blocked by TCC (errors -1743 / -600) or succeeds after a denial.
@MainActor
final class NotesAutomationPermissionState: ObservableObject {
    static let shared = NotesAutomationPermissionState()

    /// True when the last AppleScript send to Notes was rejected for missing
    /// Automation permission. Flipped back to false on the next successful send.
    @Published var needsAutomationPermission: Bool = false

    private init() {}
}

/// Sends transcribed text to Apple Notes via AppleScript.
///
/// **Why AppleScript and not the Notes URL scheme:**
/// The `notes://` URL scheme on macOS cannot create a new note with a
/// pre-populated body — it only opens existing notes. AppleScript is the
/// only documented path for "create new note with body".
///
/// **Entitlements:**
/// Already configured (see hyperwhisper.entitlements + Info.plist):
/// - `com.apple.security.automation.apple-events = true`
/// - `NSAppleEventsUsageDescription`
/// - App is not sandboxed
enum NotesDestination {

    /// AppleScript / Apple Event error codes we treat as "not authorized to
    /// control Notes." When we see one, we flip the banner state and surface
    /// the error to the user instead of pretending the send succeeded.
    private static let notAuthorizedErrorCodes: Set<Int> = [
        -1743,  // errAEEventNotPermitted — TCC denial for Automation
        -600    // procNotFound — Notes not running and access blocked
    ]

    /// Create a new note in Apple Notes with `text` as the body.
    ///
    /// - Returns: true if the AppleScript reported success. False on any
    ///   AppleScript error (including TCC denial, which also flips the
    ///   `needsAutomationPermission` flag for the settings banner).
    @MainActor
    static func send(text: String) async -> Bool {
        guard !TextDeliveryGate.isSuppressed else {
            AppLogger.audio.info("🚫 NotesDestination.send suppressed by TextDeliveryGate")
            return false
        }

        let escapedBody = escapeForAppleScript(text)

        // `activate` ensures the user sees the new note land — without it,
        // Notes may stay in the background and the user assumes nothing
        // happened. `make new note` returns the new note object; the script
        // returns "ok" so we have an unambiguous success signal.
        let script = """
        tell application "Notes"
            activate
            make new note with properties {body:"\(escapedBody)"}
            return "ok"
        end tell
        """

        return await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
            // NSAppleScript executes synchronously and can take a noticeable
            // amount of time when Notes is launching. Run it off the main
            // queue so we don't block UI.
            DispatchQueue.global(qos: .userInitiated).async {
                var errorInfo: NSDictionary?
                guard let appleScript = NSAppleScript(source: script) else {
                    AppLogger.audio.error("NotesDestination: failed to create NSAppleScript")
                    continuation.resume(returning: false)
                    return
                }

                let result = appleScript.executeAndReturnError(&errorInfo)

                if let error = errorInfo {
                    let code = (error[NSAppleScript.errorNumber] as? Int) ?? 0
                    let message = (error[NSAppleScript.errorMessage] as? String) ?? "Unknown AppleScript error"
                    AppLogger.audio.error("NotesDestination: AppleScript error \(code) — \(message, privacy: .public)")

                    let isAuthError = notAuthorizedErrorCodes.contains(code)
                    Task { @MainActor in
                        if isAuthError {
                            NotesAutomationPermissionState.shared.needsAutomationPermission = true
                        }
                    }
                    continuation.resume(returning: false)
                    return
                }

                let returnedString = result.stringValue ?? ""
                let success = returnedString == "ok"
                AppLogger.audio.info("NotesDestination: send \(success ? "succeeded" : "completed without ok marker")")

                // Clear the banner on a successful send.
                Task { @MainActor in
                    if NotesAutomationPermissionState.shared.needsAutomationPermission {
                        NotesAutomationPermissionState.shared.needsAutomationPermission = false
                    }
                }
                continuation.resume(returning: success)
            }
        }
    }

    /// Escape a string for inclusion inside an AppleScript double-quoted
    /// string literal. AppleScript string syntax:
    ///   - `\\` for a literal backslash
    ///   - `\"` for a literal double quote
    ///   - `\n` for a newline (interpreted by AppleScript at runtime)
    /// Other control characters are passed through; CR is normalised to LF
    /// first so Notes treats line breaks consistently.
    static func escapeForAppleScript(_ raw: String) -> String {
        var result = ""
        result.reserveCapacity(raw.count)
        // Normalise CRLF and bare CR to LF before scanning so the case below
        // can handle a single newline kind.
        let normalised = raw
            .replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
        for character in normalised {
            switch character {
            case "\\": result.append("\\\\")
            case "\"": result.append("\\\"")
            case "\n": result.append("\\n")
            case "\t": result.append("\\t")
            default: result.append(character)
            }
        }
        return result
    }
}
