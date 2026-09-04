//
//  AudioDeviceNotificationRefreshTests.swift
//  hyperwhisperTests
//

import Foundation
import os
import Testing
@testable import HyperWhisper

private let notificationFirstDevice = AudioDevice(id: "first", name: "First", uid: "first")
private let notificationSecondDevice = AudioDevice(id: "second", name: "Second", uid: "second")

private func notificationSnapshot(
    devices: [AudioDevice],
    selectedDeviceUID: String? = nil,
    activeUID: String? = nil,
    activeName: String? = nil
) -> AudioDeviceNotificationSnapshot {
    AudioDeviceNotificationSnapshot(
        devices: devices,
        systemDefaultDeviceUID: devices.first?.uid,
        selectedDeviceUID: selectedDeviceUID,
        inputVolumeScalar: 0.5,
        activeInputDeviceIdentifier: activeUID ?? devices.first?.uid,
        activeInputDeviceName: activeName ?? devices.first?.name
    )
}

private final class NotificationScanGate: @unchecked Sendable {
    private let semaphore = DispatchSemaphore(value: 0)

    func wait() {
        _ = semaphore.wait(timeout: .now() + 5)
    }

    func release() {
        semaphore.signal()
    }
}

private final class NotificationScanProbe: @unchecked Sendable {
    private struct State {
        var calls = 0
        var activeCalls = 0
        var maximumActiveCalls = 0
        var didRunOnMainThread = false
        var reports: [AudioDeviceNotificationScanReport] = []
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    var calls: Int { state.withLock { $0.calls } }
    var didRunOnMainThread: Bool { state.withLock { $0.didRunOnMainThread } }
    var maximumActiveCalls: Int { state.withLock { $0.maximumActiveCalls } }
    var reports: [AudioDeviceNotificationScanReport] { state.withLock { $0.reports } }

    func beginCall() -> Int {
        state.withLock {
            $0.calls += 1
            $0.activeCalls += 1
            $0.maximumActiveCalls = max($0.maximumActiveCalls, $0.activeCalls)
            $0.didRunOnMainThread = $0.didRunOnMainThread || Thread.isMainThread
            return $0.calls
        }
    }

    func endCall() {
        state.withLock { $0.activeCalls -= 1 }
    }

    func record(_ report: AudioDeviceNotificationScanReport) {
        state.withLock { $0.reports.append(report) }
    }
}

@MainActor
struct AudioDeviceNotificationRefreshTests {
    private static func waitUntil(
        _ condition: @escaping @Sendable () -> Bool
    ) async {
        for _ in 0..<1_000 {
            if condition() { return }
            try? await Task.sleep(nanoseconds: 1_000_000)
        }
        Issue.record("Timed out while waiting for the fake notification scan")
    }

    @Test func notificationScanRunsOffMainAndLeavesMainActorResponsive() async {
        let probe = NotificationScanProbe()
        let scanGate = NotificationScanGate()
        let manager = AudioDeviceManager(
            registerNotificationListeners: false,
            notificationSnapshotProvider: { _ in
                _ = probe.beginCall()
                scanGate.wait()
                return notificationSnapshot(devices: [notificationFirstDevice])
            }
        )

        let refresh = Task {
            await manager.refreshAvailableDevicesFromNotificationForTesting(reason: .coreAudioDeviceList)
        }
        await Self.waitUntil { probe.calls == 1 }

        // Reaching this assertion while the fake scan is blocked proves the main actor yielded.
        #expect(Thread.isMainThread)
        #expect(probe.didRunOnMainThread == false)

        scanGate.release()
        await refresh.value
        #expect(manager.availableDevices == [notificationFirstDevice])
    }

    @Test func newestNotificationWinsWhenScansFinishOutOfOrder() async {
        let probe = NotificationScanProbe()
        let firstScanGate = NotificationScanGate()
        let manager = AudioDeviceManager(
            registerNotificationListeners: false,
            notificationSnapshotProvider: { _ in
                let call = probe.beginCall()
                if call == 1 {
                    firstScanGate.wait()
                    return notificationSnapshot(devices: [notificationFirstDevice])
                }
                return notificationSnapshot(devices: [notificationSecondDevice])
            },
            notificationScanReporter: { report in probe.record(report) }
        )

        let firstRefresh = Task {
            await manager.refreshAvailableDevicesFromNotificationForTesting(reason: .coreAudioDeviceList)
        }
        await Self.waitUntil { probe.calls == 1 }

        let secondRefresh = Task {
            await manager.refreshAvailableDevicesFromNotificationForTesting(reason: .coreAudioDefaultInput)
        }
        await secondRefresh.value
        #expect(manager.availableDevices == [notificationSecondDevice])

        firstScanGate.release()
        await firstRefresh.value

        #expect(manager.availableDevices == [notificationSecondDevice])
        #expect(probe.reports.count == 2)
        #expect(probe.reports.contains(where: { $0.didPublish }))
        #expect(probe.reports.contains(where: { !$0.didPublish }))
        #expect(probe.reports.allSatisfy { $0.durationMs >= 0 })
    }

    @Test func notificationBurstKeepsAtMostTwoScansAndCoalescesPendingWork() async {
        let probe = NotificationScanProbe()
        let scanGate = NotificationScanGate()
        let manager = AudioDeviceManager(
            registerNotificationListeners: false,
            notificationSnapshotProvider: { _ in
                _ = probe.beginCall()
                scanGate.wait()
                probe.endCall()
                return notificationSnapshot(devices: [notificationFirstDevice])
            },
            notificationScanReporter: { report in probe.record(report) }
        )

        for _ in 0..<100 {
            manager.requestNotificationRefreshForTesting(reason: .coreAudioDeviceList)
        }

        await Self.waitUntil { probe.calls == 2 }
        #expect(probe.maximumActiveCalls == 2)

        scanGate.release()
        scanGate.release()
        await Self.waitUntil { probe.calls == 3 }
        scanGate.release()
        await Self.waitUntil { probe.reports.count == 3 }

        #expect(probe.calls == 3)
        #expect(probe.maximumActiveCalls == 2)
        #expect(probe.reports.filter(\.didPublish).count == 1)
    }

    @Test func synchronousScanInvalidatesAnOlderNotificationResult() async {
        let probe = NotificationScanProbe()
        let scanGate = NotificationScanGate()
        let manager = AudioDeviceManager(
            registerNotificationListeners: false,
            notificationSnapshotProvider: { _ in
                _ = probe.beginCall()
                scanGate.wait()
                return notificationSnapshot(devices: [notificationFirstDevice])
            },
            notificationScanReporter: { report in probe.record(report) }
        )

        let refresh = Task {
            await manager.refreshAvailableDevicesFromNotificationForTesting(reason: .coreAudioDeviceList)
        }
        await Self.waitUntil { probe.calls == 1 }
        manager.invalidateNotificationRefreshesForTesting()
        scanGate.release()
        await refresh.value

        #expect(manager.availableDevices.isEmpty)
        #expect(probe.reports.map(\.didPublish) == [false])
    }

    @Test func missingSelectedDeviceInvalidatesOnceWithoutCoreAudio() async {
        let selected = notificationFirstDevice
        let probe = NotificationScanProbe()
        let manager = AudioDeviceManager(
            registerNotificationListeners: false,
            notificationSnapshotProvider: { selectedUID in
                _ = probe.beginCall()
                return notificationSnapshot(
                    devices: [],
                    selectedDeviceUID: selectedUID,
                    activeUID: "default",
                    activeName: "Default"
                )
            }
        )
        manager.selectedDevice = selected

        var invalidated: [AudioDevice] = []
        manager.onSelectedDeviceInvalidated = { invalidated.append($0) }

        await manager.refreshAvailableDevicesFromNotificationForTesting(reason: .coreAudioDeviceList)
        await manager.refreshAvailableDevicesFromNotificationForTesting(reason: .coreAudioDeviceList)

        #expect(manager.selectedDevice == nil)
        #expect(invalidated == [selected])
        #expect(manager.activeInputDeviceIdentifier == "default")
        #expect(manager.activeInputDeviceName == "Default")
        #expect(probe.calls == 2)
    }

    @Test func selectionChangeDuringScanKeepsNewerSelectionState() async {
        let scanGate = NotificationScanGate()
        let probe = NotificationScanProbe()
        let manager = AudioDeviceManager(
            registerNotificationListeners: false,
            notificationSnapshotProvider: { selectedUID in
                _ = probe.beginCall()
                scanGate.wait()
                return notificationSnapshot(
                    devices: [notificationFirstDevice],
                    selectedDeviceUID: selectedUID,
                    activeUID: "old-active",
                    activeName: "Old Active"
                )
            }
        )
        manager.selectedDevice = notificationFirstDevice
        let initialActiveIdentifier = manager.activeInputDeviceIdentifier
        let initialActiveName = manager.activeInputDeviceName
        var invalidationCount = 0
        manager.onSelectedDeviceInvalidated = { _ in invalidationCount += 1 }

        let refresh = Task {
            await manager.refreshAvailableDevicesFromNotificationForTesting(reason: .coreAudioDefaultInput)
        }
        await Self.waitUntil { probe.calls == 1 }
        manager.selectedDevice = notificationSecondDevice
        scanGate.release()
        await refresh.value

        #expect(manager.selectedDevice == notificationSecondDevice)
        #expect(manager.activeInputDeviceIdentifier == initialActiveIdentifier)
        #expect(manager.activeInputDeviceName == initialActiveName)
        #expect(invalidationCount == 0)
    }
}
