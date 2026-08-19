//
//  FormattingHelpers.swift
//  hyperwhisper
//
//  Shared human-readable formatting helpers used across views and flows.

import Foundation

/// Format file size in bytes to human-readable string (e.g., "25 MB", "1.5 GB")
func formatFileSize(_ bytes: Int64) -> String {
    if bytes >= 1024 * 1024 * 1024 {
        let gb = Double(bytes) / (1024.0 * 1024.0 * 1024.0)
        return String(format: "%.1f GB", gb)
    } else {
        let mb = bytes / (1024 * 1024)
        return "\(mb) MB"
    }
}

/// Format a duration in seconds to a localized "minutes:seconds" string
func formatDuration(_ duration: TimeInterval) -> String {
    let minutes = Int(duration) / 60
    let seconds = Int(duration) % 60
    return "history.duration.format".localized(arguments: minutes, seconds)
}
