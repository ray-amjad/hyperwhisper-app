//
//  AppHangFingerprintTests.swift
//  hyperwhisperTests
//
//  An SDK-raised AppHang event never travels the `capture(error:)` path, so it
//  never got a fingerprint. Every hang with no HyperWhisper frame below
//  `HyperWhisperApp.$main` therefore merged into one Sentry issue —
//  HYPERWHISPER-F7, 191 events from 40 users, holding seven unrelated blocking
//  sites. Two pure functions split it: `hangFingerprintComponents` picks the
//  frame to group on, and `hangGrouping` turns that frame into the fingerprint
//  array and the tag pair. This file pins both. Issue #683.
//
//  It pins the DECISION only. It starts no SDK, so it needs no DSN and no
//  network. Both functions take plain Strings for the same reason
//  `store(for:)` takes a `DiagnosticSeverity`: this target does not link the
//  Sentry package, so a test cannot name `Frame` at all.
//
//  The only thing read off disk instead of called is the last case, which pins
//  the `beforeSend` wiring — see its own comment for why that is the last
//  resort and not the first.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("App hang fingerprint")
struct AppHangFingerprintTests {

    /// The deepest non-trap frame wins, and it is the LAST one.
    ///
    /// Sentry stores frames caller-to-callee, oldest first, so the frame the
    /// thread is stopped in is at the end. The real app and AppKit frames at the
    /// top are load-bearing: a walk that runs forward instead of backward
    /// returns `("HyperWhisper", "main")` here — which is HYPERWHISPER-F7 all
    /// over again, under a new hash. Do not drop them to "simplify" the vector.
    @Test func deepestNonTrapFrameWins() {
        let frames: [(image: String, symbol: String)] = [
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "main"),
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "HyperWhisperApp.$main"),
            ("/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit", "-[NSApplication run]"),
            ("SkyLight", "SLSDisplayGetTiming"),
            ("libsystem_kernel.dylib", "mach_msg"),
            ("libsystem_kernel.dylib", "mach_msg2_trap")
        ]

        let site = SentryService.hangFingerprintComponents(imagesAndSymbols: frames)

        #expect(site?.image == "SkyLight")
        #expect(site?.symbol == "SLSDisplayGetTiming")
    }

    /// A SwiftUI hang lands in a SwiftUI bucket, not in SkyLight's.
    ///
    /// This is the issue's "two of the seven buckets no longer collide": the
    /// SkyLight vector above and the SwiftUI vector here share every in-app
    /// frame, which is exactly why Sentry merged them. It also carries the
    /// basename derivation — a full framework path goes in, `"SwiftUI"` comes
    /// out — because `frame.package` is a full dyld path in production.
    @Test func aSwiftUIBlockingSiteGetsItsOwnBucket() {
        let swiftUIFrames: [(image: String, symbol: String)] = [
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "main"),
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "HyperWhisperApp.$main"),
            ("/System/Library/Frameworks/SwiftUI.framework/Versions/A/SwiftUI", "Set.contains"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]
        let skyLightFrames: [(image: String, symbol: String)] = [
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "main"),
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "HyperWhisperApp.$main"),
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight",
             "SLSGetRealtimeDisplayInfoShmem"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]

        let swiftUI = SentryService.hangFingerprintComponents(imagesAndSymbols: swiftUIFrames)
        let skyLight = SentryService.hangFingerprintComponents(imagesAndSymbols: skyLightFrames)

        #expect(swiftUI?.image == "SwiftUI")
        #expect(swiftUI?.symbol == "Set.contains")
        #expect(skyLight?.image == "SkyLight")
        #expect(swiftUI?.image != skyLight?.image)
    }

    /// Nothing but trap frames yields nil, whether they arrive as bare names or
    /// as full paths. An empty list yields nil too.
    ///
    /// The full-path half is the one that matters: matching the skip list
    /// against the raw `frame.package` passes the bare-name half and then fails
    /// on every real event, leaving `mach_msg2_trap` as the fingerprint.
    @Test func aKernelOnlyStackYieldsNil() {
        let bareNames: [(image: String, symbol: String)] = [
            ("libsystem_pthread.dylib", "_pthread_cond_wait"),
            ("libsystem_kernel.dylib", "__psynch_cvwait")
        ]
        let fullPaths: [(image: String, symbol: String)] = [
            ("/usr/lib/system/libsystem_platform.dylib", "_platform_memmove"),
            ("/usr/lib/system/libsystem_pthread.dylib", "_pthread_cond_wait"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]
        let empty: [(image: String, symbol: String)] = []

        #expect(SentryService.hangFingerprintComponents(imagesAndSymbols: bareNames) == nil)
        #expect(SentryService.hangFingerprintComponents(imagesAndSymbols: fullPaths) == nil)
        #expect(SentryService.hangFingerprintComponents(imagesAndSymbols: empty) == nil)
    }

    /// A found site fingerprints on FOUR elements — image and symbol appended
    /// to Sentry's own default component — and tags the same pair.
    ///
    /// This is the case the whole issue exists for, and it was the one nothing
    /// pinned: with the array literal written inline in `beforeSend`, deleting
    /// the assignment left all five tests green while every hang re-merged into
    /// HYPERWHISPER-F7. Delete or reorder the success `fingerprint` in
    /// `hangGrouping` and this case fails.
    @Test func aFoundSiteFingerprintsOnImageAndSymbol() {
        let frames: [(image: String, symbol: String)] = [
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "HyperWhisperApp.$main"),
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight",
             "SLSDisplayGetTiming"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]

        let grouping = SentryService.hangGrouping(imagesAndSymbols: frames)

        #expect(grouping.fingerprint == ["{{ default }}", "app_hang", "SkyLight", "SLSDisplayGetTiming"])
        #expect(grouping.fingerprint.count == 4)
        #expect(grouping.image == "SkyLight")
        #expect(grouping.symbol == "SLSDisplayGetTiming")
    }

    /// Two blocking sites under one in-app stack get two different fingerprints.
    ///
    /// The issue's `## Done when`, at the level this target can execute: these
    /// two vectors share every frame Sentry's own grouping looks at, which is
    /// precisely why all 191 events merged.
    @Test func twoBlockingSitesGetTwoFingerprints() {
        let swiftUI: [(image: String, symbol: String)] = [
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "HyperWhisperApp.$main"),
            ("/System/Library/Frameworks/SwiftUI.framework/Versions/A/SwiftUI", "Set.contains"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]
        let skyLight: [(image: String, symbol: String)] = [
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "HyperWhisperApp.$main"),
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight",
             "SLSGetRealtimeDisplayInfoShmem"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]

        let a = SentryService.hangGrouping(imagesAndSymbols: swiftUI)
        let b = SentryService.hangGrouping(imagesAndSymbols: skyLight)

        #expect(a.fingerprint != b.fingerprint)
        #expect(a.fingerprint == ["{{ default }}", "app_hang", "SwiftUI", "Set.contains"])
        #expect(b.fingerprint == ["{{ default }}", "app_hang", "SkyLight", "SLSGetRealtimeDisplayInfoShmem"])
    }

    /// An empty symbol becomes `"unknown"`, in the fingerprint AND in the tag —
    /// and the array is still the FOUR-element found-site shape.
    ///
    /// This is the ORDINARY production shape, not an edge case: the ANR stack is
    /// captured with `symbolicate = options.debug`, `SentryService.initialize`
    /// never sets `options.debug`, and the SDK default is `NO`. So
    /// `frame.function` is nil on device and `frame.package` is not — the symbol
    /// names in an issue's stack trace are Sentry's server-side symbolication,
    /// produced long after `beforeSend` ran. Drop the
    /// `symbol.isEmpty ? "unknown" : symbol` substitution in `hangGrouping` and
    /// this case fails; the shipped split is by image, with a readable symbol
    /// slot rather than an empty one.
    @Test func anEmptySymbolBecomesUnknownWithoutFallingBackToTheThreeElementShape() {
        let frames: [(image: String, symbol: String)] = [
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", ""),
            ("libsystem_kernel.dylib", "")
        ]

        let grouping = SentryService.hangGrouping(imagesAndSymbols: frames)

        #expect(grouping.fingerprint == ["{{ default }}", "app_hang", "SkyLight", "unknown"])
        #expect(grouping.symbol == "unknown")
        #expect(grouping.image == "SkyLight")
    }

    /// No surviving frame is the issue's step 6 literal: THREE elements, and
    /// both tags still set so the bucket is searchable.
    @Test func aKernelOnlyStackFallsBackToTheUnknownBucket() {
        let frames: [(image: String, symbol: String)] = [
            ("/usr/lib/system/libsystem_pthread.dylib", "_pthread_cond_wait"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]

        let grouping = SentryService.hangGrouping(imagesAndSymbols: frames)

        #expect(grouping.fingerprint == ["{{ default }}", "app_hang", "unknown"])
        #expect(grouping.fingerprint.count == 3)
        #expect(grouping.image == "unknown")
        #expect(grouping.symbol == "unknown")
    }

    /// `beforeSend` puts that decision on the event — and on nothing else.
    ///
    /// Source-scraping, which `ProductionSource`'s own header calls the LAST
    /// RESORT: it proves a symbol is *mentioned*, never that it is used
    /// correctly. Everything that can be lifted into a callable pure function
    /// now has been — `hangFingerprintComponents` picks the frame,
    /// `hangGrouping` decides the values, and the cases above call both. What is
    /// left is wiring inside a closure this target cannot invoke: `beforeSend`
    /// lives in `initialize()` and needs the Sentry package this target does not
    /// link, a DSN and a live SDK. So the four things extraction MOVED the risk
    /// onto are read here: the AppHang guard, the frame read, the call, and the
    /// two assignments. Delete any one of them and this case fails.
    @Test func beforeSendWritesTheGroupingOntoTheEvent() throws {
        let body = try Self.beforeSendBody()

        #expect(body.contains("mechanism?.type == \"AppHang\""),
                "the AppHang branch must be inside beforeSend — nothing else on the event reaches it")
        #expect(body.contains("(image: $0.package ?? \"\", symbol: $0.function ?? \"\")"),
                "frames feed the helper as (package, function); `module` is nil for every Cocoa frame")
        #expect(body.contains("Self.hangGrouping(imagesAndSymbols:"),
                "beforeSend must call the tested decision, not a second inline copy of it")
        #expect(body.contains("event.fingerprint = grouping.fingerprint"),
                "the fingerprint must reach the event — without this line every hang re-merges")
        #expect(body.contains("tags[\"hang_image\"] = grouping.image"),
                "the image tag carries the grouping's own image")
        #expect(body.contains("tags[\"hang_symbol\"] = grouping.symbol"),
                "the symbol tag carries the grouping's own symbol")
        #expect(body.contains("event.tags = tags"),
                "tags are copy-mutate-assign; `event.tags?[k] = v` is a no-op when tags is nil")
    }

    /// An unsymbolicated frame still groups by its image.
    ///
    /// The frame-picking half of the production shape: with symbolication off on
    /// device, `frame.function` is nil, so an empty symbol must NOT disqualify a
    /// frame — only a trap image does. The empty string that comes back here is
    /// what `hangGrouping` then rewrites to `"unknown"`; that rewrite is pinned
    /// by `anEmptySymbolBecomesUnknownWithoutFallingBackToTheThreeElementShape`.
    @Test func unsymbolicatedFrameStillGroupsByImage() {
        let frames: [(image: String, symbol: String)] = [
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", ""),
            ("libsystem_kernel.dylib", "")
        ]

        let site = SentryService.hangFingerprintComponents(imagesAndSymbols: frames)

        #expect(site?.image == "SkyLight")
        #expect(site?.symbol == "")
    }
}

extension AppHangFingerprintTests {

    fileprivate static let servicePath =
        "app/macos/hyperwhisper/Utilities/SentryService.swift"

    /// The body of `options.beforeSend`, ending where the next hook begins.
    ///
    /// If either anchor stops matching, `ProductionSource.Failure.anchorNotFound`
    /// throws with a message telling the next reader to update the anchor rather
    /// than delete the check. That is the intended failure mode — do not reach
    /// for `try?`.
    fileprivate static func beforeSendBody() throws -> String {
        try ProductionSource.slice(
            of: servicePath,
            from: "options.beforeSend = { event in",
            to: "options.beforeSendLog ="
        )
    }
}
