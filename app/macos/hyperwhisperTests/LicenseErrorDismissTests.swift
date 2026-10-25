//
//  LicenseErrorDismissTests.swift
//  hyperwhisperTests
//
//  Issue #642: after a failed activation, Settings → HyperWhisper Cloud kept the
//  red "License key not found" line under an emptied, edited or re-opened key
//  field, because nothing cleared `LicenseManager.lastError`. The section now
//  calls `dismissLastError()` when the field changes and when the page closes.
//
//  These drive a real `LicenseManager` through a failed `activateLicense`, the
//  same call the Activate button makes, so `lastError` is set by production code.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct LicenseErrorDismissTests {

    private static let notFoundMessage = "License key not found"

    private func makeLicenseManager() -> LicenseManager {
        LicenseManager(
            networkService: StubLicenseNetwork(result: LicenseValidationResult(
                isValid: false,
                status: .invalid,
                customerId: nil,
                customerEmail: nil,
                customerName: nil,
                errorMessage: Self.notFoundMessage
            )),
            loadStoredLicenseOnInit: false,
            notificationCenter: NotificationCenter()
        )
    }

    @Test func failedActivationErrorIsDroppedOnDismiss() async {
        let manager = makeLicenseManager()

        let result = await manager.activateLicense("HW-AAAA-BBBB-CCCC-DDDD")
        #expect(result.isValid == false)
        #expect(manager.lastError == Self.notFoundMessage)

        manager.dismissLastError()
        #expect(manager.lastError == nil)
    }

    @Test func dismissKeepsTheErrorWhileACheckIsInFlight() {
        let manager = makeLicenseManager()
        manager.lastError = Self.notFoundMessage
        manager.isValidating = true

        manager.dismissLastError()
        #expect(manager.lastError == Self.notFoundMessage)
    }
}
