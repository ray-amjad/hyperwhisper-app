//
//  LocalizedString.swift
//  hyperwhisper
//
//  Created by Localization Support
//
//  Helper extensions for working with localized strings throughout the app.
//

import SwiftUI

// MARK: - Base localization fallback

/// Sentinel returned by a bundle lookup when the key is genuinely absent.
/// Deliberately unprintable so it can never collide with a real translation.
private let localizationMissingMarker = "\u{1}hw.localization.missing\u{1}"

/// The development-language strings table (`Base.lproj`, with `en.lproj` as a
/// safety net).
///
/// CFBundle does NOT fall back per key: when a locale ships a
/// `Localizable.strings` that is missing a key, the lookup returns the key
/// itself, so an untranslated string renders in the UI as a raw identifier such
/// as `onboarding.welcome.task1.title`. Resolving through the Base table instead
/// means the worst case for a not-yet-translated key is readable English.
enum BaseLocalizationBundle {
    static let bundle: Bundle? = resolve(in: .main)

    /// Locate the development-language `.lproj` inside `container`.
    static func resolve(in container: Bundle) -> Bundle? {
        for name in ["Base", "en"] {
            if let path = container.path(forResource: name, ofType: "lproj"),
               let bundle = Bundle(path: path) {
                return bundle
            }
        }
        return nil
    }
}

extension Bundle {
    /// Localized value for `key`, or nil when this bundle has no entry for it.
    func localizedValueIfPresent(forKey key: String) -> String? {
        let value = localizedString(forKey: key, value: localizationMissingMarker, table: nil)
        return value == localizationMissingMarker ? nil : value
    }
}

// MARK: - String helpers

extension String {
    /// Look up the localized value for the receiver in the main bundle, falling
    /// back to the Base (development language) table when the active locale has
    /// not translated the key yet. Returns the key only when no table has it.
    var localized: String {
        localizedValue(in: .main, fallingBackTo: BaseLocalizationBundle.bundle)
    }

    /// The resolution `localized` performs, with both bundles injected so the
    /// untranslated-locale path is reachable from tests without changing the
    /// process language.
    func localizedValue(in bundle: Bundle, fallingBackTo base: Bundle?) -> String {
        if let value = bundle.localizedValueIfPresent(forKey: self) {
            return value
        }
        if let fallback = base?.localizedValueIfPresent(forKey: self) {
            return fallback
        }
        return self
    }

    /// Localize and interpolate format arguments.
    /// - Parameter arguments: Values inserted into the localized string.
    /// - Returns: Fully formatted localized string.
    func localized(arguments: CVarArg...) -> String {
        String(format: localized, arguments: arguments)
    }

    /// Localize and interpolate format arguments from an existing array.
    /// - Parameter arguments: Values inserted into the localized string.
    /// - Returns: Fully formatted localized string.
    func localized(arguments: [CVarArg]) -> String {
        String(format: localized, arguments: arguments)
    }
}

// MARK: - Text helpers

extension Text {
    /// Create a `Text` view backed by a localization key.
    ///
    /// When the active locale has the key, this keeps SwiftUI's own
    /// `LocalizedStringKey` path (and therefore its markdown handling). When it
    /// does not, the Base value is rendered verbatim rather than the raw key.
    /// - Parameter key: Localization key in the strings file.
    init(localized key: String) {
        if Bundle.main.localizedValueIfPresent(forKey: key) != nil {
            self.init(LocalizedStringKey(key))
        } else {
            self.init(verbatim: key.localized)
        }
    }

    /// Create a `Text` view backed by a localization key with interpolation.
    /// - Parameters:
    ///   - key: Localization key in the strings file.
    ///   - arguments: Values inserted into the localized string.
    init(localized key: String, arguments: CVarArg...) {
        self.init(key.localized(arguments: arguments))
    }
}
