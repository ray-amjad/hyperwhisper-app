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
//  The Windows app mirrors this table in
//  `Utilities/EnglishSpellingRegionDefault.cs` — change both together.
//

import Foundation

extension EnglishSpelling {

    /// Regions whose written English follows Canadian spelling.
    private static let canadianRegions: Set<String> = ["CA"]

    /// Regions whose written English follows Australian spelling.
    private static let australianRegions: Set<String> = [
        "AU", "CC", "CX", "NF"
    ]

    /// Regions whose written English follows British spelling. New Zealand,
    /// Ireland and South Africa sit here because the app offers no separate
    /// variant for them and British is the closest of the four.
    private static let britishRegions: Set<String> = [
        // British Isles and Europe
        "GB", "IE", "IM", "JE", "GG", "GI", "MT", "CY",
        // Africa
        "ZA", "NG", "GH", "KE", "UG", "TZ", "RW", "ZM", "ZW", "BW", "NA",
        "MW", "MU", "SC", "SZ", "LS", "GM", "SL", "SS",
        // South and South-East Asia
        "IN", "PK", "BD", "LK", "NP", "BT", "MV", "SG", "MY", "BN", "HK",
        // Caribbean and South Atlantic
        "JM", "TT", "BB", "BS", "BZ", "GY", "AG", "DM", "GD", "KN", "LC",
        "VC", "VG", "KY", "TC", "MS", "AI", "BM", "FK", "SH",
        // Oceania
        "NZ", "FJ", "PG", "SB", "VU", "WS", "TO", "KI", "TV", "NR", "CK",
        "NU", "TK"
    ]

    /// The spelling variant to seed into a new mode, from the system region.
    static var defaultForCurrentRegion: EnglishSpelling {
        forRegion(Locale.current.region?.identifier)
    }

    /// Maps an ISO 3166-1 alpha-2 region code to a spelling variant.
    /// An unknown, empty or missing code gives `.american`, the value the app
    /// used for every mode before this table existed.
    static func forRegion(_ regionCode: String?) -> EnglishSpelling {
        let code = (regionCode ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .uppercased()
        guard !code.isEmpty else { return .american }

        if canadianRegions.contains(code) { return .canadian }
        if australianRegions.contains(code) { return .australian }
        if britishRegions.contains(code) { return .british }
        return .american
    }
}
