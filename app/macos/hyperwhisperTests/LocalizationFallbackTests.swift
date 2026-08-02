//
//  LocalizationFallbackTests.swift
//  hyperwhisperTests
//
//  A key that exists in Base.lproj but has not been translated into the active
//  locale used to render in the UI as its raw identifier, because CFBundle does
//  no per key fallback. These tests pin the fallback that fixes it.
//

import Foundation
import Testing
@testable import HyperWhisper

struct LocalizationFallbackTests {

    /// A key introduced by the redesigned onboarding flow. It exists in Base and
    /// is not yet translated anywhere else, which is exactly the case that used
    /// to leak an identifier into the UI.
    private let untranslatedKey = "onboarding.welcome.task1.title"

    private func bundle(forLocalization name: String) -> Bundle? {
        guard let path = Bundle.main.path(forResource: name, ofType: "lproj") else { return nil }
        return Bundle(path: path)
    }

    @Test func theAppShipsABaseStringsTable() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        #expect(base.localizedValueIfPresent(forKey: untranslatedKey) != nil)
    }

    @Test func anUntranslatedKeyResolvesToTheBaseValueRatherThanTheIdentifier() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        let french = try #require(bundle(forLocalization: "fr"))

        // Precondition: this locale genuinely has no entry, so we are testing the
        // fallback and not an accidental translation.
        try #require(french.localizedValueIfPresent(forKey: untranslatedKey) == nil)

        let resolved = untranslatedKey.localizedValue(in: french, fallingBackTo: base)
        #expect(resolved != untranslatedKey)
        #expect(resolved == base.localizedValueIfPresent(forKey: untranslatedKey))
    }

    @Test func atranslatedKeyStillPrefersTheLocaleOverBase() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        let french = try #require(bundle(forLocalization: "fr"))

        // A key both tables carry, so the locale value must win.
        let sharedKey = "accessibility.permission.info"
        let localeValue = try #require(french.localizedValueIfPresent(forKey: sharedKey))

        #expect(sharedKey.localizedValue(in: french, fallingBackTo: base) == localeValue)
    }

    @Test func aKeyNoTableCarriesFallsBackToItself() {
        let missing = "hyperwhisper.test.key.that.does.not.exist"
        #expect(missing.localized == missing)
    }

    @Test func everyNewOnboardingKeyIsRecoverableForAnUntranslatedLocale() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        let french = try #require(bundle(forLocalization: "fr"))

        let sampled = [
            "onboarding.welcome.task1.title",
            "onboarding.mic.soundSettings",
            "onboarding.footer.reassurance.welcome",
            "onboarding.a11y.percent",
            "onboarding.setup.cloud.error"
        ]

        for key in sampled {
            let resolved = key.localizedValue(in: french, fallingBackTo: base)
            #expect(resolved != key, "\(key) still renders as its identifier")
        }
    }
}
