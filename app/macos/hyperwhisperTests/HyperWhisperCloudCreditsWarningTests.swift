//
//  HyperWhisperCloudCreditsWarningTests.swift
//  hyperwhisperTests
//
//  Issue #2618: the server floors `minutes_remaining`
//  (hyperwhisper-cloud/src/routes/usage.ts, floor(credits / 1.67)), so a balance
//  under one minute arrives as 0 minutes with credits still above 0. Settings
//  (CloudAccountSettingsSection) colours the number and shows a banner only when
//  `isLow` or `isExhausted` is true, so that balance must count as low.
//
//  The Windows counterpart lives in
//  HyperWhisper.SmokeTests/CloudCreditsWarningChecks.cs.
//

import Foundation
import Testing
@testable import HyperWhisper

struct HyperWhisperCloudCreditsWarningTests {
    /// Decodes the /usage payload the way the app does, with the server's own
    /// rounding and flooring of minutes.
    private func usage(credits: Double) throws -> HyperWhisperCloudCredits {
        let normalized = (credits * 10).rounded() / 10
        let minutes = Int((normalized / 1.67).rounded(.down))
        let json = """
        {"credits_remaining":\(normalized),"minutes_remaining":\(minutes),\
        "credits_per_minute":1.67,"is_licensed":true,"is_anonymous":false}
        """
        return try JSONDecoder().decode(HyperWhisperCloudCredits.self, from: Data(json.utf8))
    }

    private func showsAnyWarning(_ credits: HyperWhisperCloudCredits) -> Bool {
        (credits.isLow && !credits.isExhausted) || credits.isExhausted
    }

    @Test("A balance under one minute (1.5 credits, ~54 s) shows the low warning")
    func underOneMinuteIsLow() throws {
        let credits = try usage(credits: 1.5)
        #expect(credits.minutesRemaining == 0)
        #expect(credits.isLow)
        #expect(!credits.isExhausted)
        #expect(showsAnyWarning(credits))
    }

    @Test("Control: 5 credits (2 min) is low, 20 credits (11 min) is not")
    func lowThreshold() throws {
        #expect(try usage(credits: 5).isLow)
        let plenty = try usage(credits: 20)
        #expect(plenty.minutesRemaining == 11)
        #expect(!showsAnyWarning(plenty))
    }

    @Test("Zero and negative balances are exhausted, not low")
    func exhaustedIsNotLow() throws {
        for value in [0.0, -1.0] {
            let credits = try usage(credits: value)
            #expect(credits.isExhausted)
            #expect(!credits.isLow)
            #expect(showsAnyWarning(credits))
        }
    }
}
