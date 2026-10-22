//
//  LaunchAtLoginOffMainTests.swift
//  hyperwhisperTests
//
//  The launch-at-login item must never be read or written on the main thread
//  (issue #853, Sentry HYPERWHISPER-SY).
//
//  Every login-item call is a synchronous XPC round trip to the system `smd`
//  daemon. Opening Settings > General read its status inside `.onAppear`, on
//  the main thread mid-layout, and a slow `smd` hung the whole app. These
//  tests run `LaunchAtLoginManager` and `GeneralSettingsManager` against a
//  fake login item and record which thread each call arrives on.
//

import Foundation
import os
import ServiceManagement
import Testing
@testable import HyperWhisper

/// A fake login item that records every call and the thread it ran on.
private final class FakeLoginItem: Sendable {
    struct State: Sendable {
        var status: SMAppService.Status
        var statusReads = 0
        var registers = 0
        var unregisters = 0
        var approvalOpens = 0
        var callsOnMainThread = 0
    }

    struct Rejected: Error {}

    private let state: OSAllocatedUnfairLock<State>
    private let statusAfterRegister: SMAppService.Status
    private let rejectsWrites: Bool

    /// - Parameters:
    ///   - status: What a status read answers before any write.
    ///   - statusAfterRegister: What a status read answers after a successful register().
    ///   - rejectsWrites: register() and unregister() throw and change nothing.
    init(
        status: SMAppService.Status,
        statusAfterRegister: SMAppService.Status = .enabled,
        rejectsWrites: Bool = false
    ) {
        self.state = OSAllocatedUnfairLock(initialState: State(status: status))
        self.statusAfterRegister = statusAfterRegister
        self.rejectsWrites = rejectsWrites
    }

    var snapshot: State { state.withLock { $0 } }

    var service: LaunchAtLoginManager.Service {
        LaunchAtLoginManager.Service(
            status: { self.readStatus() },
            register: { try self.register() },
            unregister: { try self.unregister() },
            openApprovalSettings: { self.openApprovalSettings() }
        )
    }

    private func readStatus() -> SMAppService.Status {
        let onMain = Thread.isMainThread
        return state.withLock { current in
            current.statusReads += 1
            if onMain { current.callsOnMainThread += 1 }
            return current.status
        }
    }

    private func register() throws {
        let onMain = Thread.isMainThread
        let after = statusAfterRegister
        let rejects = rejectsWrites
        state.withLock { current in
            current.registers += 1
            if onMain { current.callsOnMainThread += 1 }
            if !rejects { current.status = after }
        }
        if rejects { throw Rejected() }
    }

    private func unregister() throws {
        let onMain = Thread.isMainThread
        let rejects = rejectsWrites
        state.withLock { current in
            current.unregisters += 1
            if onMain { current.callsOnMainThread += 1 }
            if !rejects { current.status = .notRegistered }
        }
        if rejects { throw Rejected() }
    }

    private func openApprovalSettings() {
        state.withLock { $0.approvalOpens += 1 }
    }
}

@MainActor
struct LaunchAtLoginOffMainTests {

    // MARK: - LaunchAtLoginManager

    @Test func theStatusReadRunsOffTheMainThread() async {
        let fake = FakeLoginItem(status: .enabled)

        let enabled = await LaunchAtLoginManager.readIsEnabled(using: fake.service)

        #expect(enabled)
        #expect(fake.snapshot.statusReads == 1)
        #expect(fake.snapshot.callsOnMainThread == 0)
    }

    /// `.requiresApproval` must stay ON, or the toggle bounces back to OFF
    /// with no way forward (#288).
    @Test func eachStatusMapsToTheToggleState() async {
        let cases: [(status: SMAppService.Status, expected: Bool)] = [
            (.enabled, true),
            (.requiresApproval, true),
            (.notRegistered, false),
            (.notFound, false),
        ]
        for (status, expected) in cases {
            let fake = FakeLoginItem(status: status)
            let actual = await LaunchAtLoginManager.readIsEnabled(using: fake.service)
            #expect(actual == expected, "status \(status.rawValue)")
        }
    }

    @Test func enablingRegistersOffTheMainThreadAndReadsTheResultOnce() async {
        let fake = FakeLoginItem(status: .notRegistered)

        let actual = await LaunchAtLoginManager.setEnabled(true, using: fake.service)

        let state = fake.snapshot
        #expect(actual)
        #expect(state.registers == 1)
        #expect(state.statusReads == 1)
        #expect(state.callsOnMainThread == 0)
        #expect(state.approvalOpens == 0)
    }

    @Test func disablingUnregistersOffTheMainThreadAndReadsTheResultOnce() async {
        let fake = FakeLoginItem(status: .enabled)

        let actual = await LaunchAtLoginManager.setEnabled(false, using: fake.service)

        let state = fake.snapshot
        #expect(!actual)
        #expect(state.unregisters == 1)
        #expect(state.statusReads == 1)
        #expect(state.callsOnMainThread == 0)
    }

    /// A rejected write must return the state the system still holds, so the
    /// toggle snaps back instead of showing an unapplied value (#286 review P2).
    @Test func aRejectedWriteReturnsTheStateTheSystemStillHolds() async {
        let off = FakeLoginItem(status: .notRegistered, rejectsWrites: true)
        #expect(await LaunchAtLoginManager.setEnabled(true, using: off.service) == false)
        #expect(off.snapshot.approvalOpens == 0)

        let on = FakeLoginItem(status: .enabled, rejectsWrites: true)
        #expect(await LaunchAtLoginManager.setEnabled(false, using: on.service) == true)
    }

    /// register() can succeed while macOS still wants the user's approval
    /// (#288): the toggle stays ON and the approval pane opens once.
    @Test func aRegisterThatNeedsApprovalStaysOnAndOpensTheApprovalPane() async {
        let fake = FakeLoginItem(status: .notRegistered, statusAfterRegister: .requiresApproval)

        let actual = await LaunchAtLoginManager.setEnabled(true, using: fake.service)

        #expect(actual)
        #expect(fake.snapshot.approvalOpens == 1)
        #expect(fake.snapshot.statusReads == 1)
    }

    // MARK: - GeneralSettingsManager

    /// The property the backup export reads is a cache: reading it must not
    /// reach the login item at all.
    @Test func readingTheCachedValueNeverTouchesTheLoginItem() {
        let fake = FakeLoginItem(status: .enabled)
        let manager = GeneralSettingsManager(launchAtLoginService: fake.service)

        _ = manager.launchAtLogin

        #expect(fake.snapshot.statusReads == 0)
    }

    @Test func refreshStoresTheValueItReadOffTheMainThread() async {
        let fake = FakeLoginItem(status: .requiresApproval)
        let manager = GeneralSettingsManager(launchAtLoginService: fake.service)

        let actual = await manager.refreshLaunchAtLogin()

        #expect(actual)
        #expect(manager.launchAtLogin)
        #expect(fake.snapshot.callsOnMainThread == 0)
    }

    @Test func aWriteStoresTheStateTheSystemReportsNotTheRequestedOne() async {
        let fake = FakeLoginItem(status: .notRegistered, rejectsWrites: true)
        let manager = GeneralSettingsManager(launchAtLoginService: fake.service)

        let actual = await manager.setLaunchAtLogin(true)

        #expect(!actual)
        #expect(!manager.launchAtLogin)
        #expect(fake.snapshot.registers == 1)
        #expect(fake.snapshot.callsOnMainThread == 0)
    }
}
