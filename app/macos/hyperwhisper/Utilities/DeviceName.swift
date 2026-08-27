//
//  DeviceName.swift
//  hyperwhisper
//
//  The user-facing name of this Mac ("Ray's MacBook Pro"), resolved ONCE and
//  cached for the lifetime of the process.
//
//  WHY THIS FILE EXISTS (issue #313)
//  --------------------------------
//  Two call sites used to ask Foundation for the machine name:
//
//      Host.current().name              // PromptBuilder.makeContext
//      ProcessInfo.processInfo.hostName // LicenseNetworkService (activation)
//
//  Both funnel into `-[NSHost name]`, which calls `blockingResolveUntil:` — a
//  SYNCHRONOUS `.local` mDNS resolution on the calling thread. From a plain
//  command-line binary that returns in ~4 ms, which is why no unit test and no
//  scratch harness ever caught it. Inside the shipped, signed, sandboxed app it
//  blocks for ~35 SECONDS. `PromptBuilder.makeContext` is `@MainActor` and runs
//  twice per post-processing request, so every request paid a fixed ~70 s
//  MainActor stall before a single byte went to the provider.
//
//  Those two were also the only mDNS-shaped calls in the whole macOS app, and
//  therefore the reason the system asked users to let HyperWhisper "find devices
//  on your local network" (#319). Nothing here needs the network: both callers
//  want a human-readable label for this Mac.
//
//  WHY SCDynamicStoreCopyComputerName
//  ----------------------------------
//  It is the direct read of the same value System Settings → General → About
//  shows and the user typed there. It is a local dynamic-store lookup: no DNS,
//  no Bonjour, no network of any kind, ~1 ms. `Host.current().localizedName` is
//  a thin wrapper over exactly this call and would work too, but it keeps an
//  `NSHost` in the source next to the `.name` accessor that caused #313 —
//  reading the value at its actual source removes that trap entirely.
//
//  DO NOT replace this with `Host.current().name`, `NSHost`,
//  `ProcessInfo.processInfo.hostName`, or `gethostname(2)`. The first three
//  reintroduce the stall; `gethostname` returns the DNS-shaped name, which is
//  not what either caller wants (see the note on format below).
//
//  FORMAT
//  ------
//  `SCDynamicStoreCopyComputerName` returns the friendly name — "Ray's MacBook
//  Pro". The old calls returned the DNS/Bonjour form — "Rays-MacBook-Pro.local".
//  Same machine, different spelling. Both consumers want the friendly form: it
//  is what an LLM can reason about in `<COMPUTER>`, and it is what a user
//  recognises in the licence portal's device list.
//

import Foundation
import SystemConfiguration

/// This Mac's user-facing computer name, resolved once per process.
enum DeviceName {

    /// The cached name, e.g. `"Ray's MacBook Pro"`.
    ///
    /// A Swift `static let` is initialised lazily and exactly once, under the
    /// runtime's own one-time guard, so concurrent first readers from any actor
    /// or thread are safe and only one of them performs the lookup. Every read
    /// after that is a plain load.
    ///
    /// The name is captured at first use and never refreshed. Renaming a Mac is
    /// rare, requires an admin, and the worst case is a stale label in a prompt
    /// or in the licence portal until the app is next launched — a trade the
    /// hot path (twice per post-processing request) is worth making.
    static let current: String = resolve()

    /// Reads the computer name from the system configuration dynamic store.
    ///
    /// Returns `"Unknown"` when the store has no name to give — the same
    /// fallback string the previous `Host.current().name ?? "Unknown"` produced,
    /// so the prompt template's `<COMPUTER>` field keeps its old empty-case
    /// wording.
    private static func resolve() -> String {
        guard let copied = SCDynamicStoreCopyComputerName(nil, nil) else {
            return "Unknown"
        }
        let name = (copied as String).trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? "Unknown" : name
    }
}
