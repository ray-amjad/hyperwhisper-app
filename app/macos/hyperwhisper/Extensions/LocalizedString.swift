//
//  LocalizedString.swift
//  hyperwhisper
//
//  Created by Localization Support
//
//  Helper extensions for working with localized strings throughout the app.
//

import SwiftUI

// MARK: - String helpers

extension String {
    /// Look up the localized value for the receiver in the main bundle.
    var localized: String {
        NSLocalizedString(self, comment: "")
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
    /// - Parameter key: Localization key in the strings file.
    init(localized key: String) {
        self.init(LocalizedStringKey(key))
    }

    /// Create a `Text` view backed by a localization key with interpolation.
    /// - Parameters:
    ///   - key: Localization key in the strings file.
    ///   - arguments: Values inserted into the localized string.
    init(localized key: String, arguments: CVarArg...) {
        self.init(String(format: NSLocalizedString(key, comment: ""), arguments: arguments))
    }
}
