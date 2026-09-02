//
//  RustRetryBudgetTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct RustRetryBudgetTests {

    @Test func jitteredSleepCannotExceedTheRemainingBudget() {
        let admitted = RustRetry.admittedSleepMs(
            coreDelayMs: 10_000,
            sleptMs: 20_000,
            budgetMs: 30_000,
            jitterUnit: 1
        )

        #expect(admitted == 10_000)
        #expect(20_000 + admitted == 30_000)
    }

    @Test func unboundedSleepKeepsTheFullJitter() {
        let admitted = RustRetry.admittedSleepMs(
            coreDelayMs: 10_000,
            sleptMs: 20_000,
            budgetMs: 0,
            jitterUnit: 1
        )

        #expect(admitted == 13_000)
    }
}
