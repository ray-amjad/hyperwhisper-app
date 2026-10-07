//
//  LaunchAtLoginManager.swift
//  hyperwhisper
//
//  Native launch-at-login manager on ServiceManagement's login-item API.
//  Replaces the third-party LaunchAtLogin-Modern package.
//
//  WHY NATIVE IMPLEMENTATION:
//  The LaunchAtLogin package uses computed Binding(get:set:) patterns
//  that trigger Swift Concurrency executor isolation checks during
//  SwiftUI's layout computation phase on macOS 26.2 (Build 25C56).
//  This causes an infinite recursion in SerialExecutor.isMainExecutor.getter,
//  resulting in a stack overflow crash.
//
//  SOLUTION:
//  Using a simple enum with static methods avoids the computed binding
//  pattern entirely. Views use local @State and fill it in from this
//  manager, preventing the executor isolation check loop.
//
//  Sentry Issue: HYPERWHISPER-3V
//
//  WHY EVERYTHING HERE IS ASYNC (#853, Sentry HYPERWHISPER-SY):
//  Every login-item call — the status read, register() and unregister() — is a
//  synchronous XPC round trip to the system `smd` daemon. Read from a SwiftUI
//  `.onAppear` it ran on the main thread in the middle of a layout pass, and
//  when `smd` was slow the whole app hung (10 s+ in Sentry). Both entry points
//  are now `async` and run the blocking call on a private serial queue, so
//  there is no synchronous member left for a main-actor caller to reach.
//

import Foundation
import ServiceManagement
import os

/// Native launch-at-login manager
///
/// ARCHITECTURE:
/// This is a stateless utility enum that wraps the app's own login item.
/// Both operations are `async`: the blocking system call runs on a private
/// serial queue, never on the caller's thread.
///
/// USAGE:
/// ```swift
/// // Check current state
/// let isEnabled = await LaunchAtLoginManager.readIsEnabled()
///
/// // Enable/disable — returns the state the system actually holds afterwards
/// let actual = await LaunchAtLoginManager.setEnabled(true)
/// ```
///
/// INTEGRATION WITH SWIFTUI:
/// Views should NOT create computed bindings to this manager. Use local
/// @State, fill it in from `.task`, and write through `.onChange` with a
/// `Task { }` that applies the returned state (see GeneralSettingsSection).
enum LaunchAtLoginManager {

    // MARK: - Logger

    private static let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LaunchAtLogin")

    // MARK: - System seam

    /// The blocking login-item calls, as values, so a test can run the manager
    /// against a fake. Production always uses `.mainApp`.
    struct Service: Sendable {
        var status: @Sendable () -> SMAppService.Status
        var register: @Sendable () throws -> Void
        var unregister: @Sendable () throws -> Void
        var openApprovalSettings: @Sendable () -> Void

        /// The app's own login item.
        static let mainApp = Service(
            status: { LaunchAtLoginManager.mainAppStatus() },
            register: { try LaunchAtLoginManager.mainAppRegister() },
            unregister: { try LaunchAtLoginManager.mainAppUnregister() },
            openApprovalSettings: { LaunchAtLoginManager.openLoginItemsSettings() }
        )
    }

    /// One serial queue for every read and write.
    ///
    /// Serial so that a quick ON→OFF→ON on the toggle reaches the system in the
    /// order it was clicked, and a read is never answered from the middle of a
    /// write. A dispatch queue rather than `Task.detached` so a slow `smd`
    /// parks a queue thread, not one of the few cooperative-pool threads.
    private static let queue = DispatchQueue(
        label: "com.hyperwhisper.app.launch-at-login",
        qos: .userInitiated
    )

    // MARK: - Public API

    /// Whether launch at login is currently enabled.
    ///
    /// Reads the login item's status on the private queue and returns it.
    ///
    /// Both `.enabled` and `.requiresApproval` count as enabled: in the latter,
    /// register() succeeded and the login item exists, but macOS is gating
    /// activation behind the user's approval in System Settings → Login Items &
    /// Extensions (common after Migration Assistant or an app rename).
    /// Collapsing `.requiresApproval` to false made the toggle bounce back to OFF
    /// with no way forward (#288); reporting true keeps the toggle ON while
    /// setEnabled(true) routes the user to the approval UI.
    ///
    /// `.notRegistered` and `.notFound` remain disabled.
    ///
    /// There is deliberately no timeout: reporting `false` on a slow answer
    /// would be the #288 bounce again. The caller waits off the main thread.
    nonisolated static func readIsEnabled(using service: Service = .mainApp) async -> Bool {
        await runOffMain {
            isEnabled(service.status())
        }
    }

    /// Enable or disable launch at login, and return the resulting state.
    ///
    /// IMPLEMENTATION:
    /// - Calls register() to enable, unregister() to disable
    /// - Then reads the status ONCE, on the same queue, and returns it — so the
    ///   caller needs no second round trip to resync its toggle
    ///
    /// ERROR HANDLING:
    /// Errors are logged but not thrown. This matches the behavior of
    /// the LaunchAtLogin package, which also silently handles errors.
    /// Common errors include:
    /// - User denied permission in System Preferences
    /// - App is in a location that doesn't support login items
    /// A rejected change shows up in the returned state, which is what the
    /// toggle must snap back to (#286 review P2).
    ///
    /// - Parameter enabled: Whether to enable or disable launch at login
    /// - Returns: Whether launch at login is enabled after the change
    @discardableResult
    nonisolated static func setEnabled(_ enabled: Bool, using service: Service = .mainApp) async -> Bool {
        await runOffMain {
            applyBlocking(enabled, service: service)
        }
    }

    // MARK: - Private Helpers

    /// Runs `body` on the private serial queue and resumes with its result.
    /// `body` runs exactly once, so the continuation is resumed exactly once.
    nonisolated private static func runOffMain(_ body: @escaping @Sendable () -> Bool) async -> Bool {
        await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
            queue.async {
                continuation.resume(returning: body())
            }
        }
    }

    /// The blocking write plus its one follow-up read. Only ever called on `queue`.
    nonisolated private static func applyBlocking(_ enabled: Bool, service: Service) -> Bool {
        var registered = false
        do {
            if enabled {
                try service.register()
                registered = true
                logger.info("✅ Launch at login enabled")
            } else {
                try service.unregister()
                logger.info("✅ Launch at login disabled")
            }
        } catch {
            // Log error but don't throw - matches LaunchAtLogin package behavior
            // The setting may fail silently if user denies permission or app
            // is in an unsupported location
            let action = enabled ? "enable" : "disable"
            logger.error("Failed to \(action) launch at login: \(error.localizedDescription)")
        }

        let status = service.status()

        // register() can succeed while macOS still requires the user to
        // approve the login item in System Settings (e.g. after Migration
        // Assistant or an app rename). Without surfacing this, the feature
        // silently never activates and the user has no way forward (#288).
        // Open the approval pane so they can complete enabling.
        if registered && status == .requiresApproval {
            logger.info("Launch at login requires approval — opening Login Items settings")
            service.openApprovalSettings()
        }

        return isEnabled(status)
    }

    nonisolated private static func isEnabled(_ status: SMAppService.Status) -> Bool {
        switch status {
        case .enabled, .requiresApproval:
            return true
        default:
            return false
        }
    }

    // The only places the real login item is touched. Each blocks on an XPC
    // round trip to `smd`, so each is reached only through `Service.mainApp`
    // from `runOffMain` — never on the caller's thread.

    nonisolated private static func mainAppStatus() -> SMAppService.Status {
        SMAppService.mainApp.status
    }

    nonisolated private static func mainAppRegister() throws {
        try SMAppService.mainApp.register()
    }

    nonisolated private static func mainAppUnregister() throws {
        try SMAppService.mainApp.unregister()
    }

    /// Opens System Settings → General → Login Items & Extensions.
    ///
    /// Dispatched to the main queue because it presents UI.
    nonisolated private static func openLoginItemsSettings() {
        DispatchQueue.main.async {
            SMAppService.openSystemSettingsLoginItems()
        }
    }
}
