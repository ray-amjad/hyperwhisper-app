//
//  AppHangFingerprintTests.swift
//  hyperwhisperTests
//
//  An SDK-raised AppHang event never travels the `capture(error:)` path, so it
//  never got a fingerprint. Every hang with no HyperWhisper frame below
//  `HyperWhisperApp.$main` therefore merged into one Sentry issue —
//  HYPERWHISPER-F7, 191 events from 40 users, holding seven unrelated blocking
//  sites. Two pure functions split it: `hangFingerprintComponents` picks the
//  frame to group on, and `hangGrouping` turns a mechanism type, that frame and
//  the event's existing tags into the two values `beforeSend` writes back — or
//  into nil, meaning write nothing. This file pins both. Issue #683.
//
//  It pins the DECISION only. It starts no SDK, so it needs no DSN and no
//  network. Both functions take plain Strings for the same reason
//  `store(for:)` takes a `DiagnosticSeverity`: this target does not link the
//  Sentry package, so a test cannot name `Frame` at all.
//
//  The only thing read off disk instead of called is the last case, which pins
//  the reads and writes that cannot be lifted out of the closure — see its own
//  comment for why that is the last resort and not the first.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("App hang fingerprint")
struct AppHangFingerprintTests {

    // MARK: - Picking the frame

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
        #expect(skyLight?.symbol == "SLSGetRealtimeDisplayInfoShmem")
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

    /// An unsymbolicated frame still groups by its image.
    ///
    /// The frame-picking half of the production shape: with symbolication off on
    /// device the symbol carries no name, so it must NOT disqualify a frame —
    /// only a trap image does. What comes back here is the SDK's raw
    /// `"<redacted>"` placeholder, which `hangGrouping` then rewrites; that
    /// rewrite is pinned by `aSymbolThatNamesNothingBecomesUnknown`.
    @Test func unsymbolicatedFrameStillGroupsByImage() {
        let frames: [(image: String, symbol: String)] = [
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", "<redacted>"),
            ("libsystem_kernel.dylib", "<redacted>")
        ]

        let site = SentryService.hangFingerprintComponents(imagesAndSymbols: frames)

        #expect(site?.image == "SkyLight")
        #expect(site?.symbol == "<redacted>")
    }

    // MARK: - The whole decision, including whether to write at all

    /// AN EVENT THAT IS NOT AN APP HANG IS LEFT COMPLETELY ALONE.
    ///
    /// The most dangerous mutation this file exists to stop, and the reason the
    /// mechanism check is a PARAMETER rather than an `if` wrapped round the
    /// call. `beforeSend` runs on every event the SDK sends, and six other sites
    /// set a deliberate custom fingerprint — `SentryService.capture(error:)`,
    /// `AppLogger`, `LocalModelManager`, `HyperWhisperCloudProvider` and
    /// `StreamingTranscriptionClient` twice. Hoist the check out of
    /// `hangGrouping` and every one of those is overwritten on every event they
    /// raise. While the check was an `if` in the closure, nothing in this target
    /// could execute it; now this case does.
    @Test func aNonHangEventIsLeftAlone() {
        let frames: [(image: String, symbol: String)] = [
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight",
             "SLSDisplayGetTiming"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]

        // Every mechanism a non-hang event can arrive with, plus no mechanism at
        // all — which is what a hand-built `capture(error:)` event carries.
        let others: [String?] = [nil, "", "generic", "NSException", "apphang", "AppHangFullyBlocking"]
        for mechanism in others {
            let grouping = SentryService.hangGrouping(
                mechanismType: mechanism,
                imagesAndSymbols: frames,
                existingTags: ["macos_version": "15.6.0"]
            )
            #expect(grouping == nil,
                    "mechanism \(mechanism ?? "nil") must not be fingerprinted as an app hang")
        }
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

        let grouping = SentryService.hangGrouping(
            mechanismType: "AppHang",
            imagesAndSymbols: frames,
            existingTags: nil
        )

        #expect(grouping?.fingerprint == ["{{ default }}", "app_hang", "SkyLight", "SLSDisplayGetTiming"])
        #expect(grouping?.tags["hang_image"] == "SkyLight")
        #expect(grouping?.tags["hang_symbol"] == "SLSDisplayGetTiming")
    }

    /// Two blocking sites under one in-app stack get two different fingerprints.
    ///
    /// The issue's `## Done when`, at the level this target can execute: these
    /// two vectors share every frame Sentry's own grouping looks at, which is
    /// precisely why all 191 events merged. The two exact arrays ARE the
    /// inequality — `Array ==` is element-wise — so a `!=` assertion beside them
    /// could not fail for any input, and is deliberately not written here.
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

        let a = SentryService.hangGrouping(
            mechanismType: "AppHang", imagesAndSymbols: swiftUI, existingTags: nil
        )
        let b = SentryService.hangGrouping(
            mechanismType: "AppHang", imagesAndSymbols: skyLight, existingTags: nil
        )

        #expect(a?.fingerprint == ["{{ default }}", "app_hang", "SwiftUI", "Set.contains"])
        #expect(b?.fingerprint == ["{{ default }}", "app_hang", "SkyLight", "SLSGetRealtimeDisplayInfoShmem"])
    }

    /// THE SHAPE THAT ACTUALLY SHIPS: two hangs, two images, one symbol.
    ///
    /// Every case above uses symbolicated names no released build emits. On
    /// device the ANR stack is captured with
    /// `stacktraceBuilder.symbolicate = options.debug`, `initialize()` never
    /// sets `debug`, and the SDK default is `NO` — so `SentryFrame`'s own init
    /// value survives and every frame's `function` is the literal
    /// `"<redacted>"`. The symbol is therefore a CONSTANT across all 191 events
    /// and the split that reaches Sentry is by IMAGE alone. That is the win this
    /// PR claims — 1 bucket becomes roughly 4 — and until now nothing pinned it,
    /// so nothing would have noticed it collapsing back to one.
    @Test func twoProductionShapeHangsStillSplitByImage() {
        let swiftUI: [(image: String, symbol: String)] = [
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "<redacted>"),
            ("/System/Library/Frameworks/SwiftUI.framework/Versions/A/SwiftUI", "<redacted>"),
            ("/usr/lib/system/libsystem_kernel.dylib", "<redacted>")
        ]
        let skyLight: [(image: String, symbol: String)] = [
            ("/Applications/HyperWhisper.app/Contents/MacOS/HyperWhisper", "<redacted>"),
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", "<redacted>"),
            ("/usr/lib/system/libsystem_kernel.dylib", "<redacted>")
        ]

        let a = SentryService.hangGrouping(
            mechanismType: "AppHang", imagesAndSymbols: swiftUI, existingTags: nil
        )
        let b = SentryService.hangGrouping(
            mechanismType: "AppHang", imagesAndSymbols: skyLight, existingTags: nil
        )

        #expect(a?.fingerprint == ["{{ default }}", "app_hang", "SwiftUI", "unknown"])
        #expect(b?.fingerprint == ["{{ default }}", "app_hang", "SkyLight", "unknown"])
        #expect(a?.tags["hang_image"] == "SwiftUI")
        #expect(b?.tags["hang_image"] == "SkyLight")
    }

    /// A symbol that names nothing becomes `"unknown"`, in the fingerprint AND
    /// in the tag — and the array is still the FOUR-element found-site shape.
    ///
    /// Two spellings of "no symbol", and the second is the one that ships.
    /// `""` is `beforeSend`'s own `$0.function ?? ""`, for a frame whose
    /// `function` really is nil. `"<redacted>"` is `SentryFrame.m:12`, which
    /// assigns it in `init`; `SentryCrashStackEntryMapper.m:36` overwrites it
    /// only when the unwinder produced a `symbolName`, and with symbolication
    /// off it never does. An `isEmpty`-only check therefore passes a whole test
    /// suite and fires exactly never in the field — the tag would read
    /// `hang_symbol: <redacted>` on every real event. Drop either spelling from
    /// `unreadableHangSymbols` and this case fails.
    @Test func aSymbolThatNamesNothingBecomesUnknown() {
        let nilFunction: [(image: String, symbol: String)] = [
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", ""),
            ("libsystem_kernel.dylib", "")
        ]
        let redacted: [(image: String, symbol: String)] = [
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", "<redacted>"),
            ("libsystem_kernel.dylib", "<redacted>")
        ]

        for frames in [nilFunction, redacted] {
            let grouping = SentryService.hangGrouping(
                mechanismType: "AppHang", imagesAndSymbols: frames, existingTags: nil
            )
            #expect(grouping?.fingerprint == ["{{ default }}", "app_hang", "SkyLight", "unknown"])
            #expect(grouping?.tags["hang_symbol"] == "unknown")
            #expect(grouping?.tags["hang_image"] == "SkyLight")
        }
    }

    /// No surviving frame is the issue's step 6 literal: THREE elements, and
    /// both tags still set so the bucket is searchable.
    @Test func aKernelOnlyStackFallsBackToTheUnknownBucket() {
        let frames: [(image: String, symbol: String)] = [
            ("/usr/lib/system/libsystem_pthread.dylib", "_pthread_cond_wait"),
            ("/usr/lib/system/libsystem_kernel.dylib", "mach_msg2_trap")
        ]

        let grouping = SentryService.hangGrouping(
            mechanismType: "AppHang", imagesAndSymbols: frames, existingTags: nil
        )

        #expect(grouping?.fingerprint == ["{{ default }}", "app_hang", "unknown"])
        #expect(grouping?.tags["hang_image"] == "unknown")
        #expect(grouping?.tags["hang_symbol"] == "unknown")
    }

    /// THE DEVICE TAGS SURVIVE: the returned dictionary is a MERGE, not a
    /// replacement.
    ///
    /// `SentryClient.m:797` applies the scope to the event, and only then, at
    /// `:860`, calls `beforeSend`. So `event.tags` already holds the four tags
    /// `SentryService.initialize()` put on the scope. Returning a fresh two-key
    /// dictionary — the obvious simplification, and exactly what a reader who
    /// believed the old "tags is nil on an SDK-raised event" comment would have
    /// written — strips `macos_version`, `build_number`, `architecture` and
    /// `cpu_cores` off every hang event, which is the one event class where the
    /// OS build and the CPU are the first things a triager asks for.
    @Test func theDeviceTagsSurviveTheHangTags() {
        let deviceTags = [
            "macos_version": "Version 15.6 (Build 24G84)",
            "build_number": "2470",
            "architecture": "arm64",
            "cpu_cores": "10"
        ]
        let frames: [(image: String, symbol: String)] = [
            ("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", "<redacted>"),
            ("/usr/lib/system/libsystem_kernel.dylib", "<redacted>")
        ]

        let grouping = SentryService.hangGrouping(
            mechanismType: "AppHang",
            imagesAndSymbols: frames,
            existingTags: deviceTags
        )

        for (key, value) in deviceTags {
            #expect(grouping?.tags[key] == value, "\(key) must survive the hang tags")
        }
        #expect(grouping?.tags["hang_image"] == "SkyLight")
        #expect(grouping?.tags["hang_symbol"] == "unknown")
        #expect(grouping?.tags.count == deviceTags.count + 2)
    }

    // MARK: - The wiring that cannot be called

    /// `beforeSend` reads the right things off the event, writes the decision
    /// back — and writes it ONLY inside the guard.
    ///
    /// Source-scraping, which `ProductionSource`'s own header calls the LAST
    /// RESORT: it proves a symbol is *mentioned*, never that it is used
    /// correctly. Everything that can be lifted into a callable pure function
    /// now has been, the mechanism check included, which is why the cases above
    /// execute the whole decision. What is left is a closure this target cannot
    /// invoke — `beforeSend` lives in `initialize()` and needs the Sentry
    /// package this target does not link, a DSN and a live SDK — holding five
    /// reads and two writes.
    ///
    /// Read the SHAPE of this case, not only its strings. The writes are
    /// asserted against `hangGuardBlock`, which BALANCES BRACES to return the
    /// guard's own body, and the same strings are then asserted ABSENT from
    /// everything else in `beforeSend`. A flat `contains` over the whole body
    /// cannot tell a write inside the guard from a write hoisted out of it, and
    /// hoisting it is the mutation that overwrites every other fingerprint in
    /// the app.
    @Test func beforeSendWritesTheGroupingOnlyInsideTheHangGuard() throws {
        let body = try Self.beforeSendBody()
        let guarded = try Self.hangGuardBlock(in: body)
        let elsewhere = body.replacingOccurrences(of: guarded, with: "\n")

        // The reads. Each is a different way to ship a working-looking
        // fingerprint computed from the wrong thing.
        #expect(body.contains("mechanismType: event.exceptions?.first?.mechanism?.type"),
                "the mechanism must be READ OFF THE EVENT and handed to hangGrouping; a literal there fingerprints every event the SDK sends as an app hang")
        #expect(body.contains("event.exceptions?.first?.stacktrace?.frames ?? []"),
                "the frames must come from the EXCEPTION, which is the blocked main thread; event.threads?.last is an idle worker and fingerprints every hang on its wait site")
        #expect(body.contains("image: $0.package"),
                "the image is Frame.package; Frame.module is documented 'mostly unused' and is nil for every Cocoa frame")
        #expect(body.contains("symbol: $0.function"),
                "the symbol is Frame.function")
        #expect(body.contains("existingTags: event.tags"),
                "the tag merge base must be the event's OWN tags; the scope applied macos_version/build_number/architecture/cpu_cores before beforeSend ran, and a fresh dictionary drops all four")

        // The two writes, inside the guard.
        #expect(guarded.contains("event.fingerprint = grouping.fingerprint"),
                "the fingerprint must reach the event — without this line every hang re-merges")
        #expect(guarded.contains("event.tags = grouping.tags"),
                "the tags must reach the event")

        // And nowhere else in beforeSend.
        #expect(!elsewhere.contains("event.fingerprint"),
                "beforeSend must not touch event.fingerprint outside the hang guard; six other sites set a deliberate custom fingerprint and an unguarded write clobbers them all")
        #expect(!elsewhere.contains("event.tags ="),
                "beforeSend must not assign event.tags outside the hang guard")
    }
}

extension AppHangFingerprintTests {

    fileprivate static let servicePath =
        "app/macos/hyperwhisper/Utilities/SentryService.swift"

    fileprivate static let serviceFile = "SentryService.swift"

    /// The one statement in `beforeSend` that may write the hang grouping.
    /// Everything from its `{` to the matching `}` is guarded by it; anything
    /// outside that is not.
    fileprivate static let hangGuardOpening = "if let grouping = Self.hangGrouping("

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

    /// The body of the hang guard inside `body`: from the `{` that opens it to
    /// its MATCHING `}`, exclusive of both.
    ///
    /// Braces are balanced rather than matched on indentation or on a second
    /// text anchor, because the entire value of this slice is that it ends
    /// exactly where the guard ends. An indentation rule reads as "nesting
    /// proven" while silently swallowing the rest of the closure the moment
    /// anything is re-wrapped; this cannot.
    /// `ProductionSource.code(of:)` has already removed every comment line, so
    /// no `{` in prose reaches the balance, and the guard body holds no string
    /// literal containing a brace.
    fileprivate static func hangGuardBlock(in body: String) throws -> String {
        guard let anchor = body.range(of: hangGuardOpening) else {
            throw ProductionSource.Failure.anchorNotFound(anchor: hangGuardOpening, file: serviceFile)
        }
        var depth = 0
        var started = false
        var block = ""
        for character in body[anchor.lowerBound...] {
            if character == "{" {
                depth += 1
                if depth == 1 {
                    started = true
                    continue
                }
            }
            if character == "}" {
                depth -= 1
                if depth == 0 {
                    return block
                }
            }
            if started {
                block.append(character)
            }
        }
        throw ProductionSource.Failure.anchorNotFound(
            anchor: "the closing brace of '\(hangGuardOpening)'",
            file: serviceFile
        )
    }
}
