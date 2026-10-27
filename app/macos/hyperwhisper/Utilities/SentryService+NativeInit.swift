//
//  SentryService+NativeInit.swift
//  hyperwhisper
//
//  What a native whisper.cpp init was doing when it threw (issue #2678).
//  Part of `SentryService`; see SentryService.swift.
//

import Foundation
import Darwin
import os
#if canImport(Sentry)
@_spi(Private) import Sentry
#endif

extension SentryService {

    // MARK: - Native engine init (issue #2678)

    /// A whisper.cpp init that runs inside this process.
    ///
    /// whisper.cpp 1.7.6 throws `std::runtime_error("failed to initialize CPU
    /// backend")` from `whisper_backend_init`, and both of these reach it. Swift
    /// cannot catch a C++ exception, so the throw ends the app (HYPERWHISPER-11R).
    /// llama.cpp is not here: it runs in its own `llama-server` process
    /// (LlamaServerController), so its throw cannot end this one.
    enum NativeEngine: String, CaseIterable, Sendable {
        /// `whisper_init_from_file_with_params`, LibWhisper.swift.
        case whisper
        /// `whisper_vad_init_from_file_with_params`, VoiceActivityDetector.swift.
        case whisperVAD = "whisper_vad"
    }

    /// The state of the machine just before a native init. Every field is an
    /// enum, a catalog id, a Bool or a count: no path, no transcript, no prompt.
    struct NativeInitSnapshot: Equatable, Sendable {
        let engine: NativeEngine
        /// A catalog id (`WhisperModelManager.allModels` name or the bundled
        /// VAD model), never a file path: a path carries the user's name.
        let model: String
        let useGPU: Bool
        let flashAttention: Bool
        /// Free plus inactive physical pages, in MB. nil when the kernel query fails.
        let freeMemoryMB: UInt64?
        /// This process's own phys_footprint, in MB. nil when the query fails.
        let footprintMB: UInt64?
        let thermalState: String

        /// Scope tags, one set per engine so two inits in flight at once do
        /// not clear each other's. A tag, not a breadcrumb: `sanitize` strips
        /// every breadcrumb from every event, and the crash reporter copies the
        /// scope's tags into the crash report it writes when the throw lands.
        /// Every key stays within Sentry's 32-character tag-key limit.
        var tags: [String: String] {
            let prefix = SentryService.nativeInitTagPrefix(engine)
            return [
                prefix + "model": model,
                prefix + "gpu": String(useGPU),
                prefix + "flash_attn": String(flashAttention),
                prefix + "free_mb": freeMemoryMB.map(String.init) ?? "unknown",
                prefix + "footprint_mb": footprintMB.map(String.init) ?? "unknown",
                prefix + "thermal": thermalState
            ]
        }

        /// The breadcrumb `data` and the log line read the same fields.
        var summary: String {
            let free = freeMemoryMB.map(String.init) ?? "unknown"
            let footprint = footprintMB.map(String.init) ?? "unknown"
            return "engine=\(engine.rawValue) model=\(model) use_gpu=\(useGPU) flash_attn=\(flashAttention) free_mb=\(free) footprint_mb=\(footprint) thermal=\(thermalState)"
        }
    }

    /// `init_<engine>_`, the prefix of every tag one engine's init sets.
    static func nativeInitTagPrefix(_ engine: NativeEngine) -> String {
        "init_\(engine.rawValue)_"
    }

    /// The tag that names every native init in flight, comma-joined in a fixed
    /// order, so `native_init:whisper` finds a crash inside the model load.
    static let nativeInitInFlightTag = "native_init"

    /// The `native_init` value for these engines, or nil when none is in flight.
    static func nativeInitInFlightValue(_ engines: Set<NativeEngine>) -> String? {
        let names = NativeEngine.allCases.filter(engines.contains).map(\.rawValue)
        return names.isEmpty ? nil : names.joined(separator: ",")
    }

    /// `ProcessInfo.ThermalState` as a fixed slug.
    static func thermalStateSlug(_ state: ProcessInfo.ThermalState) -> String {
        switch state {
        case .nominal: return "nominal"
        case .fair: return "fair"
        case .serious: return "serious"
        case .critical: return "critical"
        @unknown default: return "unknown"
        }
    }

    /// Free plus inactive physical pages, in MB, the pages macOS can hand out
    /// without swapping. nil when `host_statistics64` fails.
    static func freePhysicalMemoryMB() -> UInt64? {
        var stats = vm_statistics64_data_t()
        var count = mach_msg_type_number_t(
            MemoryLayout<vm_statistics64_data_t>.size / MemoryLayout<integer_t>.size
        )
        let result = withUnsafeMutablePointer(to: &stats) { ptr in
            ptr.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { intPtr in
                host_statistics64(nativeInitHostPort, HOST_VM_INFO64, intPtr, &count)
            }
        }
        guard result == KERN_SUCCESS else { return nil }
        let pages = UInt64(stats.free_count) + UInt64(stats.inactive_count)
        return pages * UInt64(vm_kernel_page_size) / (1024 * 1024)
    }

    /// Read once: every `mach_host_self()` call adds a send right to the port.
    private static let nativeInitHostPort = mach_host_self()

    /// Read the machine's state now, for an init about to start.
    static func nativeInitSnapshot(
        engine: NativeEngine,
        model: String,
        useGPU: Bool,
        flashAttention: Bool
    ) -> NativeInitSnapshot {
        NativeInitSnapshot(
            engine: engine,
            model: model,
            useGPU: useGPU,
            flashAttention: flashAttention,
            freeMemoryMB: freePhysicalMemoryMB(),
            footprintMB: MemoryFootprint.currentBytes().map { $0 / (1024 * 1024) },
            thermalState: thermalStateSlug(ProcessInfo.processInfo.thermalState)
        )
    }

    /// Engines between `beginNativeInit` and `endNativeInit`, for `native_init`.
    private final class NativeInitsInFlight: @unchecked Sendable {
        private let lock = NSLock()
        private var engines: Set<NativeEngine> = []

        /// Runs `body` on the set under the lock, so two engines that begin
        /// and end at once cannot write the `native_init` tag out of order.
        func withEngines(_ body: (inout Set<NativeEngine>) -> Void) {
            lock.lock()
            defer { lock.unlock() }
            body(&engines)
        }
    }

    private static let nativeInitsInFlight = NativeInitsInFlight()

    private static let nativeInitLogger = Logger(subsystem: "com.hyperwhisper.app", category: "NativeInit")

    /// Call right before a whisper.cpp init that can throw a C++ exception.
    /// Logs the snapshot, leaves a local breadcrumb and puts the snapshot on the
    /// scope as tags, so a crash inside the init carries it. Pair every call
    /// with `endNativeInit(_:)` once the init returns, whatever it returned.
    static func beginNativeInit(_ snapshot: NativeInitSnapshot) {
        nativeInitLogger.notice("native init begin \(snapshot.summary, privacy: .public)")
        addBreadcrumb(message: "native init begin", category: "native_init", data: snapshot.tags)
        nativeInitsInFlight.withEngines { engines in
            #if canImport(Sentry)
            // Gated like `setTag`. Not mirrored into `logScopeState`: these
            // are for the crash report, and the Logs path would redact them.
            // An engine whose tags were not written is not counted in flight,
            // so `native_init` never names an init the scope says nothing of.
            guard isReportingEnabled else { return }
            engines.insert(snapshot.engine)
            let inFlight = nativeInitInFlightValue(engines)
            let inFlightKey = nativeInitInFlightTag
            SentrySDK.configureScope { scope in
                for (key, value) in snapshot.tags {
                    scope.setTag(value: value, key: key)
                }
                if let inFlight {
                    scope.setTag(value: inFlight, key: inFlightKey)
                }
            }
            #endif
        }
    }

    /// Call once the init `beginNativeInit` announced has returned. Removes
    /// that engine's tags, so a later crash is not blamed on an init that
    /// finished.
    static func endNativeInit(_ engine: NativeEngine, succeeded: Bool) {
        nativeInitLogger.notice("native init end engine=\(engine.rawValue, privacy: .public) succeeded=\(succeeded, privacy: .public)")
        nativeInitsInFlight.withEngines { engines in
            engines.remove(engine)
            #if canImport(Sentry)
            // Not gated on the switch: a tag set while it was on must not
            // outlive its init.
            guard SentrySDK.isEnabled else { return }
            let prefix = nativeInitTagPrefix(engine)
            let inFlight = nativeInitInFlightValue(engines)
            let inFlightKey = nativeInitInFlightTag
            SentrySDK.configureScope { scope in
                for suffix in NativeInitSnapshot.tagSuffixes {
                    scope.removeTag(key: prefix + suffix)
                }
                if let inFlight {
                    scope.setTag(value: inFlight, key: inFlightKey)
                } else {
                    scope.removeTag(key: inFlightKey)
                }
            }
            #endif
        }
    }
}

extension SentryService.NativeInitSnapshot {
    /// Every suffix `tags` writes after `init_<engine>_`, for the removal in
    /// `endNativeInit`.
    static let tagSuffixes = ["model", "gpu", "flash_attn", "free_mb", "footprint_mb", "thermal"]
}
