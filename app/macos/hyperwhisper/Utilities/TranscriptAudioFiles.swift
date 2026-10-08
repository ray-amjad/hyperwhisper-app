//
//  TranscriptAudioFiles.swift
//  hyperwhisper
//
//  Which files on disk belong to one History transcript.
//
//  A transcript records two paths: `audioFilePath` (the recording or the
//  imported copy) and `trimmedAudioFilePath` (the VAD output). Two more files
//  can be written beside them that no Core Data column records:
//
//  - VAD's upload copy. When the trimmed WAV is too large to upload,
//    `VADProcessingService.convertTrimmedToM4AIfNeeded` writes
//    `<trimmed>.m4a` next to it and uploads that instead (issue #1513).
//  - A video import's audio track. `FileTranscriptionFlow` extracts
//    `<copied video>.m4a` next to the copied video and transcribes that.
//
//  The producers and every delete path take the names from here, so a delete
//  cannot forget a file the producer wrote, and the two cannot drift apart.
//  Adding a Core Data column for each artifact would also work, but it is a
//  schema change for files whose names are already fully determined by the
//  recorded paths.
//

import Foundation

/// The recorded audio paths of one transcript, as plain values.
struct TranscriptRecordedAudioPaths: Sendable, Equatable {
    let audioFilePath: String?
    let trimmedAudioFilePath: String?
}

/// Namespace for the transcript audio-file naming rules.
enum TranscriptAudioFiles {

    /// File extensions the import flow treats as video containers and extracts
    /// an audio track from before transcription.
    static let videoExtensions: Set<String> = ["mp4", "mov", "m4v"]

    /// Whether `url` is a video container that the import flow extracts audio from.
    static func isVideoFile(_ url: URL) -> Bool {
        videoExtensions.contains(url.pathExtension.lowercased())
    }

    /// Where the import flow writes a copied video's extracted audio track.
    static func extractedAudioURL(forVideo videoURL: URL) -> URL {
        videoURL.deletingPathExtension().appendingPathExtension("m4a")
    }

    /// Where VAD writes the compressed upload copy of a large trimmed WAV.
    static func uploadCopyURL(forTrimmedWAV trimmedURL: URL) -> URL {
        trimmedURL.deletingPathExtension().appendingPathExtension("m4a")
    }

    /// The unrecorded files that the producers can have written for a
    /// transcript with these recorded paths.
    ///
    /// A candidate only — the file may not exist, and it may not be safe to
    /// delete. A returned path never equals one of the two recorded paths, but
    /// the caller must still check that no OTHER transcript owns it (see
    /// `pathsToDelete(for:protectedPaths:)`).
    static func derivedArtifactPaths(for recorded: TranscriptRecordedAudioPaths) -> [String] {
        var derived: [String] = []

        if let trimmedPath = nonEmpty(recorded.trimmedAudioFilePath) {
            let trimmedURL = URL(fileURLWithPath: trimmedPath)
            // Same test the producer applies: only a WAV gets an upload copy.
            if trimmedURL.pathExtension.lowercased() == "wav" {
                derived.append(uploadCopyURL(forTrimmedWAV: trimmedURL).path)
            }
        }

        if let audioPath = nonEmpty(recorded.audioFilePath) {
            let audioURL = URL(fileURLWithPath: audioPath)
            if isVideoFile(audioURL) {
                derived.append(extractedAudioURL(forVideo: audioURL).path)
            }
        }

        let recordedPaths = Set([recorded.audioFilePath, recorded.trimmedAudioFilePath].compactMap { $0 })
        var seen = Set<String>()
        return derived.filter { !recordedPaths.contains($0) && seen.insert($0).inserted }
    }

    /// Every path that may have to be referenced to find who else owns the
    /// derived artifacts of `rows`: the recorded paths themselves and their
    /// derived candidates. A transcript that records any of these, or derives
    /// from one, can own one of the candidates.
    static func ownershipLookupPaths(for rows: [TranscriptRecordedAudioPaths]) -> [String] {
        var paths: [String] = []
        var seen = Set<String>()
        for row in rows {
            let derived = derivedArtifactPaths(for: row)
            guard !derived.isEmpty else { continue }
            let candidates = [row.audioFilePath, row.trimmedAudioFilePath].compactMap { nonEmpty($0) } + derived
            for path in candidates where seen.insert(path).inserted {
                paths.append(path)
            }
        }
        return paths
    }

    /// Every path a transcript that SURVIVES the delete owns or derives.
    static func protectedPaths(for survivors: [TranscriptRecordedAudioPaths]) -> Set<String> {
        var protected = Set<String>()
        for row in survivors {
            if let path = nonEmpty(row.audioFilePath) { protected.insert(path) }
            if let path = nonEmpty(row.trimmedAudioFilePath) { protected.insert(path) }
            protected.formUnion(derivedArtifactPaths(for: row))
        }
        return protected
    }

    /// The ordered list of files to remove when `rows` are deleted.
    ///
    /// Per row: the recorded original, the recorded trimmed file, then any
    /// derived artifact. The recorded paths keep their existing behaviour
    /// exactly (including duplicates, which auto-delete's stats rely on). A
    /// derived artifact is added once, and only when no row in the batch
    /// already records it and no surviving transcript owns or derives it
    /// (`protectedPaths`) — a derived name is a guess from a naming rule, so it
    /// must never take a file something else still points at.
    static func pathsToDelete(
        for rows: [TranscriptRecordedAudioPaths],
        protectedPaths: Set<String>
    ) -> [String] {
        let recordedInBatch = Set(rows.flatMap { [$0.audioFilePath, $0.trimmedAudioFilePath].compactMap { $0 } })
        var derivedAdded = Set<String>()
        var paths: [String] = []

        for row in rows {
            if let audioPath = row.audioFilePath {
                paths.append(audioPath)
            }
            if let trimmedPath = row.trimmedAudioFilePath {
                paths.append(trimmedPath)
            }
            for derived in derivedArtifactPaths(for: row)
            where !recordedInBatch.contains(derived)
                && !protectedPaths.contains(derived)
                && derivedAdded.insert(derived).inserted {
                paths.append(derived)
            }
        }

        return paths
    }

    private static func nonEmpty(_ path: String?) -> String? {
        guard let path, !path.isEmpty else { return nil }
        return path
    }
}
