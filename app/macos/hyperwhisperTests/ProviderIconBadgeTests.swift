//
//  ProviderIconBadgeTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1448.
//
//  A free local model that was only not downloaded yet (`.downloadable`) wore
//  the same `lock.fill` badge as a `.locked` model, and the Model Library
//  legend says that padlock means "Locked". Ray's call: a not-downloaded local
//  model shows no badge at all; the padlock stays for `.locked` only.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Provider icon badge (#1448)")
struct ProviderIconBadgeTests {

    @Test("A not-downloaded local model has no badge")
    func downloadableHasNoBadge() {
        #expect(ProviderIconView.badgeSymbol(for: .downloadable(progress: nil)) == nil)
        #expect(ProviderIconView.badgeSymbol(for: .downloadable(progress: 0.4)) == nil)
    }

    @Test("Only a locked model wears the padlock")
    func lockedKeepsPadlock() {
        #expect(ProviderIconView.badgeSymbol(for: .locked) == "lock.fill")
        #expect(ProviderIconView.badgeSymbol(for: .enabled) == nil)
        #expect(ProviderIconView.badgeSymbol(for: .error("bad key")) == "exclamationmark.triangle.fill")
    }
}
