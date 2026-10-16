//
//  AudioMuffler.swift
//  hyperwhisper
//
//  AUDIO MUFFLER
//  Muffles other apps' audio while recording, so music sounds like it plays
//  in the next room instead of stopping.
//
//  HOW IT WORKS:
//  1. A Core Audio process tap (macOS 14.2+) captures the stereo mix of every
//     process except HyperWhisper, with `.mutedWhenTapped`, so the originals
//     go silent while the tap runs.
//  2. A private aggregate device pairs that tap (input) with the default
//     output device (output). If the default output changes mid-recording,
//     a new tap and aggregate are built on the new device before the old
//     ones are destroyed, so the originals never play unmuted in between
//     (if the build fails, everything is removed so audio plays normally).
//     A sample-rate change on the same device rebuilds the same way.
//  3. The aggregate's IOProc runs the tapped audio through a 4-pole low-pass
//     filter and writes it to the output device.
//  4. The cutoff glides from fully open down to `muffledCutoffHz` on start
//     and back up on stop, so neither edge clicks. Teardown waits for the
//     glide back up to finish.
//
//  The first tap triggers the "System Audio Recording" permission prompt
//  (NSAudioCaptureUsageDescription). If the user denies it, the tap delivers
//  silence, so other audio is effectively muted for the recording instead.
//
//  The process exits with its taps destroyed, so a crash mid-recording never
//  leaves the user's other apps muted.
//

import Foundation
import CoreAudio
import Atomics

final class AudioMuffler: @unchecked Sendable {

    // MARK: - Tuning

    /// Cutoff while recording. Low enough that speech-band energy (300 Hz -
    /// 4 kHz) from the speakers mostly disappears, high enough that the beat
    /// and bass stay audible.
    static let muffledCutoffHz: Double = 400

    /// Cutoff when "open". Clamped to 45% of the sample rate at runtime.
    static let openCutoffHz: Double = 20_000

    /// Time constant of the cutoff glide. About 3 time constants to settle.
    static let glideTimeConstant: Double = 0.06

    /// How long `stop()` waits for the glide back up before tearing down.
    static let teardownDelay: TimeInterval = 0.25

    // MARK: - Aggregate identity

    /// Name and UID prefix of the private aggregate. It is visible to this
    /// process's own device list, so device pickers must skip it.
    static let aggregateName = "HyperWhisper Muffle"
    static let aggregateUIDPrefix = "com.hyperwhisper.muffle."

    /// True for the muffle aggregate (matched by UID prefix, or by name).
    static func isMuffleAggregate(uid: String, name: String) -> Bool {
        uid.hasPrefix(aggregateUIDPrefix) || name == aggregateName
    }

    // MARK: - State (only touched on `controlQueue`)

    private let controlQueue = DispatchQueue(label: "com.hyperwhisper.audio-muffler", qos: .userInitiated)
    private let ioQueue = DispatchQueue(label: "com.hyperwhisper.audio-muffler.io", qos: .userInteractive)
    /// The HAL delivers property listeners here, never on `controlQueue`.
    /// A listener only hops to `controlQueue` and returns, so removing a
    /// listener from `controlQueue` (which can happen while handling that
    /// very listener's change) never waits on a block running on its own
    /// queue.
    private let listenerQueue = DispatchQueue(label: "com.hyperwhisper.audio-muffler.listener", qos: .userInitiated)

    /// The running tap + aggregate, nil while the muffler is idle.
    private var graph: Graph?
    private var dsp: MuffleDSP? { graph?.dsp }
    private var pendingTeardown: DispatchWorkItem?
    /// Default-output listener, registered while the muffler is live.
    private var defaultOutputListener: AudioObjectPropertyListenerBlock?

    // MARK: - Public API

    /// Starts muffling other apps' audio. Returns at once; the tap is built on
    /// a background queue so a first-run permission prompt never blocks the
    /// main thread.
    func start() {
        controlQueue.async { [self] in
            if let pending = pendingTeardown {
                // A new recording started during the previous glide out:
                // keep the running tap and glide back down.
                pending.cancel()
                pendingTeardown = nil
            }
            if let dsp {
                dsp.setTargetCutoff(Self.muffledCutoffHz)
                AppLogger.audio.info("🔉 Muffle: re-engaged running tap")
                return
            }
            do {
                let built = try build()
                graph = built
                built.dsp?.setTargetCutoff(Self.muffledCutoffHz)
                addDefaultOutputListener()
                AppLogger.audio.info("🔉 Muffle: other audio muffled for recording")
            } catch {
                AppLogger.audio.error("🔉 Muffle: failed to start (\(error.localizedDescription, privacy: .public)); recording continues")
                teardown()
            }
        }
    }

    /// Glides the filter back open, then removes the tap so other apps play
    /// through untouched again.
    func stop() {
        controlQueue.async { [self] in
            guard let dsp else { return }
            dsp.setTargetCutoff(Self.openCutoffHz)
            let work = DispatchWorkItem { [weak self] in
                guard let self else { return }
                self.pendingTeardown = nil
                self.teardown()
                AppLogger.audio.info("🔊 Muffle: other audio restored")
            }
            pendingTeardown = work
            controlQueue.asyncAfter(deadline: .now() + Self.teardownDelay, execute: work)
        }
    }

    // MARK: - Default output changes

    /// The global tap keeps the originals silent whatever the default output
    /// is, so the muffled mix has to follow the default output: AirPods
    /// connecting must not leave it playing from the old speakers, and a
    /// removed device must not leave the rest of the recording silent.
    private func addDefaultOutputListener() {
        guard defaultOutputListener == nil else { return }
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            // Delivered on `listenerQueue`. Handle it after this block has
            // returned, so a teardown that removes this listener never runs
            // inside the listener's own delivery.
            guard let self else { return }
            self.controlQueue.async { self.handleDefaultOutputChange() }
        }
        var address = Self.defaultOutputAddress()
        let status = AudioObjectAddPropertyListenerBlock(
            AudioObjectID(kAudioObjectSystemObject), &address, listenerQueue, listener
        )
        if status == noErr {
            defaultOutputListener = listener
        } else {
            AppLogger.audio.warning("🔉 Muffle: could not watch the default output (\(status)); a device change will not move the muffled audio")
        }
    }

    private func removeDefaultOutputListener() {
        guard let listener = defaultOutputListener else { return }
        var address = Self.defaultOutputAddress()
        AudioObjectRemovePropertyListenerBlock(
            AudioObjectID(kAudioObjectSystemObject), &address, listenerQueue, listener
        )
        defaultOutputListener = nil
    }

    private func handleDefaultOutputChange() {
        // A change queued before teardown removed the listener: ignore it.
        guard defaultOutputListener != nil, dsp != nil else { return }
        if let pending = pendingTeardown {
            // Already gliding out: finish now rather than move devices.
            pending.cancel()
            pendingTeardown = nil
            teardown()
            AppLogger.audio.info("🔊 Muffle: default output changed during glide out; other audio restored")
            return
        }
        guard let newDevice = Self.defaultOutputDevice() else {
            teardown()
            AppLogger.audio.warning("🔊 Muffle: no default output device; muffle removed so audio plays normally")
            return
        }
        guard newDevice != graph?.outputDeviceID else { return }
        rebuildGraph(reason: "moved to the new default output device")
    }

    /// A sample-rate change on the same device (a Bluetooth headset moving to
    /// HFP, a new rate in Audio MIDI Setup) keeps the device ID, so only this
    /// catches it. The DSP's coefficients and maximum cutoff are computed for
    /// one rate, so rebuild at the new one, the same way as a device move.
    private func handleSampleRateChange() {
        guard let current = graph else { return }
        // Compare the aggregate's rate: that is the rate the IOProc runs at.
        // The output device's listener is only a trigger, and a rebuild only
        // happens while the two rates differ, so it cannot loop.
        guard let rate = Self.nominalSampleRate(current.aggregateID),
              abs(rate - current.sampleRate) > 0.5 else { return }
        if let pending = pendingTeardown {
            // Already gliding out: finish now rather than rebuild.
            pending.cancel()
            pendingTeardown = nil
            teardown()
            AppLogger.audio.info("🔊 Muffle: sample rate changed during glide out; other audio restored")
            return
        }
        rebuildGraph(reason: "rebuilt for the new sample rate")
    }

    /// Replaces the running graph with one built on the current default
    /// output, starting muffled.
    ///
    /// The new graph is built BEFORE the old one is destroyed. The old tap's
    /// `.mutedWhenTapped` keeps the originals silent until the new tap has
    /// taken over (both taps mute, so overlapping them is harmless);
    /// destroying first would let the originals play at full volume for the
    /// whole build, which takes hundreds of ms on Bluetooth.
    private func rebuildGraph(reason: String) {
        let old = graph
        do {
            let built = try build(startMuffled: true)
            graph = built
            built.dsp?.setTargetCutoff(Self.muffledCutoffHz)
            old?.destroy()
            AppLogger.audio.info("🔉 Muffle: \(reason, privacy: .public)")
        } catch {
            // build() cleaned up its own partial graph; this removes the old one.
            AppLogger.audio.error("🔊 Muffle: rebuild failed (\(error.localizedDescription, privacy: .public)); muffle removed so audio plays normally")
            teardown()
        }
    }

    // MARK: - Build / teardown

    /// One tap + aggregate + IOProc, bound to one output device.
    private struct Graph {
        var tapID = AudioObjectID(kAudioObjectUnknown)
        var aggregateID = AudioObjectID(kAudioObjectUnknown)
        var ioProcID: AudioDeviceIOProcID?
        /// The output device the aggregate is bound to.
        var outputDeviceID = AudioDeviceID(kAudioObjectUnknown)
        var sampleRate: Double = 0
        var dsp: MuffleDSP?
        /// Sample-rate listener, registered on each of `rateWatchedObjects`.
        var rateListener: AudioObjectPropertyListenerBlock?
        var rateListenerQueue: DispatchQueue?
        var rateWatchedObjects: [AudioObjectID] = []

        /// Stops and destroys whatever parts exist. Safe on a partial graph.
        func destroy() {
            if let listener = rateListener, let queue = rateListenerQueue {
                var address = AudioMuffler.sampleRateAddress()
                for object in rateWatchedObjects {
                    // Fails harmlessly if the device has already gone.
                    AudioObjectRemovePropertyListenerBlock(object, &address, queue, listener)
                }
            }
            if aggregateID != kAudioObjectUnknown, let procID = ioProcID {
                AudioDeviceStop(aggregateID, procID)
                AudioDeviceDestroyIOProcID(aggregateID, procID)
            }
            if aggregateID != kAudioObjectUnknown {
                AudioHardwareDestroyAggregateDevice(aggregateID)
            }
            if tapID != kAudioObjectUnknown {
                AudioHardwareDestroyProcessTap(tapID)
            }
        }
    }

    /// Builds and starts a new graph on the current default output. Does not
    /// touch `graph`; on failure it destroys whatever it had created.
    /// - Parameter startMuffled: start the filter already at the muffled
    ///   cutoff (a device move mid-recording) instead of fully open.
    private func build(startMuffled: Bool = false) throws -> Graph {
        var graph = Graph()
        do {
            try build(into: &graph, startMuffled: startMuffled)
            return graph
        } catch {
            graph.destroy()
            throw error
        }
    }

    private func build(into graph: inout Graph, startMuffled: Bool) throws {
        guard let outputDevice = Self.defaultOutputDevice() else {
            throw MuffleError("no default output device")
        }
        guard let outputUID = Self.deviceUID(outputDevice) else {
            throw MuffleError("could not read output device UID")
        }

        // Exclude HyperWhisper itself, so its own start/stop sounds stay clear.
        var excluded: [AudioObjectID] = []
        if let ownProcess = Self.processObject(for: getpid()) {
            excluded.append(ownProcess)
        }

        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: excluded)
        description.uuid = UUID()
        description.name = "HyperWhisper Muffle"
        description.isPrivate = true
        description.muteBehavior = .mutedWhenTapped

        var newTap = AudioObjectID(kAudioObjectUnknown)
        var status = AudioHardwareCreateProcessTap(description, &newTap)
        guard status == noErr, newTap != kAudioObjectUnknown else {
            throw MuffleError("AudioHardwareCreateProcessTap failed (\(status))")
        }
        graph.tapID = newTap

        let tapChannels = Self.tapChannelCount(newTap) ?? 2

        let aggregateDescription: [String: Any] = [
            kAudioAggregateDeviceNameKey: Self.aggregateName,
            kAudioAggregateDeviceUIDKey: "\(Self.aggregateUIDPrefix)\(UUID().uuidString)",
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [
                [kAudioSubDeviceUIDKey: outputUID]
            ],
            kAudioAggregateDeviceTapListKey: [
                [
                    kAudioSubTapUIDKey: description.uuid.uuidString,
                    kAudioSubTapDriftCompensationKey: true
                ]
            ]
        ]

        var newAggregate = AudioObjectID(kAudioObjectUnknown)
        status = AudioHardwareCreateAggregateDevice(aggregateDescription as CFDictionary, &newAggregate)
        guard status == noErr, newAggregate != kAudioObjectUnknown else {
            throw MuffleError("AudioHardwareCreateAggregateDevice failed (\(status))")
        }
        graph.aggregateID = newAggregate
        graph.outputDeviceID = outputDevice

        let sampleRate = Self.nominalSampleRate(newAggregate) ?? 48_000
        let dsp = MuffleDSP(
            sampleRate: sampleRate,
            tapChannels: tapChannels,
            startCutoff: startMuffled ? Self.muffledCutoffHz : nil
        )
        graph.sampleRate = sampleRate
        graph.dsp = dsp

        var procID: AudioDeviceIOProcID?
        status = AudioDeviceCreateIOProcIDWithBlock(&procID, newAggregate, ioQueue) { _, inputData, _, outputData, _ in
            dsp.process(input: inputData, output: outputData)
        }
        guard status == noErr, let procID else {
            throw MuffleError("AudioDeviceCreateIOProcIDWithBlock failed (\(status))")
        }
        graph.ioProcID = procID

        status = AudioDeviceStart(newAggregate, procID)
        guard status == noErr else {
            throw MuffleError("AudioDeviceStart failed (\(status))")
        }

        watchSampleRate(of: &graph)
        if let rate = Self.nominalSampleRate(newAggregate), abs(rate - sampleRate) > 0.5 {
            // Changed between the read above and the listener registering.
            controlQueue.async { [weak self] in self?.handleSampleRateChange() }
        }

        AppLogger.audio.info("🔉 Muffle: tap \(newTap) + aggregate \(newAggregate) at \(Int(sampleRate)) Hz, \(tapChannels) tap channels")
    }

    /// Full stop: stop following the default output and remove the tap.
    private func teardown() {
        removeDefaultOutputListener()
        teardownGraph()
    }

    /// Destroys the IOProc, aggregate and tap, leaving the listener alone.
    /// Idempotent.
    private func teardownGraph() {
        graph?.destroy()
        graph = nil
    }

    /// Watches the nominal sample rate of the graph's aggregate and output
    /// device. Best effort: a failed registration is logged, not thrown.
    private func watchSampleRate(of graph: inout Graph) {
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            // Delivered on `listenerQueue`; handled on `controlQueue` after
            // this block returns (see `addDefaultOutputListener`).
            guard let self else { return }
            self.controlQueue.async { self.handleSampleRateChange() }
        }
        graph.rateListener = listener
        graph.rateListenerQueue = listenerQueue
        var address = Self.sampleRateAddress()
        for object in [graph.aggregateID, graph.outputDeviceID] where object != kAudioObjectUnknown {
            let status = AudioObjectAddPropertyListenerBlock(object, &address, listenerQueue, listener)
            if status == noErr {
                graph.rateWatchedObjects.append(object)
            } else {
                AppLogger.audio.warning("🔉 Muffle: could not watch the sample rate of device \(object) (\(status))")
            }
        }
    }

    // MARK: - Core Audio helpers

    fileprivate static func sampleRateAddress() -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyNominalSampleRate,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
    }

    private static func defaultOutputAddress() -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
    }

    private static func defaultOutputDevice() -> AudioDeviceID? {
        var address = defaultOutputAddress()
        var deviceID = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &deviceID)
        return status == noErr && deviceID != kAudioObjectUnknown ? deviceID : nil
    }

    private static func deviceUID(_ deviceID: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var uid: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &uid)
        guard status == noErr, let uid else { return nil }
        return uid.takeRetainedValue() as String
    }

    private static func processObject(for pid: pid_t) -> AudioObjectID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var pid = pid
        var object = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject), &address,
            UInt32(MemoryLayout<pid_t>.size), &pid, &size, &object
        )
        return status == noErr && object != kAudioObjectUnknown ? object : nil
    }

    private static func tapChannelCount(_ tapID: AudioObjectID) -> Int? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioTapPropertyFormat,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var format = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        let status = AudioObjectGetPropertyData(tapID, &address, 0, nil, &size, &format)
        guard status == noErr, format.mChannelsPerFrame > 0 else { return nil }
        return Int(format.mChannelsPerFrame)
    }

    private static func nominalSampleRate(_ deviceID: AudioDeviceID) -> Double? {
        var address = sampleRateAddress()
        var rate = Float64(0)
        var size = UInt32(MemoryLayout<Float64>.size)
        let status = AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &rate)
        return status == noErr && rate > 0 ? rate : nil
    }
}

private struct MuffleError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

// MARK: - DSP

/// The real-time half: runs on the aggregate device's IO thread.
/// No allocation, no locks, no Objective-C after init. The only shared value
/// is the target cutoff, read through an atomic.
final class MuffleDSP: @unchecked Sendable {
    static let maxChannels = 16
    /// Two cascaded biquads = a 4-pole (24 dB/octave) low-pass.
    static let stages = 2

    private let sampleRate: Double
    private let tapChannels: Int
    private let maxCutoff: Double

    /// Target cutoff in Hz, written from the control queue.
    private let targetCutoff: ManagedAtomic<Int>
    /// Current (gliding) cutoff in Hz; IO thread only.
    private var currentCutoff: Double

    // Per output channel, per stage: z1, z2 (transposed direct form II).
    private let state: UnsafeMutablePointer<Float>
    // Flattened channel views of the current buffers: base pointer + stride.
    private let inPointers: UnsafeMutablePointer<UnsafeMutablePointer<Float>?>
    private let inStrides: UnsafeMutablePointer<Int>
    private let outPointers: UnsafeMutablePointer<UnsafeMutablePointer<Float>?>
    private let outStrides: UnsafeMutablePointer<Int>

    init(sampleRate: Double, tapChannels: Int, startCutoff: Double? = nil) {
        self.sampleRate = sampleRate
        self.tapChannels = max(1, tapChannels)
        let maxCutoff = min(AudioMuffler.openCutoffHz, sampleRate * 0.45)
        self.maxCutoff = maxCutoff
        let initialCutoff = min(max(startCutoff ?? maxCutoff, 20), maxCutoff)
        self.currentCutoff = initialCutoff
        self.targetCutoff = ManagedAtomic(Int(initialCutoff))

        let stateCount = Self.maxChannels * Self.stages * 2
        state = .allocate(capacity: stateCount)
        state.initialize(repeating: 0, count: stateCount)
        inPointers = .allocate(capacity: Self.maxChannels * 2)
        inPointers.initialize(repeating: nil, count: Self.maxChannels * 2)
        inStrides = .allocate(capacity: Self.maxChannels * 2)
        inStrides.initialize(repeating: 0, count: Self.maxChannels * 2)
        outPointers = .allocate(capacity: Self.maxChannels)
        outPointers.initialize(repeating: nil, count: Self.maxChannels)
        outStrides = .allocate(capacity: Self.maxChannels)
        outStrides.initialize(repeating: 0, count: Self.maxChannels)
    }

    deinit {
        state.deallocate()
        inPointers.deallocate()
        inStrides.deallocate()
        outPointers.deallocate()
        outStrides.deallocate()
    }

    func setTargetCutoff(_ hz: Double) {
        targetCutoff.store(Int(min(max(hz, 20), maxCutoff)), ordering: .relaxed)
    }

    func process(input: UnsafePointer<AudioBufferList>, output: UnsafeMutablePointer<AudioBufferList>) {
        let inList = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
        let outList = UnsafeMutableAudioBufferListPointer(output)

        // Start from silence on every output channel. Only the first
        // tap-wide channels (L/R) get the muffled mix below; centre, LFE,
        // rears and any extra interface outputs stay silent.
        for buffer in outList {
            if let data = buffer.mData { memset(data, 0, Int(buffer.mDataByteSize)) }
        }

        // Locate the tap. The aggregate lists the output device's own input
        // streams (if any) first and the tap's stream LAST, so walk the
        // buffer list from the end and take `tapChannels` channels from there.
        // Counting from the end, with no cap on how many device inputs come
        // before it, keeps a device's microphones out of the filter however
        // many input channels it has. Pointers are collected in reverse.
        let wanted = min(tapChannels, Self.maxChannels)
        var tapCount = 0
        var inFrames = Int.max
        var bufferIndex = inList.count - 1
        while bufferIndex >= 0 && tapCount < wanted {
            let buffer = inList[bufferIndex]
            bufferIndex -= 1
            let channels = Int(buffer.mNumberChannels)
            guard channels > 0 else { continue }
            // A tap buffer with no data: treat the tap as silent this cycle
            // rather than reaching past it into the device's inputs.
            guard let data = buffer.mData?.assumingMemoryBound(to: Float.self) else { break }
            inFrames = min(inFrames, Int(buffer.mDataByteSize) / (MemoryLayout<Float>.size * channels))
            var c = channels - 1
            while c >= 0 && tapCount < wanted {
                inPointers[tapCount] = data + c
                inStrides[tapCount] = channels
                tapCount += 1
                c -= 1
            }
        }
        guard tapCount > 0, inFrames != Int.max, inFrames > 0 else { return }

        // Output channels that carry audio: one per tap channel, and a mono
        // tap feeds both L and R.
        let outWanted = min(tapCount == 1 ? 2 : tapCount, Self.maxChannels)
        var outCount = 0
        var outFrames = Int.max
        for buffer in outList where outCount < outWanted {
            guard let data = buffer.mData?.assumingMemoryBound(to: Float.self) else { continue }
            let channels = Int(buffer.mNumberChannels)
            guard channels > 0 else { continue }
            outFrames = min(outFrames, Int(buffer.mDataByteSize) / (MemoryLayout<Float>.size * channels))
            for c in 0..<channels where outCount < outWanted {
                outPointers[outCount] = data + c
                outStrides[outCount] = channels
                outCount += 1
            }
        }
        guard outCount > 0, outFrames != Int.max else { return }

        let frames = min(inFrames, outFrames)

        // Glide the cutoff once per block, in the log domain.
        let target = Double(targetCutoff.load(ordering: .relaxed))
        let alpha = 1 - exp(-Double(frames) / (AudioMuffler.glideTimeConstant * sampleRate))
        currentCutoff = exp(log(currentCutoff) + (log(target) - log(currentCutoff)) * alpha)

        // RBJ low-pass, Q = 1/sqrt(2).
        let w0 = 2 * Double.pi * currentCutoff / sampleRate
        let cosW0 = cos(w0)
        let a = sin(w0) / (2 * 0.7071067811865476)
        let a0 = 1 + a
        let b0 = Float((1 - cosW0) / 2 / a0)
        let b1 = Float((1 - cosW0) / a0)
        let b2 = b0
        let a1 = Float(-2 * cosW0 / a0)
        let a2 = Float((1 - a) / a0)

        for oc in 0..<outCount {
            guard let outBase = outPointers[oc] else { continue }
            let outStride = outStrides[oc]
            // Tap channel `oc` (mono: channel 0); pointers were collected in reverse.
            let ic = tapCount - 1 - (tapCount == 1 ? 0 : oc)
            guard let inBase = inPointers[ic] else { continue }
            let inStride = inStrides[ic]
            let s = state + oc * Self.stages * 2

            var z1a = s[0], z2a = s[1], z1b = s[2], z2b = s[3]
            for f in 0..<frames {
                let x = inBase[f * inStride]
                let y1 = b0 * x + z1a
                z1a = b1 * x - a1 * y1 + z2a
                z2a = b2 * x - a2 * y1
                let y2 = b0 * y1 + z1b
                z1b = b1 * y1 - a1 * y2 + z2b
                z2b = b2 * y1 - a2 * y2
                outBase[f * outStride] = y2
            }
            // Flush denormals so a silent tail does not burn CPU.
            s[0] = abs(z1a) < 1e-20 ? 0 : z1a
            s[1] = abs(z2a) < 1e-20 ? 0 : z2a
            s[2] = abs(z1b) < 1e-20 ? 0 : z1b
            s[3] = abs(z2b) < 1e-20 ? 0 : z2b
        }
        // Output frames past the input length were zeroed at the top.
    }
}
