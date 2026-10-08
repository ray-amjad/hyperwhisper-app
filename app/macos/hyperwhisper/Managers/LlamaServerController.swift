//
//  LlamaServerController.swift
//  hyperwhisper
//
//  Coordinates a bundled llama.cpp HTTP server used for local LLM post-processing.
//  Automatically launches and tears down the server as modes change so users never
//  have to run shell scripts manually.
//

import Foundation
import os.log
#if os(macOS)
import AppKit
import Darwin
#endif

@MainActor
final class LlamaServerController: ObservableObject {

    enum HardwareTier: String {
        case low   // ≤ 16 GB physical RAM
        case mid   // 16 GB < x ≤ 32 GB
        case high  // > 32 GB
    }

    struct Configuration: Equatable {
        var host: String = "127.0.0.1"
        var port: Int = 37219
        var contextSize: Int = LlamaServerController.defaultContextSize()
        var threads: Int = max(1, ProcessInfo.processInfo.activeProcessorCount - 1)
        // TODO: revisit if llama.cpp auto-fit solver is fixed upstream — see github.com/ggml-org/llama.cpp issues for "auto-fit"
        var gpuLayers: Int? = 99
        var flashAttention: Bool = true
        var parallel: Int = 1
        var useMlock: Bool = LlamaServerController.defaultUseMlock()
        var quantizedKV: Bool = LlamaServerController.defaultQuantizedKV()
        var largeBatch: Bool = LlamaServerController.defaultLargeBatch()
        var chatTemplate: String = ""
        var executableOverride: URL?

        static var `default`: Configuration { Configuration() }
    }

    nonisolated static func hardwareTier() -> HardwareTier {
        let bytes = ProcessInfo.processInfo.physicalMemory
        let gb = Double(bytes) / 1_073_741_824.0
        if gb > 32 { return .high }
        if gb > 16 { return .mid }
        return .low
    }

    /// Reserve enough context for an 8,192-token rewrite without evicting the
    /// source prompt. Higher-memory Macs get additional room for long transcripts.
    nonisolated static func defaultContextSize() -> Int {
        hardwareTier() == .high ? 32_768 : 16_384
    }

    // `--mlock` is actively harmful on Apple Silicon: Metal already keeps active
    // tensors resident via residency sets, mmap maps the GGUF, and `--mlock` then
    // double-counts the pages as wired — fighting the wired collector and
    // triggering jetsam SIGKILLs under any concurrent memory pressure. Also trips
    // llama.cpp issue #18152 (`GGML_ASSERT(addr) failed` in mmap path) on builds
    // b7410-b7440. Jan defaults this off; we should too.
    // See tuning-notes/00-research-summary.md and the SIGKILL diagnosis fork.
    nonisolated fileprivate static func defaultUseMlock() -> Bool {
        return false
    }

    // Quantize the KV cache on memory-constrained Macs to halve KV footprint. Requires
    // --flash-attn on (which we always set). Must match K and V quant types — mixed
    // quantization on Metal pre-M5 can silently drop FA or crash.
    nonisolated fileprivate static func defaultQuantizedKV() -> Bool {
        hardwareTier() == .low
    }

    // Larger batch / ubatch improves prefill on Apple Silicon. Increases compute-buffer
    // allocation — keep off on 8/16 GB Macs to preserve memory for the model itself.
    nonisolated fileprivate static func defaultLargeBatch() -> Bool {
        hardwareTier() != .low
    }

    enum StopReason: String {
        case modeChanged
        case providerDisabled
        case modelMissing
        case applicationTerminating
        case manual
        case memoryPressure
    }

    enum State: Equatable {
        case stopped
        case pending
        case starting(modelId: String)
        case ready(modelId: String)
        case failed(String)
    }

    enum Error: Swift.Error, LocalizedError {
        case executableNotFound
        case modelNotFound
        case launchFailed(String)
        case healthCheckFailed
        case unsupportedArchitecture
        case needsNativeRelaunch

        var errorDescription: String? {
            switch self {
            case .executableNotFound:
                return "Local runtime executable could not be located."
            case .modelNotFound:
                return "No local models are available on disk."
            case .launchFailed(let reason):
                return "Failed to launch local runtime: \(reason)."
            case .healthCheckFailed:
                return "Local runtime did not become ready in time."
            case .unsupportedArchitecture:
                return "Local AI post-processing requires Apple Silicon. Cloud post-processing providers are available as an alternative."
            case .needsNativeRelaunch:
                return "transcription.guidance.needsNativeRelaunch".localized
            }
        }
    }

    /// Whether local post-processing is available on this machine.
    ///
    /// Backed by `SystemCapability` (runtime sysctl detection) rather than the
    /// compile-time `#if arch(arm64)` we used to rely on, so a Rosetta-translated
    /// process on Apple Silicon is still recognised as Apple-Silicon hardware
    /// (it gets a "relaunch natively" nudge instead of a false "unsupported").
    /// `true` for any Apple-Silicon Mac — native or translated. The actual
    /// runtime launch is gated more strictly on `SystemCapability.canRunLocalRuntime`.
    static var isAppleSilicon: Bool {
        SystemCapability.current.isAppleSiliconHardware
    }

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LlamaServer")
    private let runtimeManager = LlamaRuntimeManager()
    private var process: Process?

    // MARK: - PID File Tracking
    // PID file is used to track the llama-server process across app sessions.
    // This allows cleanup of orphaned processes that survive app crashes or force quits.
    // Location: ~/Library/Application Support/hyperwhisper/.llama-server.pid
    private nonisolated static let defaultPIDFileURL: URL = {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return appSupport.appendingPathComponent("hyperwhisper/.llama-server.pid")
    }()
    /// The PID file this controller writes on launch and removes on stop.
    /// Production uses `defaultPIDFileURL`; a test passes its own file so it
    /// never overwrites or deletes the host app's record.
    private nonisolated let pidFileURL: URL

    /// Every llama-server this app launched that Foundation has not yet
    /// reported as exited. Readable without the actor, so the quit handler
    /// can stop a runtime the PID file does not hold (#1537).
    private nonisolated static let trackedRuntimes = TrackedRuntimeRegistry()

    private var stdoutReader: RuntimeOutputReader?
    private var stderrReader: RuntimeOutputReader?
    private var readinessTask: Task<Bool, Never>?
    /// The ownership token. Bumped by every stop(), and so by every start
    /// (ensureRunning stops first, then reads it). A start owns the runtime
    /// only while the value it read is still current; output readers, the
    /// termination handler and the residency eviction compare against it too.
    private var launchGeneration: UInt64 = 0
    private var missingDependencyHinted = false
    private var recentRuntimeLines: [String] = []
    private var lastHealthStatusCode: Int?
    private var lastHealthResponseSnippet: String?

    private var currentConfiguration: Configuration = .default
    private var currentModelId: String?
    private var currentModelURL: URL?

    #if os(macOS)
    /// Stores the termination observer so it can be removed in deinit.
    /// Without this, the observer leaks and may fire with a nil self reference.
    private var terminationObserver: NSObjectProtocol?
    #endif

    /// Residency id for the local LLM server in `ModelResidencyRegistry`.
    static let residencyId = "llm.local"

    @Published private(set) var state: State = .stopped

    /// Signal that a local runtime will be needed (shows "Warming Up" in the status bar).
    /// Called before the async `ensureRunning` work begins.
    ///
    /// Only `.stopped` becomes `.pending`. A `.failed` state keeps its reason:
    /// the caller can still return without starting (no model manager yet),
    /// and then "Warming Up" would stay up forever with the reason lost. A
    /// retry from `.failed` still shows progress, because ensureRunning sets
    /// `.starting` before its first suspension point.
    func markPending() {
        guard case .stopped = state else { return }
        state = .pending
    }

    /// - Parameter pidFileURL: Where to track the launched process. `nil` (the
    ///   app) uses the shared Application Support file and sweeps orphans from
    ///   earlier sessions. Tests pass their own file, which also skips the
    ///   sweep, so they never signal or untrack the host app's runtime.
    init(pidFileURL: URL? = nil) {
        self.pidFileURL = pidFileURL ?? Self.defaultPIDFileURL
        #if os(macOS)
        // ORPHAN CLEANUP ON LAUNCH — dispatched off-main because
        // Process.waitUntilExit() inside Phase 2 spins the main runloop, and
        // on macOS 26.3 that re-enters SwiftUI's AttributeGraph transaction
        // while we're still inside a StateObject initializer, tripping
        // AG::precondition_failure and aborting before the UI ever renders.
        if pidFileURL == nil {
            let cleanupLogger = logger
            DispatchQueue.global(qos: .utility).async {
                Self.cleanupOrphanedProcesses(logger: cleanupLogger)
            }
        }

        // CRITICAL: App termination handler must execute SYNCHRONOUSLY
        // Using Task { @MainActor in ... } would schedule async work that may never
        // execute before the app terminates, leaving llama-server orphaned.
        //
        // It also must not touch the actor or ask Swift concurrency which
        // executor it is on (#1537): `MainActor.assumeIsolated` here crashed
        // with SIGSEGV on quit once the main executor had been left broken.
        // Quit reads the lock-protected launch record and the PID file instead.
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification,
            object: nil,
            queue: .main
        ) { _ in
            LlamaServerController.stopTrackedRuntimeForTermination()
        }
        #endif
    }

    deinit {
        #if os(macOS)
        // Remove the termination observer to prevent leaks and dangling references
        if let observer = terminationObserver {
            NotificationCenter.default.removeObserver(observer)
        }
        #endif

        // CRITICAL: Synchronous cleanup in deinit
        // Using Task { @MainActor in ... } here would schedule async work that may never
        // execute if the object is being deallocated during app termination.
        // Instead, we perform synchronous cleanup to ensure the process is killed.
        stopSynchronouslyFromDeinit()
    }

    /// Ensures the llama-server is running with the given model file.
    /// Use this overload when the caller has already resolved the model (e.g. Qwen models).
    func ensureRunning(modelId: String, modelURL: URL, configuration: Configuration = .default) async throws -> String {
        // Runtime gate (not compile-time): on Intel the runtime can never run; under
        // Rosetta on Apple Silicon the arm64 runtime can't load, so nudge a native
        // relaunch rather than failing opaquely. `#if arch(arm64)` is kept as a final
        // belt below — a non-arm64 build must never even attempt the launch.
        switch SystemCapability.current {
        case .supported:
            break
        case .needsNativeRelaunch:
            logger.warning("Local runtime needs a native relaunch — running under Rosetta")
            state = .failed("Relaunch natively")
            throw Error.needsNativeRelaunch
        case .unsupported:
            logger.warning("Local runtime requires Apple Silicon — skipping on Intel")
            state = .failed("Requires Apple Silicon")
            throw Error.unsupportedArchitecture
        }
        #if !arch(arm64)
        logger.warning("Local runtime build is not arm64 — skipping launch")
        state = .failed("Requires Apple Silicon")
        throw Error.unsupportedArchitecture
        #endif

        if currentModelURL == modelURL,
           currentConfiguration == configuration {
            switch state {
            case .starting(let activeId) where activeId == modelId:
                logger.info("⏳ Runtime already starting for \(modelId, privacy: .public), skipping duplicate launch")
                return modelId
            case .ready(let activeId) where activeId == modelId:
                if process?.isRunning == true {
                    return modelId
                }
            default:
                break
            }
        }

        stop(reason: .modeChanged)

        // OWNERSHIP. This start owns the runtime, its state and its residency
        // only while `generation` is current. Any stop() — a mode change, a
        // newer ensureRunning (which stops first), memory pressure — bumps
        // `launchGeneration`, and from then on that call owns them. The value
        // is read before the first suspension point and re-checked after every
        // await below, so a superseded start never launches, never writes
        // `.ready`/`.failed`, and never registers residency.
        let generation = launchGeneration

        currentConfiguration = configuration
        currentModelId = modelId
        currentModelURL = modelURL

        state = .starting(modelId: modelId)
        logger.info("🚀 Launching runtime for model \(modelId, privacy: .public)")
        logger.debug("Resolved runtime model path: \(modelURL.path, privacy: .public)")

        let executableURL: URL
        do {
            executableURL = try await runtimeManager.prepareExecutable(override: configuration.executableOverride)
        } catch let runtimeError as LlamaRuntimeManager.Error {
            let failure: String
            let error: Error
            switch runtimeError {
            case .executableNotFound:
                logger.error("❌ Unable to locate llama-server executable")
                failure = runtimeError.localizedDescription
                error = .executableNotFound
            case .runtimeMissingDependencies(let missing):
                let missingFiles = missing.joined(separator: ", ")
                logger.error("❌ Runtime missing dependencies: \(missingFiles, privacy: .public)")
                failure = "Runtime missing dependencies: \(missingFiles)"
                error = .launchFailed(failure)
            case .copyFailed(let reason):
                logger.error("❌ Runtime copy failed: \(reason, privacy: .public)")
                failure = reason
                error = .launchFailed(reason)
            }
            if generation == launchGeneration {
                failStart(failure)
            }
            throw error
        } catch {
            if generation == launchGeneration {
                failStart(error.localizedDescription)
            }
            throw error
        }

        guard generation == launchGeneration else {
            logger.info("Local runtime start for \(modelId, privacy: .public) was superseded before launch")
            throw Error.healthCheckFailed
        }

        // No suspension point between the check above and the launch, so the
        // process launched here is always current when it starts. If a stop()
        // supersedes this start later, that stop() signals this process (it is
        // `self.process` until then), so a superseded start leaves no orphan.
        let launched: Process
        do {
            launched = try launchProcess(
                executableURL: executableURL,
                modelURL: modelURL,
                configuration: configuration,
                generation: generation
            )
        } catch Error.launchFailed(let reason) {
            // `process.run()` threw. Leaving `.starting` would make every later
            // call for this model return at once as "already starting".
            failStart(reason)
            throw Error.launchFailed(reason)
        } catch {
            failStart(error.localizedDescription)
            throw error
        }

        let ready = await waitForReadiness(host: configuration.host, port: configuration.port)
        // Both branches: a stop() or a newer start that ran during the wait owns
        // the process and the state now. Stopping here would kill the newer
        // launch, and `.ready`/`.failed` would overwrite its state.
        guard generation == launchGeneration else {
            logger.info("Local runtime start for \(modelId, privacy: .public) was superseded while waiting for readiness")
            throw Error.healthCheckFailed
        }
        guard ready else {
            // Keep the real reason. The termination handler ("Exit code N") or
            // the stderr reader ("Missing runtime dependency") may have set it
            // already; if the process has exited but its handler has not run
            // yet, read the status here. Only a live runtime timed out.
            let failure: String
            if case .failed(let reason) = state {
                failure = reason
            } else if !launched.isRunning {
                failure = "Exit code \(launched.terminationStatus)"
            } else {
                failure = "Timed out waiting for runtime"
            }
            failStart(failure)
            throw Error.healthCheckFailed
        }

        state = .ready(modelId: modelId)
        logger.info("✅ Runtime is ready on http://\(configuration.host):\(configuration.port)")

        // Register the local LLM for memory-pressure eviction. Tier `.llm`, so it
        // is reclaimed only under CRITICAL pressure (it is the largest and most
        // expensive to reload). Weak capture; eviction hops to the main actor,
        // and stops the runtime only if this start still owns it, so a stale
        // registration can never stop a newer runtime.
        await ModelResidencyRegistry.shared.register(id: Self.residencyId, tier: .llm) { [weak self] in
            await MainActor.run { self?.evictForMemoryPressure(generation: generation) }
        }
        // A stop() during the registration await has already queued its own
        // deregistration, and this caller no longer has a runtime.
        guard generation == launchGeneration else {
            logger.info("Local runtime for \(modelId, privacy: .public) was stopped while registering residency")
            throw Error.healthCheckFailed
        }
        AppLogger.memory.info("model.load.cold id=\(Self.residencyId, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
        return modelId
    }

    /// Ends the current start as failed. stop() tears down whatever the start
    /// launched and resets the model and configuration (so the next call
    /// launches again rather than seeing "already starting"); the reason is
    /// written after it because stop() sets `.stopped`.
    private func failStart(_ reason: String) {
        stop(reason: .modeChanged)
        state = .failed(reason)
    }

    private func evictForMemoryPressure(generation: UInt64) {
        guard generation == launchGeneration else { return }
        stop(reason: .memoryPressure)
    }

    func stop(reason: StopReason = .manual) {
        // Drop the residency registration regardless of which path stop takes
        // (fire-and-forget: stop() is synchronous, the registry is an actor).
        Task { await ModelResidencyRegistry.shared.deregister(id: Self.residencyId) }

        // Ends the ownership of whichever start is in flight (see
        // ensureRunning). Output already queued from the old process is
        // logged but no longer touches this controller's state.
        launchGeneration &+= 1

        // Cancelling a reader is safe at any moment (#1536): it stops a
        // dispatch read source and never closes the pipe's descriptor, so no
        // read can land on a closed handle (see `RuntimeOutputReader`). It
        // also bounds the reader's life when the process never reaches EOF.
        stdoutReader?.cancel()
        stderrReader?.cancel()
        stdoutReader = nil
        stderrReader = nil
        readinessTask?.cancel()
        readinessTask = nil

        guard let process else {
            state = .stopped
            currentModelId = nil
            currentModelURL = nil
            return
        }

        if process.isRunning {
            logger.info("🛑 Stopping local runtime (reason: \(reason.rawValue))")
            #if os(macOS)
            if let terminationRecord = LlamaProcessIdentity.recordForLiveProcess(pid: process.processIdentifier),
               LlamaProcessIdentity.liveProcessMatches(terminationRecord) {
                kill(terminationRecord.pid, SIGTERM)
                scheduleTerminationEnforcement(for: process, record: terminationRecord, reason: reason)
            } else {
                logger.warning("Skipping SIGTERM for local runtime: process identity no longer matches tracked llama-server")
            }
            #else
            process.terminate()
            scheduleTerminationEnforcement(for: process, record: nil, reason: reason)
            #endif
        }

        // Remove PID file during normal shutdown
        // This prevents false positive orphan detection on next launch
        removePIDFile()

        self.process = nil
        currentModelId = nil
        currentModelURL = nil
        missingDependencyHinted = false
        recentRuntimeLines.removeAll()
        lastHealthStatusCode = nil
        lastHealthResponseSnippet = nil
        state = .stopped
    }

    // MARK: - Process Termination Helpers

    /// Reads the tracked process identity from disk. Legacy bare PID files are
    /// intentionally not trusted for signaling because the PID may have been reused.
    private nonisolated static func readPIDFileContents(
        at pidFileURL: URL = defaultPIDFileURL,
        removeOnFailure: Bool = true
    ) -> LlamaPIDFileContents? {
        #if os(macOS)
        guard FileManager.default.fileExists(atPath: pidFileURL.path) else {
            return nil
        }

        do {
            let data = try Data(contentsOf: pidFileURL)
            let contents = LlamaProcessIdentity.parsePIDFileData(data)
            if case .invalid = contents, removeOnFailure {
                try? FileManager.default.removeItem(at: pidFileURL)
            }
            return contents
        } catch {
            if removeOnFailure {
                try? FileManager.default.removeItem(at: pidFileURL)
            }
            return .invalid
        }
        #else
        return nil
        #endif
    }

    /// Synchronously kills a process with SIGTERM, waiting up to the timeout before SIGKILL.
    /// Uses usleep for blocking wait - safe for deinit and notification handlers.
    ///
    /// - Parameters:
    ///   - record: Stored process identity to terminate
    ///   - timeout: Maximum seconds to wait for graceful termination (default 3.0)
    ///   - pollInterval: Microseconds between liveness checks. Use 0 for single wait.
    ///   - sendInitialSigterm: If true, sends SIGTERM before waiting (default true)
    ///   - requireKnownPath: If true, only a llama-server at a known HyperWhisper
    ///     runtime path is signalled. The quit path passes false for a process
    ///     this app launched itself (an `executableOverride` runtime included).
    /// - Returns: true if process was running and kill was attempted
    @discardableResult
    private nonisolated static func killProcessSynchronously(
        record: LlamaServerPIDRecord,
        timeout: TimeInterval = 3.0,
        pollInterval: useconds_t = 100_000,
        sendInitialSigterm: Bool = true,
        requireKnownPath: Bool = true
    ) -> Bool {
        #if os(macOS)
        let pid = record.pid
        guard kill(pid, 0) == 0 else { return false }
        if requireKnownPath {
            guard LlamaProcessIdentity.isKnownHyperWhisperLlamaServerPath(record.executablePath) else { return false }
        }
        guard LlamaProcessIdentity.liveProcessMatches(record) else { return false }

        if sendInitialSigterm {
            kill(pid, SIGTERM)
        }

        // Wait for process to exit
        if pollInterval > 0 {
            let deadline = Date().addingTimeInterval(timeout)
            while kill(pid, 0) == 0,
                  LlamaProcessIdentity.liveProcessMatches(record),
                  Date() < deadline {
                usleep(pollInterval)
            }
        } else {
            usleep(useconds_t(timeout * 1_000_000))
        }

        // Force kill if still running
        if kill(pid, 0) == 0, LlamaProcessIdentity.liveProcessMatches(record) {
            kill(pid, SIGKILL)
            usleep(100_000)  // Brief wait for SIGKILL to take effect
        }
        return true
        #else
        return false
        #endif
    }

    // MARK: - Synchronous Stop Methods

    /// Synchronous stop for the willTerminateNotification handler.
    ///
    /// CRITICAL: This must complete synchronously before returning to ensure
    /// the llama-server process is terminated before the app exits.
    ///
    /// It never reads the actor's state and never asks the concurrency runtime
    /// which executor it is on (#1537). It stops two sets of processes:
    ///
    /// 1. Every runtime in `trackedRuntimes`: each launch adds its process and
    ///    Foundation's termination callback removes it, under a lock. This
    ///    covers a runtime the PID file does not hold (the save failed, another
    ///    install overwrote the shared file, an `executableOverride` path) and
    ///    a stopped runtime that has not exited yet. A PID is signalled only if
    ///    the live process still runs the executable this app launched.
    /// 2. The PID file record, gated on its full identity and a known
    ///    HyperWhisper runtime path, as before.
    ///
    /// All get SIGTERM first; then each is waited on (up to 3 s) and SIGKILLed.
    private nonisolated static func stopTrackedRuntimeForTermination() {
        #if os(macOS)
        var targets: [LlamaServerPIDRecord] = []
        for entry in trackedRuntimes.snapshot() {
            guard let live = LlamaProcessIdentity.liveProcessIdentity(pid: entry.pid),
                  live.executablePath == entry.executablePath else { continue }
            targets.append(live)
        }
        if case .record(let record)? = readPIDFileContents(),
           !targets.contains(where: { $0.pid == record.pid }),
           LlamaProcessIdentity.isKnownHyperWhisperLlamaServerPath(record.executablePath),
           LlamaProcessIdentity.liveProcessMatches(record) {
            targets.append(record)
        }
        for target in targets {
            kill(target.pid, SIGTERM)
        }
        for target in targets {
            killProcessSynchronously(
                record: target,
                timeout: 3.0,
                sendInitialSigterm: false,
                requireKnownPath: false
            )
        }
        try? FileManager.default.removeItem(at: defaultPIDFileURL)
        #endif
    }

    /// Synchronous stop specifically for deinit.
    /// In deinit, we cannot access actor-isolated properties normally,
    /// so we directly kill the process using the saved PID if available.
    ///
    /// This is a last-resort cleanup that bypasses the normal stop() flow
    /// to ensure the process is killed even during unexpected deallocation.
    private nonisolated func stopSynchronouslyFromDeinit() {
        #if os(macOS)
        guard let contents = Self.readPIDFileContents(at: pidFileURL) else { return }
        guard case .record(let record) = contents else {
            try? FileManager.default.removeItem(at: pidFileURL)
            return
        }
        Self.killProcessSynchronously(record: record, timeout: 0.5, pollInterval: 0)
        try? FileManager.default.removeItem(at: pidFileURL)
        #endif
    }

    /// Launches llama-server for the start that owns `generation` and returns
    /// the process. Synchronous: the caller checks ownership right before it.
    private func launchProcess(
        executableURL: URL,
        modelURL: URL,
        configuration: Configuration,
        generation: UInt64
    ) throws -> Process {
        let process = Process()
        let arguments = buildArguments(modelURL: modelURL, configuration: configuration)
        process.executableURL = executableURL
        process.arguments = arguments
        process.currentDirectoryURL = executableURL.deletingLastPathComponent()
        recentRuntimeLines.removeAll()
        lastHealthStatusCode = nil
        lastHealthResponseSnippet = nil

        // Runtime artifact validation is handled by runtimeManager.prepareExecutable()
        // which already ran before this point. No duplicate check needed here.
        let runtimeDirectory = executableURL.deletingLastPathComponent()

        var environment = ProcessInfo.processInfo.environment
        #if os(macOS)
        let runtimePath = runtimeDirectory.path
        environment["GGML_METAL_PATH_RESOURCES"] = runtimePath

        let existing = environment["DYLD_LIBRARY_PATH"].flatMap { $0.isEmpty ? nil : $0 }
        var searchPaths = [runtimePath]
        if let existing {
            searchPaths.append(existing)
        }
        environment["DYLD_LIBRARY_PATH"] = searchPaths.joined(separator: ":")
        #endif
        process.environment = environment

        logger.debug("Runtime executable: \(executableURL.path, privacy: .public)")
        logger.debug("Runtime working directory: \(process.currentDirectoryURL?.path ?? "<nil>", privacy: .public)")
        logger.debug("Runtime launch arguments: \(arguments.joined(separator: " "), privacy: .public)")

        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe

        process.terminationHandler = { [weak self] proc in
            // Foundation's queue, no actor hop: the quit path must stop
            // tracking this PID as soon as the process is gone.
            LlamaServerController.trackedRuntimes.remove(pid: proc.processIdentifier)
            Task { @MainActor in
                guard let self else { return }
                let reasonLabel = proc.terminationReason == .uncaughtSignal ? "signal" : "exit"

                // A process that stop() already let go of (a failed start, a
                // mode change, a relaunch) owns none of the current state. Its
                // exit is logged, but it must not cancel the NEW launch's
                // readers or overwrite its state.
                guard self.process === proc else {
                    self.logger.info("Earlier local runtime exited · status=\(proc.terminationStatus) · reason=\(reasonLabel, privacy: .public)")
                    return
                }

                // Give the readers a bounded moment to reach EOF, so
                // llama-server's last lines (and a missing-library hint) are
                // seen before the crash is recorded. Bounded, because a
                // grandchild that inherited a pipe keeps it open past this
                // exit, and then EOF never comes.
                let readers = [self.stdoutReader, self.stderrReader].compactMap { $0 }
                let deadline = Date().addingTimeInterval(1)
                while readers.contains(where: { !$0.isFinished }), Date() < deadline {
                    try? await Task.sleep(nanoseconds: 50_000_000)
                }
                // Let the line deliveries the readers queued run first.
                await Task.yield()
                // The awaits are suspension points: stop() or a new launch may
                // have run meanwhile, and then this exit is no longer current.
                guard self.process === proc else { return }
                readers.forEach { $0.cancel() }
                self.stdoutReader = nil
                self.stderrReader = nil

                if proc.terminationStatus == 0 {
                    self.logger.info("♻️ Local runtime exited cleanly · reason=\(reasonLabel, privacy: .public)")
                } else {
                    self.logger.error("💥 Local runtime crashed · status=\(proc.terminationStatus) · reason=\(reasonLabel, privacy: .public)")
                    if self.missingDependencyHinted {
                        let error = NSError(
                            domain: "com.hyperwhisper.app.runtime",
                            code: Int(proc.terminationStatus),
                            userInfo: [NSLocalizedDescriptionKey: "runtime.error.llama.missingLib".localized]
                        )
                        SentryService.capture(
                            error: error,
                            message: "Local runtime terminated due to missing libmtmd.dylib",
                            tags: ["component": "LlamaRuntime", "severity": "fatal"]
                        )
                    }
                    self.state = .failed("Exit code \(proc.terminationStatus)")
                }
                self.process = nil
            }
        }

        do {
            try process.run()
        } catch {
            logger.error("❌ Failed to launch llama-server: \(error.localizedDescription, privacy: .public)")
            throw Error.launchFailed(error.localizedDescription)
        }

        self.process = process
        self.missingDependencyHinted = false

        #if os(macOS)
        // Track the child for the quit path before anything else can fail.
        // If it already exited, its termination callback may have run before
        // this insert, so take it out again; otherwise the callback does.
        let pid = process.processIdentifier
        Self.trackedRuntimes.insert(
            pid: pid,
            executablePath: LlamaProcessIdentity.canonicalizedPath(executableURL.path)
        )
        if !process.isRunning {
            Self.trackedRuntimes.remove(pid: pid)
        }
        #endif

        // Save PID to file for orphan tracking
        // This allows cleanup of this process if the app crashes before normal shutdown
        savePIDFile()

        stdoutReader = RuntimeOutputReader(pipe: stdoutPipe, label: "stdout") { [weak self] lines in
            Task { @MainActor in
                self?.handleRuntimeOutput(lines, isStderr: false, generation: generation)
            }
        }
        stderrReader = RuntimeOutputReader(pipe: stderrPipe, label: "stderr") { [weak self] lines in
            Task { @MainActor in
                self?.handleRuntimeOutput(lines, isStderr: true, generation: generation)
            }
        }
        return process
    }

    private func buildArguments(modelURL: URL, configuration: Configuration) -> [String] {
        var args: [String] = [
            "--model", modelURL.path,
            "--host", configuration.host,
            "--port", String(configuration.port),
            "--threads", String(configuration.threads),
            "--ctx-size", String(configuration.contextSize),
            "--no-context-shift",
            "--no-webui"
        ]

        if let gpuLayers = configuration.gpuLayers {
            args.append(contentsOf: ["--gpu-layers", String(gpuLayers)])
        }

        if configuration.flashAttention {
            // Explicit "on" — auto can silently drop FA on pre-M5 Metal and trigger
            // a 65 GB alloc attempt under quantized-V configs.
            args.append(contentsOf: ["--flash-attn", "on"])
        }

        args.append(contentsOf: ["--parallel", String(configuration.parallel)])

        // Bump scheduler priority on the server process to reduce inter-token
        // jitter under load. `--prio` was finally wired into llama-server in
        // PR #20373 (Mar 2026); silently no-op on older builds. 2 = high.
        args.append(contentsOf: ["--prio", "2"])

        // Gemma 4 SWA model needs --swa-full for the prompt cache to retain
        // the 2,500-token static prefix across requests. Iter 7 tested this
        // alone and reverted; retest now with iter 10's stable system prompt
        // and iter 13's top_k=40 in place — composition may differ.
        args.append("--swa-full")

        // Disable mmap — load model directly into RAM. May give the OS
        // less to evict under memory pressure on Apple Silicon's unified
        // memory architecture. Default mmap can confuse the wired collector
        // when KV cache wants to grow.
        args.append("--no-mmap")

        if configuration.useMlock {
            args.append("--mlock")
        }

        if configuration.largeBatch {
            // Big logical + physical batch helps prefill throughput on Apple
            // Silicon. Iter 4 measured this worse (ubatch 512 better), but
            // that was *before* --prio 2 was added in iter 8. Iter 9 retest
            // with prio in place showed 2048/2048 strongest overall (E2B -7%,
            // E4B -6%, 12B flat vs ub=512). Matches Hannecke's recommendation
            // and Apple's gpt-oss guide.
            args.append(contentsOf: ["--batch-size", "2048", "--ubatch-size", "2048"])
        }

        if configuration.quantizedKV {
            args.append(contentsOf: ["--cache-type-k", "q8_0", "--cache-type-v", "q8_0"])
        }

        if !configuration.chatTemplate.isEmpty {
            args.append(contentsOf: ["--chat-template", configuration.chatTemplate])
        }

        let tier = Self.hardwareTier().rawValue
        let ngl = configuration.gpuLayers.map(String.init) ?? "auto"
        let kv = configuration.quantizedKV ? "q8_0" : "fp16"
        let batch = configuration.largeBatch ? "2048" : "default"
        logger.info("Launching llama-server: tier=\(tier, privacy: .public), flash-attn=\(configuration.flashAttention ? "on" : "off", privacy: .public), gpu-layers=\(ngl, privacy: .public), mlock=\(configuration.useMlock, privacy: .public), batch=\(batch, privacy: .public), kv=\(kv, privacy: .public), parallel=\(configuration.parallel, privacy: .public)")

        return args
    }

    private func waitForReadiness(host: String, port: Int) async -> Bool {
        readinessTask?.cancel()
        let url = URL(string: "http://\(host):\(port)/health")
        readinessTask = Task { @MainActor in
            let deadline = Date().addingTimeInterval(25)
            while !Task.isCancelled, Date() < deadline {
                if let process = self.process, !process.isRunning {
                    self.logger.error("Local runtime exited before readiness check succeeded")
                    self.logCapturedRuntimeDiagnostics(reason: "process exited before health check passed")
                    return false
                }
                do {
                    if let url {
                        var request = URLRequest(url: url)
                        request.httpMethod = "GET"
                        let (data, response) = try await URLSession.shared.data(for: request)
                        if let http = response as? HTTPURLResponse {
                            if http.statusCode != self.lastHealthStatusCode {
                                self.lastHealthStatusCode = http.statusCode
                                let snippet = String(data: data, encoding: .utf8)?
                                    .trimmingCharacters(in: .whitespacesAndNewlines)
                                if let snippet, !snippet.isEmpty {
                                    self.lastHealthResponseSnippet = String(snippet.prefix(240))
                                } else {
                                    self.lastHealthResponseSnippet = nil
                                }
                                self.logger.debug(
                                    "Runtime health status changed to \(http.statusCode, privacy: .public) for model \(self.currentModelId ?? "<unknown>", privacy: .public)"
                                )
                                if let snippet = self.lastHealthResponseSnippet {
                                    self.logger.debug("Runtime health response: \(snippet, privacy: .public)")
                                }
                            }
                            if http.statusCode == 200 {
                                return true
                            }
                        }
                    }
                } catch {
                    if self.process == nil || self.process?.isRunning == false {
                        self.logger.error("Local runtime became unavailable during readiness polling: \(error.localizedDescription, privacy: .public)")
                        self.logCapturedRuntimeDiagnostics(reason: "runtime unavailable during readiness polling")
                        return false
                    }
                    if self.lastHealthStatusCode != nil {
                        self.lastHealthStatusCode = nil
                        self.lastHealthResponseSnippet = nil
                        self.logger.debug("Runtime health probe fell back to connection errors while waiting for readiness")
                    }
                }
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
            self.logCapturedRuntimeDiagnostics(reason: "timed out waiting for health check")
            return false
        }
        return await readinessTask?.value ?? false
    }

    /// Handles lines a `RuntimeOutputReader` read from the launch that owned
    /// `generation`. A launch that `stop()` has since replaced still has its
    /// lines logged, but they no longer feed the diagnostics buffer or the
    /// runtime state.
    private func handleRuntimeOutput(_ lines: [String], isStderr: Bool, generation: UInt64) {
        let level: OSLogType = isStderr ? .error : .debug
        let source = isStderr ? "stderr" : "stdout"
        for message in lines {
            logger.log(level: level, "[llama] \(message, privacy: .public)")
            guard generation == launchGeneration else { continue }
            captureRuntimeLine("[\(source)] \(message)")
            if isStderr,
               message.contains("libmtmd.dylib") || message.contains("image not found") {
                if !missingDependencyHinted {
                    missingDependencyHinted = true
                    logger.fault("Detected missing runtime dependency libmtmd.dylib")
                    let error = NSError(
                        domain: "com.hyperwhisper.app.runtime",
                        code: -1,
                        userInfo: [NSLocalizedDescriptionKey: "runtime.error.llama.reportedMissingLib".localized]
                    )
                    SentryService.capture(
                        error: error,
                        message: "Local runtime reported missing libmtmd.dylib",
                        tags: ["component": "LlamaRuntime", "severity": "fatal"]
                    )
                    state = .failed("Missing runtime dependency")
                }
            }

            if message.localizedCaseInsensitiveContains("error loading model") ||
                message.localizedCaseInsensitiveContains("failed to load model") ||
                message.localizedCaseInsensitiveContains("unknown model architecture") {
                logger.error("Runtime model load diagnostic: \(message, privacy: .public)")
            }
        }
    }

    private func captureRuntimeLine(_ line: String) {
        recentRuntimeLines.append(line)
        if recentRuntimeLines.count > 40 {
            recentRuntimeLines.removeFirst(recentRuntimeLines.count - 40)
        }
    }

    private func logCapturedRuntimeDiagnostics(reason: String) {
        if let modelId = currentModelId {
            logger.error("Runtime diagnostics for \(modelId, privacy: .public): \(reason, privacy: .public)")
        } else {
            logger.error("Runtime diagnostics: \(reason, privacy: .public)")
        }

        if let status = lastHealthStatusCode {
            logger.error("Last health status: \(status, privacy: .public)")
        }
        if let snippet = lastHealthResponseSnippet {
            logger.error("Last health response snippet: \(snippet, privacy: .public)")
        }
        if !recentRuntimeLines.isEmpty {
            logger.error("Recent runtime output:\n\(self.recentRuntimeLines.joined(separator: "\n"), privacy: .public)")
        }
    }

    private func scheduleTerminationEnforcement(
        for process: Process,
        record terminationRecord: LlamaServerPIDRecord?,
        reason: StopReason
    ) {
        let pid = process.processIdentifier
        // Capture only the PID, not the `Process` object. Polling `process.isRunning`
        // would retain the Process (and its stdout/stderr Pipes/FileHandles) for up to
        // the full timeout, keeping those FDs alive across rapid mode switches even
        // after `stop()` has nil'd `self.process`. `kill(pid, 0)` checks liveness
        // without holding a reference; the destructive SIGKILL below is still gated on
        // `liveProcessMatches` so PID reuse cannot cause a wrong-process kill.
        Task.detached { [weak self] in
            let timeout: TimeInterval = 3
            let pollInterval: UInt64 = 100_000_000  // 100ms
            let deadline = Date().addingTimeInterval(timeout)

            while kill(pid, 0) == 0, Date() < deadline {
                try await Task.sleep(nanoseconds: pollInterval)
            }

            guard kill(pid, 0) == 0 else { return }

#if os(macOS)
            guard let terminationRecord,
                  LlamaProcessIdentity.liveProcessMatches(terminationRecord) else {
                await MainActor.run {
                    self?.logger.warning("Skipping delayed force kill for PID \(pid): process identity no longer matches tracked llama-server")
                }
                return
            }
            kill(pid, SIGKILL)
#endif

            await MainActor.run {
                self?.logger.warning("Force killed local runtime (pid: \(pid)) after failing to terminate")
            }
        }
    }

    // MARK: - Orphan Process Cleanup

    /// Cleans up orphaned llama-server processes from previous app sessions.
    /// Called on app launch to recover from crashes or force quits.
    ///
    /// Two-phase cleanup:
    /// 1. **Surgical (PID file)**: Kill the specific process identity we previously tracked
    /// 2. **Enumerated fallback**: Kill llama-server processes whose executable path is ours
    ///
    /// This ensures users don't accumulate orphaned processes consuming ~2-3GB each.
    private static func cleanupOrphanedProcesses(logger: Logger) {
        #if os(macOS)
        // Phase 1: Try to kill the specific process we previously tracked
        cleanupStalePIDFile(logger: logger)

        // Phase 2: Kill any llama-server processes running from our runtime directory
        // This catches orphans from older versions that didn't have PID tracking
        cleanupOrphanedLlamaServers(logger: logger)
        #endif
    }

    /// Phase 1: Surgical cleanup using PID file.
    /// Bare legacy PID files are removed but never used for signaling.
    private static func cleanupStalePIDFile(logger: Logger) {
        #if os(macOS)
        guard let contents = readPIDFileContents(removeOnFailure: false) else {
            return
        }

        switch contents {
        case .record(let record):
            guard kill(record.pid, 0) == 0 else {
                logger.debug("PID \(record.pid) from stale PID file is no longer running")
                try? FileManager.default.removeItem(at: defaultPIDFileURL)
                return
            }

            guard LlamaProcessIdentity.liveProcessMatches(record) else {
                logger.warning("⚠️ Stale PID file no longer matches HyperWhisper llama-server; removing without signaling")
                try? FileManager.default.removeItem(at: defaultPIDFileURL)
                return
            }

            guard LlamaProcessIdentity.isTrackedHyperWhisperLlamaServerPath(record.executablePath) else {
                logger.warning("⚠️ Stale PID file points outside HyperWhisper's tracked llama-server runtime; removing without signaling")
                try? FileManager.default.removeItem(at: defaultPIDFileURL)
                return
            }

            logger.info("🧹 Found orphaned llama-server (PID: \(record.pid)) from previous session, terminating...")
            kill(record.pid, SIGTERM)

            DispatchQueue.global().asyncAfter(deadline: .now() + 2) {
                guard LlamaProcessIdentity.liveProcessMatches(record) else {
                    logger.warning("Skipping orphan force kill for PID \(record.pid): process identity no longer matches tracked llama-server")
                    return
                }
                logger.warning("⚡️ Force killing orphaned llama-server (PID: \(record.pid))")
                kill(record.pid, SIGKILL)
            }

        case .legacyPID(let pid):
            logger.warning("⚠️ Removing legacy bare PID file for PID \(pid) without signaling")
        case .invalid:
            if FileManager.default.fileExists(atPath: defaultPIDFileURL.path) {
                logger.warning("⚠️ Invalid PID in stale PID file, removing")
            }
        }

        try? FileManager.default.removeItem(at: defaultPIDFileURL)
        #endif
    }

    /// Phase 2: Enumerated cleanup by executable path.
    /// Kills only llama-server processes running from HyperWhisper's runtime directory.
    /// This catches orphans from older app versions that didn't have PID tracking.
    private static func cleanupOrphanedLlamaServers(logger: Logger) {
        #if os(macOS)
        for pid in LlamaProcessIdentity.allRunningProcessIDs() where pid != getpid() {
            guard let identity = LlamaProcessIdentity.liveProcessIdentity(pid: pid),
                  LlamaProcessIdentity.isKnownHyperWhisperLlamaServerPath(identity.executablePath) else {
                continue
            }
            guard LlamaProcessIdentity.liveProcessMatches(identity) else {
                logger.warning("Skipping enumerated orphan SIGTERM for PID \(pid): process identity no longer matches")
                continue
            }

            logger.info("🧹 Found orphaned llama-server by executable path (PID: \(pid)), terminating...")
            kill(pid, SIGTERM)

            DispatchQueue.global().asyncAfter(deadline: .now() + 2) {
                guard LlamaProcessIdentity.liveProcessMatches(identity) else {
                    logger.warning("Skipping enumerated orphan force kill for PID \(pid): process identity no longer matches")
                    return
                }
                logger.warning("⚡️ Force killing enumerated orphaned llama-server (PID: \(pid))")
                kill(pid, SIGKILL)
            }
        }
        #endif
    }

    /// Saves the current process PID to the PID file.
    /// Called when llama-server is successfully launched.
    private func savePIDFile() {
        #if os(macOS)
        guard let process = process else { return }

        let pid = process.processIdentifier

        do {
            guard let record = LlamaProcessIdentity.recordForLiveProcess(pid: pid) else {
                logger.warning("Failed to save PID file: unable to read launched process identity for PID \(pid)")
                return
            }

            // Ensure directory exists
            let directory = pidFileURL.deletingLastPathComponent()
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

            // Write verified process identity to file for reuse-safe cleanup.
            let data = try LlamaProcessIdentity.encodePIDRecord(record)
            try data.write(to: pidFileURL, options: .atomic)
            logger.debug("📝 Saved PID \(pid) to tracking file")
        } catch {
            logger.warning("Failed to save PID file: \(error.localizedDescription, privacy: .public)")
        }
        #endif
    }

    /// Removes the PID file when the process is stopped.
    /// Called during normal shutdown to prevent false orphan detection.
    private func removePIDFile() {
        #if os(macOS)
        do {
            if FileManager.default.fileExists(atPath: pidFileURL.path) {
                try FileManager.default.removeItem(at: pidFileURL)
                logger.debug("🗑️ Removed PID tracking file")
            }
        } catch {
            logger.warning("Failed to remove PID file: \(error.localizedDescription, privacy: .public)")
        }
        #endif
    }
}

// MARK: - Runtime Output Reader

/// Reads one llama-server output pipe, line by line, until EOF or `cancel()`.
///
/// #1536: the earlier reader used `fileHandleForReading.bytes.lines`. Once
/// `stop()` had closed that handle, the read raised an Objective-C
/// NSFileHandleOperationException, which Swift cannot catch, and the main
/// queue never drained again. This reader never reads through FileHandle
/// (`bytes`, `availableData`, `readDataToEndOfFile`). A DispatchSourceRead
/// calls `read(2)` on the pipe's descriptor, which can only return an error.
///
/// The descriptor is never closed under a read. Nothing calls `closeFile()`:
/// the descriptor closes only when the Pipe's read FileHandle is deallocated.
/// The event handler holds the Pipe, and dispatch releases that handler only
/// after the source is cancelled and any read in flight has returned.
///
/// The reader's life is bounded. EOF ends it when every writer has gone, and
/// `cancel()` ends it when one never goes (a SIGTERM that `stop()` skipped, a
/// grandchild that inherited the pipe). It holds the controller only through
/// the `onLines` closure, which captures it weakly.
private final class RuntimeOutputReader: @unchecked Sendable {
    /// Lines longer than this are delivered in pieces.
    private static let maximumLineBytes = 65_536

    private let lock = NSLock()
    /// Guarded by `lock`.
    private var source: DispatchSourceRead?
    /// Guarded by `lock`.
    private var finished = false
    /// Bytes after the last newline. Touched only on the source's queue.
    private var partialLine: [UInt8] = []
    private let onLines: @Sendable ([String]) -> Void

    init(pipe: Pipe, label: String, onLines: @escaping @Sendable ([String]) -> Void) {
        self.onLines = onLines
        let queue = DispatchQueue(label: "com.hyperwhisper.llama-server.\(label)")
        let descriptor = pipe.fileHandleForReading.fileDescriptor
        let source = DispatchSource.makeReadSource(fileDescriptor: descriptor, queue: queue)
        source.setEventHandler { [weak self] in
            // Holding `pipe` keeps `descriptor` open for as long as this
            // handler can run.
            withExtendedLifetime(pipe) {
                self?.readAvailable(from: descriptor)
            }
        }
        self.source = source
        source.resume()
    }

    deinit {
        source?.cancel()
    }

    /// True once EOF was read or `cancel()` ran.
    var isFinished: Bool {
        lock.lock()
        defer { lock.unlock() }
        return finished
    }

    /// Stops reading. Safe from any thread, at any time, more than once.
    func cancel() {
        lock.lock()
        let source = self.source
        self.source = nil
        finished = true
        lock.unlock()
        source?.cancel()
    }

    private func readAvailable(from descriptor: Int32) {
        var buffer = [UInt8](repeating: 0, count: 16_384)
        let count = buffer.withUnsafeMutableBytes { raw in
            Darwin.read(descriptor, raw.baseAddress, raw.count)
        }
        if count > 0 {
            consume(buffer[0..<count])
        } else if count == 0 || (errno != EINTR && errno != EAGAIN) {
            // EOF, or a read error: either way nothing more will come.
            finish()
        }
    }

    private func consume(_ bytes: ArraySlice<UInt8>) {
        var lines: [String] = []
        for byte in bytes {
            if byte == UInt8(ascii: "\n") {
                lines.append(Self.decode(partialLine))
                partialLine.removeAll(keepingCapacity: true)
            } else {
                partialLine.append(byte)
                if partialLine.count >= Self.maximumLineBytes {
                    lines.append(Self.decode(partialLine))
                    partialLine.removeAll(keepingCapacity: true)
                }
            }
        }
        lines.removeAll { $0.isEmpty }
        if !lines.isEmpty {
            onLines(lines)
        }
    }

    private func finish() {
        if !partialLine.isEmpty {
            let tail = Self.decode(partialLine)
            partialLine.removeAll()
            if !tail.isEmpty {
                onLines([tail])
            }
        }
        cancel()
    }

    private static func decode(_ bytes: [UInt8]) -> String {
        var line = String(decoding: bytes, as: UTF8.self)
        if line.hasSuffix("\r") {
            line.removeLast()
        }
        return line
    }
}

// MARK: - Tracked Runtime Registry

/// The llama-server processes this app launched and Foundation has not yet
/// reported as exited, with the executable each was launched from.
///
/// Lock-protected and actor-free, so the quit handler can read it without the
/// main actor (#1537).
private final class TrackedRuntimeRegistry: @unchecked Sendable {
    struct Entry: Sendable {
        let pid: Int32
        /// Canonical path of the executable the app launched.
        let executablePath: String
    }

    private let lock = NSLock()
    /// Guarded by `lock`.
    private var entries: [Int32: Entry] = [:]

    func insert(pid: Int32, executablePath: String) {
        lock.lock()
        entries[pid] = Entry(pid: pid, executablePath: executablePath)
        lock.unlock()
    }

    func remove(pid: Int32) {
        lock.lock()
        entries[pid] = nil
        lock.unlock()
    }

    func snapshot() -> [Entry] {
        lock.lock()
        defer { lock.unlock() }
        return Array(entries.values)
    }
}
