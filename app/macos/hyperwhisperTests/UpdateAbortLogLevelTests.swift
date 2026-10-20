//
//  UpdateAbortLogLevelTests.swift
//  hyperwhisperTests
//
//  Sparkle ends a healthy "you're up to date" check through
//  `updater(_:didAbortWithError:)` with `SUNoUpdateError` (code 1001). The
//  delegate logged CRITICAL "Update aborted with error" before it read the
//  code, so every launch of an up-to-date app wrote a CRITICAL line to
//  updates.log. `AppDelegate.isNoUpdateAbort` now picks the INFO branch.
//  This file pins that decision: 1001 is the healthy outcome, and every real
//  Sparkle failure code still is not. Issue #680.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Update abort log level")
struct UpdateAbortLogLevelTests {

    private func sparkleError(_ code: Int) -> NSError {
        NSError(domain: "SUSparkleErrorDomain", code: code, userInfo: [
            NSLocalizedDescriptionKey: "You're up to date!"
        ])
    }

    /// The healthy outcome from the issue's evidence: no update found.
    @Test func noUpdateFoundIsNotAnAbort() {
        #expect(AppDelegate.isNoUpdateAbort(sparkleError(1001)))
    }

    /// Real failures keep their CRITICAL / ERROR lines.
    @Test(arguments: [1002, 1003, 2000, 2001, 3000, 4000, 5000])
    func realSparkleFailuresStillLog(code: Int) {
        #expect(!AppDelegate.isNoUpdateAbort(sparkleError(code)))
    }

    /// Code 1001 means "no update" only in Sparkle's domain.
    @Test func sameCodeInAnotherDomainIsAFailure() {
        let other = NSError(domain: NSCocoaErrorDomain, code: 1001)
        #expect(!AppDelegate.isNoUpdateAbort(other))
    }
}
