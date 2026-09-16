//
//  AppHangFingerprintTests.swift
//  hyperwhisperTests
//
//  An SDK-raised AppHang event never travels the `capture(error:)` path, so it
//  never got a fingerprint. Every hang with no HyperWhisper frame below
//  `HyperWhisperApp.$main` therefore merged into one Sentry issue —
//  HYPERWHISPER-F7, 191 events from 40 users, holding seven unrelated blocking
//  sites. `SentryService.hangFingerprintComponents(imagesAndSymbols:)` picks the
//  site to group on, and this file pins it. Issue #683.
//
//  It pins the DECISION only. It starts no SDK, so it needs no DSN and no
//  network. The helper takes plain Strings for the same reason
//  `store(for:)` takes a `DiagnosticSeverity`: this target does not link the
//  Sentry package, so a test cannot name `Frame` at all.
//
//  The one thing here that is read off disk instead of called is case 3b — see
//  its own comment for why, and for why that is the last resort and not the
//  first.
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

    /// The caller turns the helper's `nil` into the issue's `"unknown"` bucket.
    ///
    /// Source-scraping, which `ProductionSource`'s own header calls the LAST
    /// RESORT: it proves a symbol is *mentioned*, never that it is used
    /// correctly. Everything that can be lifted into a callable pure function
    /// already has been — that is `hangFingerprintComponents`, and the other
    /// cases call it. What is left is wiring inside a closure this target cannot
    /// invoke: `beforeSend` lives in `initialize()` and needs the Sentry package
    /// this target does not link, a DSN and a live SDK. `## Done when` names it
    /// anyway, so it is read.
    @Test func theCallerFallsBackToUnknownWhenNoFrameSurvives() throws {
        let body = try Self.beforeSendBody()

        #expect(body.contains("mechanism?.type == \"AppHang\""),
                "the AppHang branch must be inside beforeSend — nothing else on the event reaches it")
        #expect(body.contains("Self.hangFingerprintComponents(imagesAndSymbols:"),
                "beforeSend must call the tested helper, not a second inline copy of the walk")
        #expect(body.contains("[\"{{ default }}\", \"app_hang\", \"unknown\"]"),
                "the no-survivor fallback is the issue's step 6 literal, three elements")
        #expect(body.contains("tags[\"hang_image\"]") && body.contains("tags[\"hang_symbol\"]"),
                "both tags are set on every AppHang branch, including the unknown one")
    }

    /// An unsymbolicated frame still groups by its image.
    ///
    /// This is the ORDINARY production shape, not an edge case: the ANR stack is
    /// captured with `symbolicate = options.debug`, `SentryService.initialize`
    /// never sets `options.debug`, and the SDK default is `NO`. So
    /// `frame.function` is nil on device and `frame.package` is not — the symbol
    /// names in an issue's stack trace are Sentry's server-side symbolication,
    /// produced long after `beforeSend` ran. The split that ships is by image.
    /// The caller's `symbol.isEmpty ? "unknown" : symbol` is what makes the
    /// empty string below readable in the `hang_symbol` tag.
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
