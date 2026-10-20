//
//  SentryService+HangGrouping.swift
//  hyperwhisper
//
//  App hang fingerprinting (issue #683).
//  Part of `SentryService`; see SentryService.swift.
//

import Foundation

extension SentryService {

    // MARK: - App hang grouping

    /// The images at the bottom of a blocked main thread: the trap itself, not
    /// the cause. Every app hang ends in one of these, so grouping on them
    /// merges every hang into one Issue — which is the bug this splits.
    private static let hangTrapImages: Set<String> = [
        "libsystem_kernel.dylib",
        "libsystem_pthread.dylib",
        "libsystem_platform.dylib"
    ]

    /// The exception mechanism the SDK stamps on an app-hang event, and the
    /// only mechanism this grouping applies to.
    private static let appHangMechanism = "AppHang"

    /// Symbol strings that name nothing, and must not reach a tag or a
    /// fingerprint element.
    ///
    /// `""` is this file's own `$0.function ?? ""`. `"<redacted>"` is the SDK's:
    /// `SentryFrame.m` assigns `self.function = @"<redacted>"` in `init`, and
    /// `SentryCrashStackEntryMapper.m` overwrites it only when the unwinder
    /// produced a `symbolName`. Symbolication is off on device — the ANR stack
    /// is built with `stacktraceBuilder.symbolicate = options.debug` and
    /// `initialize()` never sets `debug` — so the literal string `"<redacted>"`,
    /// neither nil nor empty, is what a production hang frame actually carries.
    /// An empty check alone therefore never fires in the field.
    private static let unreadableHangSymbols: Set<String> = ["", "<redacted>"]

    /// The blocking site of an app hang, as the (image, symbol) pair to group on.
    ///
    /// `imagesAndSymbols` is the main thread's frames in Sentry's own order:
    /// caller to callee, oldest first, so the frame the thread is *stopped in*
    /// is LAST. See `SentryStacktraceBuilder.buildStacktraceFromFrames`, which
    /// reverses the unwinder's output for exactly this reason.
    ///
    /// `image` may be a full path (`/usr/.../SkyLight`) or a bare name; the
    /// basename is what comes back either way, and the basename is what the
    /// trap list is matched against.
    ///
    /// Plain Strings on both sides on purpose: `hyperwhisperTests` does not link
    /// the Sentry package, so a test cannot name `Frame` at all — the same split
    /// `store(for:)` in SentryService+Diagnostics.swift uses. Nil means every frame was a trap frame; the
    /// caller owns what to fingerprint then.
    static func hangFingerprintComponents(
        imagesAndSymbols: [(image: String, symbol: String)]
    ) -> (image: String, symbol: String)? {
        for frame in imagesAndSymbols.reversed() {
            let basename = frame.image.split(separator: "/").last.map(String.init) ?? frame.image
            if basename.isEmpty || hangTrapImages.contains(basename) { continue }
            return (image: basename, symbol: frame.symbol)
        }
        return nil
    }

    /// The whole `beforeSend` decision for one event: the `fingerprint` to put
    /// on it and the complete `tags` dictionary to put on it — or nil, meaning
    /// write nothing and leave the event exactly as it arrived.
    ///
    /// This exists so the decision is reachable from `hyperwhisperTests`.
    /// `beforeSend` (`sanitize(_:)` in SentryService+Privacy.swift) takes the
    /// Sentry package's `Event`, which that target does not link, so a test can
    /// never run it; with the array literals written inline there, deleting the found-site assignment
    /// left every test green while every hang re-merged into one Issue. The
    /// values live here, the wiring stays there, and both are pinned.
    ///
    /// **`mechanismType` is a PARAMETER, not an `if` around the call.** That is
    /// the load-bearing part of the shape. `beforeSend` sees every event the SDK
    /// sends, and six other sites set a deliberate custom fingerprint —
    /// `capture(error:)` in SentryService+Diagnostics.swift, `AppLogger.swift`, `LocalModelManager.swift`,
    /// `HyperWhisperCloudProvider.swift` and `StreamingTranscriptionClient.swift`
    /// (twice). A hang fingerprint that escaped the mechanism check would
    /// overwrite all of them on every event. With the check inside this
    /// function, "a non-hang event is left alone" is a case a unit test runs,
    /// rather than a shape a reader has to eyeball in a closure nothing calls.
    ///
    /// **`existingTags` is `event.tags`, and the result MERGES into it.** Not
    /// because `event.tags` is nil — it usually is not. `SentryClient.m:797`
    /// applies the scope to the event, and only then, at `:860`, calls
    /// `beforeSend`. So by the time this runs `event.tags` already carries the
    /// four device tags `initialize()` set on the scope: `macos_version`,
    /// `build_number`, `architecture`, `cpu_cores`. Building a fresh two-key
    /// dictionary here — the obvious "simplification" — would silently drop all
    /// four from every hang event, which is the one class of event where the OS
    /// version and the CPU matter most. The `?? [:]` is only for the case where
    /// no tag has been set at all.
    ///
    /// The two fingerprint shapes are NOT a tidy-up candidate. Issue #683 step 4
    /// specifies four elements for a found site and step 6 specifies three,
    /// verbatim, for the fallback; a four-element fallback hashes to a different
    /// Sentry group than the issue asked for. Both tags are set either way,
    /// because step 5 wants `hang_image:unknown` searchable before the regroup
    /// lands.
    static func hangGrouping(
        mechanismType: String?,
        imagesAndSymbols: [(image: String, symbol: String)],
        existingTags: [String: String]?
    ) -> (fingerprint: [String], tags: [String: String])? {
        guard mechanismType == appHangMechanism else { return nil }

        let fingerprint: [String]
        let image: String
        let symbol: String
        if let site = hangFingerprintComponents(imagesAndSymbols: imagesAndSymbols) {
            image = site.image
            // Symbolication is off on device, so the symbol arrives as the SDK's
            // own `"<redacted>"` placeholder — see `unreadableHangSymbols`.
            symbol = unreadableHangSymbols.contains(site.symbol) ? "unknown" : site.symbol
            fingerprint = ["{{ default }}", "app_hang", image, symbol]
        } else {
            // Issue step 6, verbatim: THREE elements, not four.
            image = "unknown"
            symbol = "unknown"
            fingerprint = ["{{ default }}", "app_hang", "unknown"]
        }

        var tags = existingTags ?? [:]
        tags["hang_image"] = image
        tags["hang_symbol"] = symbol
        return (fingerprint: fingerprint, tags: tags)
    }
}
