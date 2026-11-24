//
//  NativeInitDiagnosticsTests.swift
//  hyperwhisperTests
//
//  Issue #2678: an uncaught C++ throw inside whisper.cpp's init ("failed to
//  initialize CPU backend", HYPERWHISPER-11R) ended the app, and the crash
//  event said nothing about which init died or what the machine looked like.
//  `SentryService.beginNativeInit` now puts that on the scope as tags before
//  the init, and the SDK records the stack at the throw.
//
//  The tag set is tested by calling it. The wiring (the init sits between
//  begin and end, the SDK option is on) is read from source: it calls the
//  whisper.cpp C API and the Sentry package, and this target links neither.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Native init diagnostics")
struct NativeInitDiagnosticsTests {

    private func snapshot(_ engine: SentryService.NativeEngine) -> SentryService.NativeInitSnapshot {
        SentryService.NativeInitSnapshot(
            engine: engine,
            model: "large-v3-turbo",
            useGPU: true,
            flashAttention: false,
            freeMemoryMB: 2048,
            footprintMB: 512,
            thermalState: "fair"
        )
    }

    /// The crash report carries the engine, the model, the GPU flags, the
    /// memory and the thermal state.
    @Test func tagsCarryEveryField() {
        let tags = snapshot(.whisper).tags
        #expect(tags == [
            "init_whisper_model": "large-v3-turbo",
            "init_whisper_gpu": "true",
            "init_whisper_flash_attn": "false",
            "init_whisper_free_mb": "2048",
            "init_whisper_footprint_mb": "512",
            "init_whisper_thermal": "fair"
        ])
    }

    /// A failed memory query still writes a well-formed tag.
    @Test func unknownMemoryIsSpelledOut() {
        let tags = SentryService.NativeInitSnapshot(
            engine: .whisperVAD, model: "silero-v5.1.2", useGPU: false, flashAttention: false,
            freeMemoryMB: nil, footprintMB: nil, thermalState: "nominal"
        ).tags
        #expect(tags["init_whisper_vad_free_mb"] == "unknown")
        #expect(tags["init_whisper_vad_footprint_mb"] == "unknown")
    }

    /// `endNativeInit` removes by `tagSuffixes`, so a key `tags` writes and
    /// the list does not name would outlive its init and blame a later crash
    /// on it.
    @Test func endRemovesEveryKeyBeginWrites() {
        for engine in SentryService.NativeEngine.allCases {
            let prefix = SentryService.nativeInitTagPrefix(engine)
            let removed = Set(SentryService.NativeInitSnapshot.tagSuffixes.map { prefix + $0 })
            #expect(Set(snapshot(engine).tags.keys) == removed, "\(engine.rawValue)")
        }
    }

    /// Sentry drops a tag whose key is longer than 32 characters.
    @Test func everyTagKeyFitsSentrysLimit() {
        for engine in SentryService.NativeEngine.allCases {
            for key in snapshot(engine).tags.keys {
                #expect(key.count <= 32, "\(key) is \(key.count) characters")
            }
        }
        #expect(SentryService.nativeInitInFlightTag.count <= 32)
    }

    /// Two engines' tags never share a key, so one ending cannot clear the other.
    @Test func enginesUseDisjointKeys() {
        let whisper = Set(snapshot(.whisper).tags.keys)
        let vad = Set(snapshot(.whisperVAD).tags.keys)
        #expect(whisper.isDisjoint(with: vad))
    }

    /// `native_init` lists what is in flight in a fixed order, and is absent
    /// when nothing is.
    @Test func inFlightValueIsOrderedAndNilWhenIdle() {
        #expect(SentryService.nativeInitInFlightValue([]) == nil)
        #expect(SentryService.nativeInitInFlightValue([.whisper]) == "whisper")
        #expect(SentryService.nativeInitInFlightValue([.whisperVAD, .whisper]) == "whisper,whisper_vad")
    }

    @Test func thermalStatesAreFixedSlugs() {
        #expect(SentryService.thermalStateSlug(.nominal) == "nominal")
        #expect(SentryService.thermalStateSlug(.fair) == "fair")
        #expect(SentryService.thermalStateSlug(.serious) == "serious")
        #expect(SentryService.thermalStateSlug(.critical) == "critical")
    }

    /// The kernel query answers on a real Mac, with a plausible number.
    @Test func freeMemoryReadsOnThisMachine() throws {
        let free = try #require(SentryService.freePhysicalMemoryMB())
        let totalMB = ProcessInfo.processInfo.physicalMemory / (1024 * 1024)
        #expect(free <= totalMB)
    }

    /// A snapshot read now holds the model id it was given, and no path.
    @Test func liveSnapshotHoldsNoPath() {
        let live = SentryService.nativeInitSnapshot(
            engine: .whisper, model: "base.en", useGPU: true, flashAttention: true
        )
        #expect(live.tags["init_whisper_model"] == "base.en")
        for value in live.tags.values {
            #expect(!value.contains("/"), "\(value)")
        }
    }

    /// With the SDK stopped, begin and end only log: nothing to crash on.
    @Test func beginAndEndAreSafeWithTheSDKStopped() {
        SentryService.beginNativeInit(snapshot(.whisper))
        SentryService.endNativeInit(.whisper, succeeded: true)
    }

    // MARK: - Wiring, read from source

    /// The model load is bracketed: begin before the C call, end after it,
    /// with the model's catalog id and the params the call really uses.
    @Test func modelLoadIsBracketed() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Whisper/LibWhisper.swift",
            from: "private func initializeModel(path: String, model: String) throws {",
            to: "func setLanguage("
        )
        let begin = try #require(body.range(of: "SentryService.beginNativeInit("))
        let call = try #require(body.range(of: "whisper_init_from_file_with_params(path, params)"))
        let end = try #require(body.range(of: "SentryService.endNativeInit(.whisper, succeeded: context != nil)"))
        #expect(begin.upperBound <= call.lowerBound)
        #expect(call.upperBound <= end.lowerBound)
        let snapshot = String(body[begin.lowerBound..<call.lowerBound])
        #expect(snapshot.contains("engine: .whisper"))
        #expect(snapshot.contains("model: model"))
        #expect(snapshot.contains("useGPU: params.use_gpu"))
        #expect(snapshot.contains("flashAttention: params.flash_attn"))
    }

    /// The Silero VAD load reaches the same `whisper_backend_init` throw, so
    /// it is bracketed the same way.
    @Test func vadLoadIsBracketed() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/Processing/VoiceActivityDetector.swift",
            from: "func loadModel() async throws {",
            to: "var isReady: Bool {"
        )
        let begin = try #require(body.range(of: "SentryService.beginNativeInit("))
        let call = try #require(body.range(of: "whisper_vad_init_from_file_with_params(modelPath, contextParams)"))
        let end = try #require(body.range(of: "SentryService.endNativeInit(.whisperVAD, succeeded: ctx != nil)"))
        #expect(begin.upperBound <= call.lowerBound)
        #expect(call.upperBound <= end.lowerBound)
        #expect(String(body[begin.lowerBound..<call.lowerBound]).contains("engine: .whisperVAD"))
    }

    /// The provider passes the catalog name, never the path, as the model.
    @Test func providerPassesTheCatalogName() throws {
        let code = try ProductionSource.code(
            of: "app/macos/hyperwhisper/Managers/Transcription/Providers/Local/LibWhisperProvider.swift"
        )
        #expect(code.contains("WhisperContext.createContext(path: modelPath, model: model.name)"))
    }

    /// The SDK records the stack at `__cxa_throw`. Without it the crash event
    /// has no frames, which is how HYPERWHISPER-11R arrived.
    @Test func sdkHooksCxaThrow() throws {
        let start = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Utilities/SentryService+Lifecycle.swift",
            from: "SentrySDK.start { options in",
            to: "SentrySDK.configureScope { scope in"
        )
        #expect(start.contains("options.experimental.enableUnhandledCPPExceptionsV2 = true"))
    }
}
