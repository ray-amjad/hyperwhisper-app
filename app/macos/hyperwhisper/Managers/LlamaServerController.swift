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
        var contextSize: Int = 4096
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
    private static let pidFileURL: URL = {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return appSupport.appendingPathComponent("hyperwhisper/.llama-server.pid")
    }()
    private var stdoutPipe: Pipe?
    private var stderrPipe: Pipe?
    private var monitorTask: Task<Void, Never>?
    private var readinessTask: Task<Bool, Never>?
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
    func markPending() {
        guard case .stopped = state else { return }
        state = .pending
    }

    init() {
        #if os(macOS)
        // ORPHAN CLEANUP ON LAUNCH — dispatched off-main because
        // Process.waitUntilExit() inside Phase 2 spins the main runloop, and
        // on macOS 26.3 that re-enters SwiftUI's AttributeGraph transaction
        // while we're still inside a StateObject initializer, tripping
        // AG::precondition_failure and aborting before the UI ever renders.
        let cleanupLogger = logger
        DispatchQueue.global(qos: .utility).async {
            Self.cleanupOrphanedProcesses(logger: cleanupLogger)
        }

        // CRITICAL: App termination handler must execute SYNCHRONOUSLY
        // Using Task { @MainActor in ... } would schedule async work that may never
        // execute before the app terminates, leaving llama-server orphaned.
        //
        // The fix: Use DispatchQueue.main.sync to block until stop() completes.
        // This ensures the process is terminated before the app exits.
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self else { return }
            // Execute synchronously - we're already on main thread via queue: .main
            // Must block until stop() completes to prevent orphaned processes
            self.stopSynchronously(reason: .applicationTerminating)
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
            switch runtimeError {
            case .executableNotFound:
                logger.error("❌ Unable to locate llama-server executable")
                state = .failed(runtimeError.localizedDescription)
                throw Error.executableNotFound
            case .runtimeMissingDependencies(let missing):
                let missingFiles = missing.joined(separator: ", ")
                logger.error("❌ Runtime missing dependencies: \(missingFiles, privacy: .public)")
                state = .failed("Runtime missing dependencies: \(missingFiles)")
                throw Error.launchFailed("Runtime missing dependencies: \(missingFiles)")
            case .copyFailed(let reason):
                logger.error("❌ Runtime copy failed: \(reason, privacy: .public)")
                state = .failed(reason)
                throw Error.launchFailed(reason)
            }
        }

        try launchProcess(executableURL: executableURL, modelURL: modelURL, configuration: configuration)

        let ready = try await waitForReadiness(host: configuration.host, port: configuration.port)
        guard ready else {
            state = .failed("Timed out waiting for runtime")
            stop(reason: .modeChanged)
            throw Error.healthCheckFailed
        }

        state = .ready(modelId: modelId)
        logger.info("✅ Runtime is ready on http://\(configuration.host):\(configuration.port)")

        // Register the local LLM for memory-pressure eviction. Tier `.llm`, so it
        // is reclaimed only under CRITICAL pressure (it is the largest and most
        // expensive to reload). Weak capture; eviction hops to the main actor.
        await ModelResidencyRegistry.shared.register(id: Self.residencyId, tier: .llm) { [weak self] in
            await MainActor.run { self?.stop(reason: .memoryPressure) }
        }
        AppLogger.memory.info("model.load.cold id=\(Self.residencyId, privacy: .public) footprintMB=\(MemoryFootprint.currentMB(), privacy: .public)")
        return modelId
    }

    func stop(reason: StopReason = .manual) {
        // Drop the residency registration regardless of which path stop takes
        // (fire-and-forget: stop() is synchronous, the registry is an actor).
        Task { await ModelResidencyRegistry.shared.deregister(id: Self.residencyId) }

        monitorTask?.cancel()
        readinessTask?.cancel()
        monitorTask = nil
        readinessTask = nil

        guard let process else {
            state = .stopped
            currentModelId = nil
            currentModelURL = nil
            return
        }

        stdoutPipe?.fileHandleForReading.closeFile()
        stderrPipe?.fileHandleForReading.closeFile()

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
        stdoutPipe = nil
        stderrPipe = nil
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
    private nonisolated static func readPIDFileContents(removeOnFailure: Bool = true) -> LlamaPIDFileContents? {
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
    /// - Returns: true if process was running and kill was attempted
    @discardableResult
    private nonisolated static func killProcessSynchronously(
        record: LlamaServerPIDRecord,
        timeout: TimeInterval = 3.0,
        pollInterval: useconds_t = 100_000,
        sendInitialSigterm: Bool = true
    ) -> Bool {
        #if os(macOS)
        let pid = record.pid
        guard kill(pid, 0) == 0 else { return false }
        guard LlamaProcessIdentity.isKnownHyperWhisperLlamaServerPath(record.executablePath) else { return false }
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

    /// Synchronous stop for use in willTerminateNotification handler.
    /// This method is nonisolated so it can be called from notification handlers,
    /// but it dispatches synchronously to the main thread to execute stop().
    ///
    /// CRITICAL: This must complete synchronously before returning to ensure
    /// the llama-server process is terminated before the app exits.
    ///
    /// Unlike the regular stop(), this method:
    /// 1. Captures the PID before stopping
    /// 2. Calls stop() to send SIGTERM and clean up state
    /// 3. Waits synchronously for the process to die (with SIGKILL fallback)
    nonisolated func stopSynchronously(reason: StopReason) {
        #if os(macOS)
        var pid: Int32 = 0
        var terminationRecord: LlamaServerPIDRecord?

        if Thread.isMainThread {
            MainActor.assumeIsolated {
                pid = self.process?.processIdentifier ?? 0
                if pid > 0 {
                    terminationRecord = LlamaProcessIdentity.recordForLiveProcess(pid: pid)
                }
                self.stop(reason: reason)
            }
        } else {
            DispatchQueue.main.sync {
                MainActor.assumeIsolated {
                    pid = self.process?.processIdentifier ?? 0
                    if pid > 0 {
                        terminationRecord = LlamaProcessIdentity.recordForLiveProcess(pid: pid)
                    }
                    self.stop(reason: reason)
                }
            }
        }

        // Wait synchronously for process to die (SIGTERM already sent by stop())
        guard pid > 0 else { return }
        guard let terminationRecord else { return }
        Self.killProcessSynchronously(record: terminationRecord, timeout: 3.0, sendInitialSigterm: false)
        #else
        if Thread.isMainThread {
            MainActor.assumeIsolated { self.stop(reason: reason) }
        } else {
            DispatchQueue.main.sync {
                MainActor.assumeIsolated { self.stop(reason: reason) }
            }
        }
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
        guard let contents = Self.readPIDFileContents() else { return }
        guard case .record(let record) = contents else {
            try? FileManager.default.removeItem(at: Self.pidFileURL)
            return
        }
        Self.killProcessSynchronously(record: record, timeout: 0.5, pollInterval: 0)
        try? FileManager.default.removeItem(at: Self.pidFileURL)
        #endif
    }

    private func launchProcess(executableURL: URL, modelURL: URL, configuration: Configuration) throws {
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
            Task { @MainActor in
                guard let self else { return }
                // Cancel the stdout/stderr stream task and wait for it before
                // dropping the pipes, so we don't race `bytes.lines` against pipe
                // teardown and lose llama-server's final messages.
                self.monitorTask?.cancel()
                _ = await self.monitorTask?.value
                self.monitorTask = nil

                // Drain any trailing bytes still buffered in stderr — these are
                // the last words llama-server wrote before exit, and the streaming
                // task may have missed them if the pipe closed mid-line.
                if let stderr = self.stderrPipe {
                    let trailing = stderr.fileHandleForReading.availableData
                    if !trailing.isEmpty, let text = String(data: trailing, encoding: .utf8) {
                        for line in text.split(whereSeparator: \.isNewline) where !line.isEmpty {
                            self.captureRuntimeLine("[stderr] \(line)")
                            self.logger.error("[llama] \(String(line), privacy: .public)")
                        }
                    }
                }

                let reasonLabel = proc.terminationReason == .uncaughtSignal ? "signal" : "exit"
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
                self.stdoutPipe = nil
                self.stderrPipe = nil
            }
        }

        do {
            try process.run()
        } catch {
            logger.error("❌ Failed to launch llama-server: \(error.localizedDescription, privacy: .public)")
            throw Error.launchFailed(error.localizedDescription)
        }

        self.process = process
        self.stdoutPipe = stdoutPipe
        self.stderrPipe = stderrPipe
        self.missingDependencyHinted = false

        // Save PID to file for orphan tracking
        // This allows cleanup of this process if the app crashes before normal shutdown
        savePIDFile()

        monitorTask = Task { [weak self] in
            guard let self else { return }
            async let stdoutStream = self.stream(pipe: stdoutPipe, level: .debug, source: "stdout")
            async let stderrStream = self.stream(pipe: stderrPipe, level: .error, source: "stderr")
            _ = await (stdoutStream, stderrStream)
        }
    }

    private func buildArguments(modelURL: URL, configuration: Configuration) -> [String] {
        var args: [String] = [
            "--model", modelURL.path,
            "--host", configuration.host,
            "--port", String(configuration.port),
            "--threads", String(configuration.threads),
            "--ctx-size", String(configuration.contextSize),
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

    private func waitForReadiness(host: String, port: Int) async throws -> Bool {
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

    private func stream(pipe: Pipe, level: OSLogType, source: String) async {
        do {
            for try await line in pipe.fileHandleForReading.bytes.lines {
                let message = String(line)
                captureRuntimeLine("[\(source)] \(message)")
                logger.log(level: level, "[llama] \(message, privacy: .public)")
                if level == .error,
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
        } catch {
            logger.error("Stream error: \(error.localizedDescription, privacy: .public)")
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
                try? FileManager.default.removeItem(at: pidFileURL)
                return
            }

            guard LlamaProcessIdentity.liveProcessMatches(record) else {
                logger.warning("⚠️ Stale PID file no longer matches HyperWhisper llama-server; removing without signaling")
                try? FileManager.default.removeItem(at: pidFileURL)
                return
            }

            guard LlamaProcessIdentity.isTrackedHyperWhisperLlamaServerPath(record.executablePath) else {
                logger.warning("⚠️ Stale PID file points outside HyperWhisper's tracked llama-server runtime; removing without signaling")
                try? FileManager.default.removeItem(at: pidFileURL)
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
            if FileManager.default.fileExists(atPath: pidFileURL.path) {
                logger.warning("⚠️ Invalid PID in stale PID file, removing")
            }
        }

        try? FileManager.default.removeItem(at: pidFileURL)
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
            let directory = Self.pidFileURL.deletingLastPathComponent()
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

            // Write verified process identity to file for reuse-safe cleanup.
            let data = try LlamaProcessIdentity.encodePIDRecord(record)
            try data.write(to: Self.pidFileURL, options: .atomic)
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
            if FileManager.default.fileExists(atPath: Self.pidFileURL.path) {
                try FileManager.default.removeItem(at: Self.pidFileURL)
                logger.debug("🗑️ Removed PID tracking file")
            }
        } catch {
            logger.warning("Failed to remove PID file: \(error.localizedDescription, privacy: .public)")
        }
        #endif
    }
}
