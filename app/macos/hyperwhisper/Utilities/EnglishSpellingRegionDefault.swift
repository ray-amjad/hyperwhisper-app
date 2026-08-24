//
//  EnglishSpellingRegionDefault.swift
//  hyperwhisper
//
//  Picks the English spelling variant to SEED into a mode the user has not
//  touched yet: the default mode written at first install, and every brand-new
//  mode created afterwards. A user in London had to change "American" to
//  "British" on every mode they made; deriving it from the system region once
//  removes that step.
//
//  This only supplies a STARTING value. An existing mode's stored
//  `englishSpelling` is never re-derived, so a user who already picked a
//  variant (or a mode restored from a backup) keeps it.
//
//  The ISO 3166-1 region table itself lives in the shared Rust core
//  (`hw-text`, `EnglishSpelling::for_region`, reached through
//  `englishSpellingForRegion(region:)`). macOS, Windows and the portable .NET
//  head all read that one table, so there is no longer a copy to keep in sync —
//  only the platform's own way of asking the OS for its region.
//

import Foundation

extension EnglishSpelling {

    /// The spelling variant to seed into a new mode, from the system region.
    static var defaultForCurrentRegion: EnglishSpelling {
        forRegion(Locale.current.region?.identifier)
    }

    /// Maps an ISO 3166-1 alpha-2 region code to a spelling variant.
    /// An unknown, empty or missing code gives `.american`, the value the app
    /// used for every mode before this table existed.
    ///
    /// The core's `HwEnglishSpelling` carries a fifth `.none` case that this
    /// four-case enum has no room for. `.none` means "emit no spelling
    /// instruction at all", which is never a thing to seed, so `for_region` is
    /// documented and tested never to return it — the `?? .american` below is a
    /// defensive arm for an impossible value, not a fallback with behaviour
    /// riding on it.
    static func forRegion(_ regionCode: String?) -> EnglishSpelling {
        let raw = englishSpellingRawValue(
            spelling: englishSpellingForRegion(region: regionCode))
        return EnglishSpelling(rawValue: raw) ?? .american
    }
}
