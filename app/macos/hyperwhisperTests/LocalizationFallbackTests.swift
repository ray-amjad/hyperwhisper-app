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

    /// A key introduced by the redesigned onboarding flow, used here only to
    /// prove Base carries it.
    private let newOnboardingKey = "onboarding.welcome.task1.title"

    private func bundle(forLocalization name: String) -> Bundle? {
        guard let path = Bundle.main.path(forResource: name, ofType: "lproj") else { return nil }
        return Bundle(path: path)
    }

    /// A throwaway strings table that deliberately omits `absentKey`.
    ///
    /// The untranslated case cannot be pinned to a shipped locale: the moment
    /// the translations land, the key is present and the test that guards
    /// incomplete locales starts failing. Completing a locale must never break
    /// the safety net for the next incomplete one, so the missing entry is
    /// manufactured here instead of borrowed from the app bundle.
    private func bundleMissing(_ absentKey: String, carrying presentKey: String) throws -> (Bundle, URL) {
        let root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("hw-loc-fallback-\(UUID().uuidString).lproj", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try "\"\(presentKey)\" = \"Locale wins\";\n"
            .write(to: root.appendingPathComponent("Localizable.strings"), atomically: true, encoding: .utf8)
        return (try #require(Bundle(path: root.path)), root)
    }

    @Test func theAppShipsABaseStringsTable() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        #expect(base.localizedValueIfPresent(forKey: newOnboardingKey) != nil)
    }

    @Test func anUntranslatedKeyResolvesToTheBaseValueRatherThanTheIdentifier() throws {
        let base = try #require(BaseLocalizationBundle.resolve(in: .main))
        let (incomplete, root) = try bundleMissing(newOnboardingKey, carrying: "common.back")
        defer { try? FileManager.default.removeItem(at: root) }

        // Precondition: this table genuinely has no entry, so we are testing the
        // fallback and not an accidental translation.
        try #require(incomplete.localizedValueIfPresent(forKey: newOnboardingKey) == nil)

        let resolved = newOnboardingKey.localizedValue(in: incomplete, fallingBackTo: base)
        #expect(resolved != newOnboardingKey)
        #expect(resolved == base.localizedValueIfPresent(forKey: newOnboardingKey))
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
        // Resolved against a table that carries none of these, so a pass means
        // the fallback produced the value rather than a translation that happens
        // to be present today.
        let (incomplete, root) = try bundleMissing(newOnboardingKey, carrying: "common.back")
        defer { try? FileManager.default.removeItem(at: root) }

        let sampled = [
            "onboarding.welcome.task1.title",
            "onboarding.mic.soundSettings",
            "onboarding.footer.reassurance.welcome",
            "onboarding.a11y.percent",
            "onboarding.setup.cloud.error"
        ]

        for key in sampled {
            let resolved = key.localizedValue(in: incomplete, fallingBackTo: base)
            #expect(resolved != key, "\(key) still renders as its identifier")
            #expect(resolved == base.localizedValueIfPresent(forKey: key))
        }
    }
}
