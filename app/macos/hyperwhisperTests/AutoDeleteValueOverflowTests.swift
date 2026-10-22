//
//  AutoDeleteValueOverflowTests.swift
//  hyperwhisperTests
//
//  #2077: the Storage field accepted any whole number, and the cutoff maths
//  multiplied it in `Int`. 999999999999999 days trapped in `toSeconds`, and the
//  cleanup pass runs at launch, so the app crashed on every start. A Swift
//  arithmetic trap cannot be caught, so on the old code this suite crashes the
//  test process instead of failing an expectation.
//
//  Only the pure unit and snapshot maths are exercised: the manager's setters
//  write `UserDefaults.standard` of the host app (see
//  `AutoDeleteCleanupServiceTests`), so a test must not call them.
//

import Foundation
import Testing
@testable import HyperWhisper

struct AutoDeleteValueOverflowTests {

    /// The values the bug report typed, one per unit, plus `Int.max`.
    private static let hugeValues: [(AutoDeleteTimeUnit, Int)] = [
        (.days, 999_999_999_999_999),
        (.hours, 9_999_999_999_999_999),
        (.minutes, 999_999_999_999_999_999),
        (.days, Int.max),
        (.hours, Int.max),
        (.minutes, Int.max),
    ]

    private static let hundredYears: TimeInterval = 36_500 * 24 * 60 * 60

    /// The repro from #2077: a typed huge value, stored, reaches the launch
    /// cleanup pass's cutoff. It must not trap, and it counts as 100 years.
    @Test func aStoredHugeValueDoesNotCrashTheCleanupPass() {
        let now = Date(timeIntervalSince1970: 1_760_000_000)
        for (unit, value) in Self.hugeValues {
            let snapshot = AutoDeleteSettingsSnapshot(enabled: true, timeUnit: unit, value: value)
            let cutoff = snapshot.deletionCutoffDate(now: now)
            #expect(cutoff == now.addingTimeInterval(-Self.hundredYears), "unit \(unit.rawValue)")
        }
    }

    @Test func toSecondsCapsAtTheUnitMaximum() {
        for unit in AutoDeleteTimeUnit.allCases {
            #expect(unit.toSeconds(unit.maximumValue) == Self.hundredYears)
            #expect(unit.toSeconds(unit.maximumValue + 1) == Self.hundredYears)
            #expect(unit.toSeconds(Int.max) == Self.hundredYears)
        }
    }

    /// Ordinary values keep their old meaning exactly.
    @Test func toSecondsIsUnchangedInRange() {
        #expect(AutoDeleteTimeUnit.minutes.toSeconds(1) == 60)
        #expect(AutoDeleteTimeUnit.hours.toSeconds(24) == 24 * 60 * 60)
        #expect(AutoDeleteTimeUnit.days.toSeconds(30) == 30 * 24 * 60 * 60)
        #expect(AutoDeleteTimeUnit.days.toSeconds(36_500) == Self.hundredYears)
    }

    @Test func clampedValueKeepsOneToTheUnitMaximum() {
        #expect(AutoDeleteTimeUnit.days.maximumValue == 36_500)
        #expect(AutoDeleteTimeUnit.hours.maximumValue == 876_000)
        #expect(AutoDeleteTimeUnit.minutes.maximumValue == 52_560_000)
        for unit in AutoDeleteTimeUnit.allCases {
            #expect(unit.clampedValue(0) == 1)
            #expect(unit.clampedValue(-5) == 1)
            #expect(unit.clampedValue(30) == 30)
            #expect(unit.clampedValue(unit.maximumValue) == unit.maximumValue)
            #expect(unit.clampedValue(unit.maximumValue + 1) == unit.maximumValue)
            #expect(unit.clampedValue(Int.max) == unit.maximumValue)
        }
    }
}
