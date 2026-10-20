//
//  DeviceAudioRecorder.swift
//  hyperwhisper
//
//  Records from ONE chosen CoreAudio input device, not the macOS default.
//
//  `AVAudioRecorder` can only record from the system-default input. Before the
//  microphone picker, choosing a microphone in HyperWhisper therefore CHANGED the
//  Mac's default input, which also moved Zoom, FaceTime and every other app. This
//  recorder opens the chosen device itself, through the `AVAudioEngine` input node's
//  AudioUnit (`kAudioOutputUnitProperty_CurrentDevice`), so the default is untouched.
//
//  It is used ONLY when the chosen device is connected and is not the current
//  default. Every other case — no choice, the choice unplugged, the choice already
//  the default — keeps the long-proven `AVAudioRecorder` path (see
//  `SimpleRecorder.makeLiveRecorder`). The engine path once caused static and
//  silence, so it is kept to the one case that needs it, and it reuses the
//  conversion `StreamingAudioCapture` has run in production since.
//

import Foundation
import AVFoundation
import AudioToolbox
import CoreAudio
import os

// MARK: - Live Recording Abstraction

/// The part of `AVAudioRecorder` that `SimpleRecorder` drives.
///
/// `SimpleRecorder` keeps one live recorder, meters it on its meter queue and stops it
/// there. Abstracting just these calls lets it drive a `DeviceAudioRecorder` with all
/// of the same start, supersede, meter and stop discipline, instead of a second copy.
protocol LiveAudioRecording: AnyObject {
    var isMeteringEnabled: Bool { get set }
    func record() -> Bool
    func stop()
    func updateMeters()
    func averagePower(forChannel channelNumber: Int) -> Float
    func deleteRecording() -> Bool
}

extension AVAudioRecorder: LiveAudioRecording {}

// MARK: - Errors

enum DeviceAudioRecorderError: Error, CustomStringConvertible {
    case noAudioUnit
    case deviceSelectionFailed(OSStatus)
    case noInputFormat
    case converterUnavailable

    var description: String {
        switch self {
        case .noAudioUnit:
            return "The input node has no AudioUnit"
        case .deviceSelectionFailed(let status):
            return "Selecting the input device failed (OSStatus \(status))"
        case .noInputFormat:
            return "The input device reports no usable format"
        case .converterUnavailable:
            return "Could not build a converter to 16 kHz mono Int16"
        }
    }
}

// MARK: - Device Recorder

/// Records one CoreAudio input device to a 16 kHz, mono, 16-bit little-endian
/// interleaved WAV — the same file `AVAudioRecorder` writes for `SimpleRecorder` — or,
/// with a `nil` URL, only meters it (the Settings / onboarding level preview).
///
/// **Threading.** Every method is blocking CoreAudio work, so callers run it off the
/// main thread, exactly as they do for `AVAudioRecorder`: `record()` on
/// `SimpleRecorder`'s start queue, `stop()` and the meter read on its meter queue.
/// Two locks make that safe:
/// - `controlLock` serializes `record()`, `stop()` and a configuration-change restart.
/// - `tapLock` guards what the tap thread touches: the file, the converter pipeline
///   and the last level. `stop()` clears the file under it, so once `stop()` returns
///   no tap can still be writing, the `AVAudioFile` has been released, and the WAV
///   header is final.
///
/// **Configuration changes** (`AVAudioEngineConfigurationChange`): a Bluetooth headset
/// switching profile, a sample-rate change, or the device going away stops the engine.
/// The recorder rebuilds the engine and keeps writing the SAME file: on the chosen
/// device if it still opens, else on the macOS default input (the spec: an unplugged
/// choice records from the default). The converter already handles any input format.
/// A restart that opens neither is retried shortly after, and only
/// `maxConsecutiveRestartFailures` failures IN A ROW give up; any successful restart
/// resets the count, so repeated benign changes (a Bluetooth profile flipping back and
/// forth) never end a long recording. Giving up stops cleanly (the file keeps what
/// was captured) and calls the capture-lost handler, so the owner ends the recording
/// through its error path instead of treating a truncated file as a success.
///
/// `@unchecked Sendable`: all mutable state is behind the locks above (and
/// `handlerLock` for the capture-lost handler).
final class DeviceAudioRecorder: NSObject, LiveAudioRecording, @unchecked Sendable {

    // MARK: Format

    /// The WAV `SimpleRecorder.recordSettings` asks `AVAudioRecorder` for.
    static let fileSettings: [String: Any] = [
        AVFormatIDKey: Int(kAudioFormatLinearPCM),
        AVSampleRateKey: 16000.0,
        AVNumberOfChannelsKey: 1,
        AVLinearPCMBitDepthKey: 16,
        AVLinearPCMIsFloatKey: false,
        AVLinearPCMIsBigEndianKey: false,
        AVLinearPCMIsNonInterleaved: false
    ]

    /// What the converter produces and the file is written from.
    static let outputFormat = AVAudioFormat(
        commonFormat: .pcmFormatInt16,
        sampleRate: 16000,
        channels: 1,
        interleaved: true
    )!

    /// The level reported while nothing has been heard, matching `AVAudioRecorder`.
    static let silentPowerDb: Float = -160

    /// Restart attempts that may fail IN A ROW (neither the chosen device nor the
    /// default opens) before the recorder gives up. A successful restart resets it.
    static let maxConsecutiveRestartFailures = 3

    /// Wait before retrying a restart that failed: a device mid-reconfiguration (a
    /// Bluetooth profile switch) briefly reports no format.
    static let restartRetryDelay: DispatchTimeInterval = .milliseconds(500)

    /// Open the WAV the recorder writes. Static so a test can check the file shape.
    static func makeOutputFile(url: URL) throws -> AVAudioFile {
        try AVAudioFile(
            forWriting: url,
            settings: fileSettings,
            commonFormat: .pcmFormatInt16,
            interleaved: true
        )
    }

    /// Point an `AVAudioEngine` input node at `deviceID`. Call before the node's
    /// format is read and before the engine starts.
    ///
    /// Shared with `StreamingAudioCapture`, so live streaming opens the same device
    /// the recording does.
    static func setInputDevice(_ deviceID: AudioDeviceID, on inputNode: AVAudioInputNode) throws {
        guard let unit = inputNode.audioUnit else {
            throw DeviceAudioRecorderError.noAudioUnit
        }
        var id = deviceID
        let status = AudioUnitSetProperty(
            unit,
            kAudioOutputUnitProperty_CurrentDevice,
            kAudioUnitScope_Global,
            0,
            &id,
            UInt32(MemoryLayout<AudioDeviceID>.size)
        )
        guard status == noErr else {
            throw DeviceAudioRecorderError.deviceSelectionFailed(status)
        }
    }

    /// The tap format for an input node, or `nil` when the device reports none (no
    /// permission, no device, a device mid-reconfiguration). Installing a tap with a
    /// zero-rate or zero-channel format raises an Objective-C exception, which Swift
    /// cannot catch, so this check is what keeps a bad device from crashing the app.
    static func tapFormat(for inputNode: AVAudioInputNode) -> AVAudioFormat? {
        let output = inputNode.outputFormat(forBus: 0)
        let hardware = inputNode.inputFormat(forBus: 0)
        guard output.sampleRate > 0, output.channelCount > 0 else { return nil }
        // A tap whose rate differs from the hardware's raises an exception too. After a
        // device switch the output side can briefly lag behind the hardware side.
        if hardware.sampleRate > 0, hardware.sampleRate != output.sampleRate {
            AppLogger.audio.notice("Input node output rate \(output.sampleRate, privacy: .public) Hz differs from the device's \(hardware.sampleRate, privacy: .public) Hz - tapping at the device rate")
            // `init(standardFormatWithSampleRate:channels:)` returns nil above two
            // channels, so a multi-channel device keeps its layout.
            if let layout = output.channelLayout {
                return AVAudioFormat(standardFormatWithSampleRate: hardware.sampleRate, channelLayout: layout)
            }
            return AVAudioFormat(standardFormatWithSampleRate: hardware.sampleRate, channels: output.channelCount)
        }
        return output
    }

    /// RMS power of a float buffer in dB, floored at `silentPowerDb`.
    static func powerDb(of buffer: AVAudioPCMBuffer) -> Float {
        guard let data = buffer.floatChannelData else { return silentPowerDb }
        let frames = Int(buffer.frameLength)
        let channels = Int(buffer.format.channelCount)
        guard frames > 0, channels > 0 else { return silentPowerDb }

        var sumSquares: Float = 0
        if buffer.format.isInterleaved {
            let samples = data[0]
            for i in 0..<(frames * channels) {
                sumSquares += samples[i] * samples[i]
            }
        } else {
            for channel in 0..<channels {
                let samples = data[channel]
                for i in 0..<frames {
                    sumSquares += samples[i] * samples[i]
                }
            }
        }
        let rms = (sumSquares / Float(frames * channels)).squareRoot()
        guard rms > 0, rms.isFinite else { return silentPowerDb }
        return max(silentPowerDb, 20 * log10(rms))
    }

    // MARK: Properties

    /// The chosen device. Restarts try it first, so capture returns to it when it can.
    let deviceID: AudioDeviceID

    /// Where the WAV goes, or `nil` to meter only.
    let url: URL?

    /// The macOS default input, the fallback when the chosen device stops opening.
    /// Injected so a test never has to read real hardware.
    private let defaultInputDeviceID: @Sendable () -> AudioDeviceID?

    private let controlLock = NSLock()
    private var engine: AVAudioEngine?               // controlLock
    private var configObserver: NSObjectProtocol?   // controlLock
    private var isRunning = false                    // controlLock
    private var restartBudget = CaptureRestartBudget(maxConsecutiveFailures: DeviceAudioRecorder.maxConsecutiveRestartFailures) // controlLock
    private var restartRetryPending = false          // controlLock

    /// Separate from `controlLock` so the main actor can install the handler without
    /// waiting on a CoreAudio restart that holds `controlLock`.
    private let handlerLock = NSLock()
    private var captureLostHandler: (@Sendable () -> Void)?  // handlerLock
    private var captureLost = false                          // handlerLock

    private let tapLock = NSLock()
    private var file: AVAudioFile?                   // tapLock
    private var pipeline: DeviceCapturePipeline?     // tapLock
    private var lastPowerDb: Float = DeviceAudioRecorder.silentPowerDb  // tapLock
    private var meteringEnabled = true               // tapLock
    private var loggedWriteError = false             // tapLock

    /// Restarts run here, never on the thread that posted the notification: that can
    /// be a thread inside `record()` that already holds `controlLock`.
    private let restartQueue = DispatchQueue(
        label: "com.hyperwhisper.audio.device-recorder.restart",
        qos: .userInitiated
    )

    init(
        deviceID: AudioDeviceID,
        url: URL?,
        defaultInputDeviceID: @escaping @Sendable () -> AudioDeviceID? = {
            CoreAudioDeviceHelper.getSystemDefaultInputDeviceID()
        }
    ) {
        self.deviceID = deviceID
        self.url = url
        self.defaultInputDeviceID = defaultInputDeviceID
        super.init()
    }

    /// Called once, on a background queue, if capture stops on its own because no input
    /// device could be reopened after a configuration change. If that already
    /// happened, `handler` runs at once. Never called after a normal `stop()`.
    func setCaptureLostHandler(_ handler: @escaping @Sendable () -> Void) {
        handlerLock.lock()
        let alreadyLost = captureLost
        if !alreadyLost {
            captureLostHandler = handler
        }
        handlerLock.unlock()
        if alreadyLost {
            handler()
        }
    }

    deinit {
        if let configObserver {
            NotificationCenter.default.removeObserver(configObserver)
        }
    }

    // MARK: LiveAudioRecording

    var isMeteringEnabled: Bool {
        get {
            tapLock.lock()
            defer { tapLock.unlock() }
            return meteringEnabled
        }
        set {
            tapLock.lock()
            meteringEnabled = newValue
            tapLock.unlock()
        }
    }

    /// Open the device and start writing. Returns `false` (and leaves no file behind)
    /// if the device cannot be opened. Blocking.
    func record() -> Bool {
        controlLock.lock()
        defer { controlLock.unlock() }
        guard !isRunning else { return true }

        if let url {
            do {
                let newFile = try Self.makeOutputFile(url: url)
                tapLock.lock()
                file = newFile
                tapLock.unlock()
            } catch {
                AppLogger.audio.error("Could not create the WAV for the chosen microphone: \(error.localizedDescription, privacy: .public)")
                return false
            }
        }

        let newEngine = AVAudioEngine()
        do {
            try startEngine(newEngine, deviceID: deviceID)
        } catch {
            AppLogger.audio.error("Could not start capture on the chosen microphone (device \(self.deviceID, privacy: .public)): \(String(describing: error), privacy: .public)")
            tapLock.lock()
            pipeline = nil
            file = nil
            tapLock.unlock()
            removeFileIfPresent()
            return false
        }

        engine = newEngine
        restartBudget.recordSuccess()
        isRunning = true
        observeConfigurationChanges(of: newEngine)
        return true
    }

    /// Stop the engine and finalize the WAV. Safe to call more than once. Blocking.
    func stop() {
        controlLock.lock()
        defer { controlLock.unlock() }
        stopLocked()
    }

    /// The level is computed on the tap thread for every buffer; nothing to refresh.
    func updateMeters() {}

    func averagePower(forChannel channelNumber: Int) -> Float {
        tapLock.lock()
        defer { tapLock.unlock() }
        return lastPowerDb
    }

    func deleteRecording() -> Bool {
        stop()
        return removeFileIfPresent()
    }

    // MARK: Engine

    /// Select the device, build the converter for its format, tap it and start.
    /// Caller holds `controlLock`.
    private func startEngine(_ engine: AVAudioEngine, deviceID: AudioDeviceID) throws {
        let input = engine.inputNode
        try Self.setInputDevice(deviceID, on: input)

        guard let format = Self.tapFormat(for: input) else {
            throw DeviceAudioRecorderError.noInputFormat
        }

        tapLock.lock()
        let target = file?.processingFormat ?? Self.outputFormat
        let newPipeline = DeviceCapturePipeline(inputFormat: format, outputFormat: target)
        pipeline = newPipeline
        tapLock.unlock()
        guard newPipeline != nil else {
            throw DeviceAudioRecorderError.converterUnavailable
        }

        AppLogger.audio.info("Chosen microphone opened: \(format.sampleRate, privacy: .public) Hz, \(format.channelCount, privacy: .public) channel(s)")

        input.installTap(onBus: 0, bufferSize: 1024, format: format) { [weak self] buffer, _ in
            self?.process(buffer)
        }
        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            throw error
        }
    }

    /// Stop and drop the engine, keeping the file open. Caller holds `controlLock`.
    private func tearDownEngineLocked() {
        if let configObserver {
            NotificationCenter.default.removeObserver(configObserver)
            self.configObserver = nil
        }
        if let engine {
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
        }
        engine = nil

        tapLock.lock()
        flushPipelineLocked()
        pipeline = nil
        tapLock.unlock()
    }

    /// Caller holds `controlLock`.
    private func stopLocked() {
        tearDownEngineLocked()
        isRunning = false
        restartRetryPending = false

        tapLock.lock()
        // Releasing the last reference closes the file and writes the final WAV
        // header. Nothing else holds one: the tap only reads `file` under this lock.
        file = nil
        lastPowerDb = Self.silentPowerDb
        tapLock.unlock()
    }

    /// Write what the converter still holds. Caller holds `tapLock`.
    private func flushPipelineLocked() {
        guard let pipeline, file != nil else { return }
        pipeline.flush { converted in
            self.writeLocked(converted)
        }
    }

    @discardableResult
    private func removeFileIfPresent() -> Bool {
        guard let url else { return true }
        guard FileManager.default.fileExists(atPath: url.path) else { return true }
        do {
            try FileManager.default.removeItem(at: url)
            return true
        } catch {
            AppLogger.audio.warning("Could not delete the chosen microphone's WAV: \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    // MARK: Tap

    private func process(_ buffer: AVAudioPCMBuffer) {
        tapLock.lock()
        defer { tapLock.unlock() }
        guard let pipeline, let source = pipeline.monoSource(for: buffer) else { return }

        if meteringEnabled {
            lastPowerDb = Self.powerDb(of: source)
        }
        guard file != nil else { return }
        pipeline.convert(source) { converted in
            self.writeLocked(converted)
        }
    }

    /// Caller holds `tapLock`.
    private func writeLocked(_ buffer: AVAudioPCMBuffer) {
        do {
            try file?.write(from: buffer)
        } catch {
            // Once per recording: a full disk would otherwise log at buffer rate.
            if !loggedWriteError {
                loggedWriteError = true
                AppLogger.audio.error("Writing the chosen microphone's audio failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    // MARK: Configuration Changes

    /// Caller holds `controlLock`.
    private func observeConfigurationChanges(of engine: AVAudioEngine) {
        configObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine,
            queue: nil
        ) { [weak self] _ in
            guard let self else { return }
            self.restartQueue.async { [weak self] in
                self?.handleConfigurationChange()
            }
        }
    }

    private func handleConfigurationChange() {
        controlLock.lock()
        guard isRunning, engine != nil else {
            controlLock.unlock()
            return
        }
        AppLogger.audio.notice("Chosen microphone changed configuration mid-capture - restarting capture")
        SentryService.addBreadcrumb(
            message: "Chosen microphone configuration changed mid-capture",
            category: "audio.recording",
            level: .warning
        )
        tearDownEngineLocked()
        let lostHandler = restartLocked()
        controlLock.unlock()
        lostHandler?()
    }

    private func retryRestart() {
        controlLock.lock()
        guard restartRetryPending else {
            controlLock.unlock()
            return
        }
        restartRetryPending = false
        let lostHandler = restartLocked()
        controlLock.unlock()
        lostHandler?()
    }

    /// Reopen capture into the same file. Returns the capture-lost handler to call
    /// (after `controlLock` is released) when the recorder gives up, else `nil`.
    /// Caller holds `controlLock`; the engine is already torn down.
    private func restartLocked() -> (@Sendable () -> Void)? {
        guard isRunning, engine == nil else { return nil }

        if startOnAnyInputLocked() {
            restartBudget.recordSuccess()
            return nil
        }

        guard restartBudget.recordFailure() else {
            AppLogger.audio.notice("Capture restart failed (\(self.restartBudget.consecutiveFailures, privacy: .public) in a row) - retrying shortly")
            restartRetryPending = true
            restartQueue.asyncAfter(deadline: .now() + Self.restartRetryDelay) { [weak self] in
                self?.retryRestart()
            }
            return nil
        }

        AppLogger.audio.error("No input device could be reopened after a configuration change - stopping capture; the file keeps what was recorded")
        SentryService.addBreadcrumb(
            message: "Chosen microphone capture lost after a configuration change",
            category: "audio.recording",
            level: .warning
        )
        stopLocked()
        return takeCaptureLostHandler()
    }

    /// Start a fresh engine on the chosen device, else on the macOS default input.
    /// Caller holds `controlLock`.
    private func startOnAnyInputLocked() -> Bool {
        var candidates: [AudioDeviceID] = [deviceID]
        if let fallback = defaultInputDeviceID(),
           fallback != kAudioObjectUnknown,
           fallback != deviceID {
            candidates.append(fallback)
        }

        for candidate in candidates {
            // A fresh engine: the old one's input unit may still point at a device
            // that is gone.
            let newEngine = AVAudioEngine()
            do {
                try startEngine(newEngine, deviceID: candidate)
            } catch {
                AppLogger.audio.notice("Could not reopen capture on device \(candidate, privacy: .public): \(String(describing: error), privacy: .public)")
                continue
            }
            engine = newEngine
            observeConfigurationChanges(of: newEngine)
            if candidate == deviceID {
                AppLogger.audio.info("Chosen microphone capture restarted after a configuration change")
            } else {
                AppLogger.audio.notice("Chosen microphone is unavailable - continuing the recording from the macOS default input (device \(candidate, privacy: .public))")
            }
            return true
        }
        return false
    }

    /// Mark capture as lost and hand back the handler, at most once.
    private func takeCaptureLostHandler() -> (@Sendable () -> Void)? {
        handlerLock.lock()
        defer { handlerLock.unlock() }
        captureLost = true
        let handler = captureLostHandler
        captureLostHandler = nil
        return handler
    }
}

// MARK: - Restart Budget

/// Counts restart attempts that failed in a row. A success resets the count, so only
/// a device that keeps failing ends the capture, never a long recording that simply
/// sees many configuration changes.
struct CaptureRestartBudget: Equatable, Sendable {
    let maxConsecutiveFailures: Int
    private(set) var consecutiveFailures = 0

    init(maxConsecutiveFailures: Int) {
        self.maxConsecutiveFailures = maxConsecutiveFailures
    }

    mutating func recordSuccess() {
        consecutiveFailures = 0
    }

    /// Count one failure. Returns `true` when the capture should give up.
    mutating func recordFailure() -> Bool {
        consecutiveFailures += 1
        return consecutiveFailures >= maxConsecutiveFailures
    }
}

// MARK: - Conversion Pipeline

/// Converts input-node buffers to the 16 kHz mono Int16 output format.
///
/// The same two paths as `StreamingAudioCapture.processAudioBuffer`: more than two
/// channels are summed to mono by hand (with 1/sqrt(N) scaling), because
/// `AVAudioConverter` only keeps channels 1-2 and a multi-track recorder may carry the
/// voice on any track; one or two channels convert directly. The converter is drained
/// in a loop so a large tap buffer never leaves audio queued inside it.
///
/// Not thread-safe: `DeviceAudioRecorder` only uses it under its `tapLock`.
final class DeviceCapturePipeline {
    let inputChannelCount: AVAudioChannelCount
    private let converter: AVAudioConverter
    private let outputBuffer: AVAudioPCMBuffer
    private let monoBuffer: AVAudioPCMBuffer?

    init?(inputFormat: AVAudioFormat, outputFormat: AVAudioFormat) {
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else { return nil }
        inputChannelCount = inputFormat.channelCount

        let converterSource: AVAudioFormat
        if inputFormat.channelCount > 2 {
            guard let mono = AVAudioFormat(
                commonFormat: .pcmFormatFloat32,
                sampleRate: inputFormat.sampleRate,
                channels: 1,
                interleaved: false
            ),
            let buffer = AVAudioPCMBuffer(
                pcmFormat: mono,
                frameCapacity: max(AVAudioFrameCount(inputFormat.sampleRate * 0.1), AVAudioFrameCount(16384))
            ) else { return nil }
            monoBuffer = buffer
            converterSource = mono
        } else {
            monoBuffer = nil
            converterSource = inputFormat
        }

        guard let converter = AVAudioConverter(from: converterSource, to: outputFormat),
              let output = AVAudioPCMBuffer(
                pcmFormat: outputFormat,
                frameCapacity: AVAudioFrameCount(outputFormat.sampleRate * 0.1)
              ) else { return nil }
        self.converter = converter
        self.outputBuffer = output
    }

    /// The buffer to convert: `buffer` itself for 1-2 channels, else its mono mix.
    /// `nil` if a multi-channel buffer is not deinterleaved float.
    func monoSource(for buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard let monoBuffer else { return buffer }
        guard !buffer.format.isInterleaved,
              let input = buffer.floatChannelData,
              let mono = monoBuffer.floatChannelData else { return nil }

        let channels = Int(buffer.format.channelCount)
        // Clamp to the destination capacity (issue #249).
        let frames = min(Int(buffer.frameLength), Int(monoBuffer.frameCapacity))
        let scale = 1.0 / Float(channels).squareRoot()
        let out = mono[0]
        for i in 0..<frames {
            out[i] = 0
        }
        for channel in 0..<channels {
            let samples = input[channel]
            for i in 0..<frames {
                out[i] += samples[i] * scale
            }
        }
        monoBuffer.frameLength = AVAudioFrameCount(frames)
        return monoBuffer
    }

    /// Convert `source` and hand every output chunk to `write`.
    func convert(_ source: AVAudioPCMBuffer, write: (AVAudioPCMBuffer) -> Void) {
        var providedInput = false
        while true {
            outputBuffer.frameLength = 0
            var error: NSError?
            let status = converter.convert(to: outputBuffer, error: &error) { _, outStatus in
                if providedInput {
                    outStatus.pointee = .noDataNow
                    return nil
                }
                providedInput = true
                outStatus.pointee = .haveData
                return source
            }
            if status == .error {
                AppLogger.audio.warning("Chosen microphone conversion error: \(error?.localizedDescription ?? "unknown", privacy: .public)")
                return
            }
            if outputBuffer.frameLength == 0 { return }
            write(outputBuffer)
        }
    }

    /// Emit what the converter still holds (the resampler's tail). After this the
    /// pipeline is spent.
    func flush(write: (AVAudioPCMBuffer) -> Void) {
        while true {
            outputBuffer.frameLength = 0
            var error: NSError?
            let status = converter.convert(to: outputBuffer, error: &error) { _, outStatus in
                outStatus.pointee = .endOfStream
                return nil
            }
            if status == .error || outputBuffer.frameLength == 0 { return }
            write(outputBuffer)
            if status == .endOfStream { return }
        }
    }
}
