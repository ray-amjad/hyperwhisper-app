//
//  StreamingClientProtocol.swift
//  hyperwhisper
//
//  Shared interface for streaming transcription clients (remote WebSocket
//  and local on-device Parakeet). The single call site in
//  RecordingTranscriptionFlow+Streaming.swift holds this protocol type and
//  branches only on instantiation, not on every method call.
//

import Foundation

@MainActor
protocol StreamingClientProtocol: AnyObject {
    /// Emits transcript chunks. `isFinal == true` should be typed immediately;
    /// `isFinal == false` should be shown as a volatile preview.
    var onTranscriptUpdate: ((String, Bool) -> Void)? { get set }

    /// Fires after a graceful session teardown with the total audio duration
    /// processed (seconds) and credits consumed (0 for local providers).
    var onSessionComplete: ((Double, Double) -> Void)? { get set }

    /// Fires on unrecoverable errors. The session is already torn down.
    var onError: ((Error) -> Void)? { get set }

    /// Connection/streaming state transitions for UI feedback.
    var onConnectionStateChange: ((StreamingConnectionState) -> Void)? { get set }

    /// Normalized input level for waveform visualization.
    /// Values are clamped to 0.0...1.0 and delivered at roughly 30 FPS.
    var onAudioLevel: ((Float) -> Void)? { get set }

    /// Human-readable label used for history entries
    /// (e.g. "Parakeet V3 (On-Device Streaming)").
    var transcriptionProviderLabel: String { get }

    /// Begin a session. For local providers, `config` is mostly ignored.
    func startSession(config: StreamingSessionConfig) async throws

    /// Gracefully stop, draining any pending output as a final transcript
    /// update before firing `onSessionComplete`.
    func stopSession() async

    /// Abort without draining pending output.
    func cancel() async
}

extension StreamingClientProtocol {
    /// Default: cancel is treated as a graceful stop. Providers that need
    /// to discard pending finals on cancel should override.
    func cancel() async {
        await stopSession()
    }
}
