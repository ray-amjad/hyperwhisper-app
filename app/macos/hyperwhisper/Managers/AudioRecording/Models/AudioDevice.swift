//
//  AudioDevice.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation

// MARK: - Audio Device Model

/// Custom struct to represent audio devices on macOS
///
/// **Purpose:**
/// Replaces AVAudioSessionPortDescription which is iOS-only. Provides a platform-independent
/// way to represent audio input devices on macOS.
///
/// **Properties:**
/// - `id`: Unique identifier for SwiftUI list identification
/// - `name`: Human-readable display name shown in UI
/// - `uid`: CoreAudio system UID for device selection
///
/// **Conformances:**
/// - `Identifiable`: Enables use in SwiftUI lists and ForEach
/// - `Hashable`: Allows use in Sets and as Dictionary keys
/// - `Sendable`: Stated explicitly. Three immutable `String`s already earn the implicit
///   conformance, but device lists now cross a `Task.detached` boundary on the CoreAudio
///   scan path (`AudioDeviceScanSnapshot`, HYPERWHISPER-HP). Spelling it out
///   means adding a mutable or reference-typed property breaks the build at this
///   declaration rather than at a distant concurrency diagnostic.
///
/// **Usage:**
/// Used throughout the audio recording system to represent available microphones
/// and track the currently selected input device.
struct AudioDevice: Identifiable, Hashable, Sendable {
    let id: String      // Unique identifier
    let name: String    // Display name
    let uid: String     // System UID (CoreAudio)

    /// Initialize from system audio device
    ///
    /// **Parameters:**
    /// - `id`: Unique identifier (typically same as UID)
    /// - `name`: User-facing device name
    /// - `uid`: CoreAudio device UID for system-level identification
    init(id: String, name: String, uid: String) {
        self.id = id
        self.name = name
        self.uid = uid
    }
}
