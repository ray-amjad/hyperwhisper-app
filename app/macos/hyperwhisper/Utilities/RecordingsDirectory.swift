//
//  RecordingsDirectory.swift
//  hyperwhisper
//

import Foundation

/// Resolves the configured recordings directory or the legacy default location.
enum RecordingsDirectory {
    static func resolve(configuredPath: String?) -> URL {
        if let configuredPath, !configuredPath.isEmpty {
            return URL(fileURLWithPath: configuredPath, isDirectory: true)
        }

        return FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Recordings", isDirectory: true)
    }
}
