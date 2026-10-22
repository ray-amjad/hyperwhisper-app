//
//  PersistenceController+Transcripts.swift
//  hyperwhisper
//
//  Transcript reads, writes and audio-file bookkeeping.
//  Split out of PersistenceController.swift (#697); every body moved unchanged.
//

import CoreData
import Foundation
import SQLite3

extension PersistenceController {

    /// Finalize a recording session on stop in ONE serial write — collapses the
    /// old `updateRecordingSessionOnStop` + audioFormat mutation + save.
    func updateRecordingSessionOnStopInBackground(
        sessionID: NSManagedObjectID,
        audioFilePath: String,
        duration: TimeInterval,
        audioFormat: String
    ) async {
        await performWrite { context in
            guard let session = try? context.existingObject(with: sessionID) as? RecordingSession else {
                AppLogger.coreData.warning("updateRecordingSessionOnStopInBackground: session row not found — skipping")
                return
            }
            session.audioFilePath = audioFilePath
            session.durationInSeconds = duration
            session.endTime = Date()
            session.audioFormat = audioFormat
        }
    }

    /// Update a transcript's audio file path (and its recording session's path)
    /// on the serial writer — used by the background M4A conversion path.
    func updateTranscriptAudioFilePathInBackground(
        transcriptID: NSManagedObjectID,
        newPath: String
    ) async {
        await performWrite { context in
            guard let transcript = try? context.existingObject(with: transcriptID) as? Transcript else {
                AppLogger.coreData.warning("updateTranscriptAudioFilePathInBackground: transcript row not found — skipping")
                return
            }
            transcript.audioFilePath = newPath
            if let session = transcript.recordingSession {
                session.audioFilePath = newPath
            }
        }
    }

    /// Delete a recording session on the serial writer, then remove its audio file
    /// off the main thread. Reads the path inside the writer so nothing crosses a
    /// context boundary.
    func deleteRecordingSessionInBackground(
        sessionID: NSManagedObjectID,
        deleteAudioFile: Bool = true
    ) async {
        let filePathToRemove: String? = await performWrite { context -> String? in
            guard let session = try? context.existingObject(with: sessionID) as? RecordingSession else {
                AppLogger.coreData.debug("deleteRecordingSessionInBackground: session row not found — skipping")
                return nil
            }
            let path = deleteAudioFile ? session.audioFilePath : nil
            context.delete(session)
            return path
        }

        if let filePathToRemove {
            do {
                try FileManager.default.removeItem(atPath: filePathToRemove)
                AppLogger.audio.debug("Deleted incomplete audio file: \(filePathToRemove, privacy: .public)")
            } catch {
                AppLogger.audio.debug("Could not delete incomplete audio file (may not exist): \(error.localizedDescription)")
            }
        }
    }

    /// Stops every running retry (plain Retry or "Retry with") of rows being
    /// deleted, whichever path deletes them (#1743): History, auto-delete, a
    /// duplicate collapse. Otherwise the engine runs on for a row that is gone.
    /// Cancels at once on the main thread; from a background context it hops
    /// there, and the retry's own deleted-row checks cover the gap.
    static func stopRetries(ofDeleted transcriptIDs: [UUID]) {
        guard !transcriptIDs.isEmpty else { return }
        if Thread.isMainThread {
            MainActor.assumeIsolated {
                transcriptIDs.forEach(TranscriptActionHandler.cancelRetryTask(for:))
            }
        } else {
            Task { @MainActor in
                transcriptIDs.forEach(TranscriptActionHandler.cancelRetryTask(for:))
            }
        }
    }

    // MARK: - Transcript Audio Files

    /// The recorded audio paths of `transcript`, as plain values.
    /// Call on the queue of the transcript's context.
    static func recordedAudioPaths(of transcript: Transcript) -> TranscriptRecordedAudioPaths {
        TranscriptRecordedAudioPaths(
            audioFilePath: transcript.audioFilePath,
            trimmedAudioFilePath: transcript.value(forKey: "trimmedAudioFilePath") as? String
        )
    }

    /// Every file on disk that belongs to `transcripts`, in delete order.
    ///
    /// The single source for History's single and bulk Delete and for
    /// auto-delete, so the three cannot drift. It returns the two recorded
    /// paths of each row (unchanged behaviour) plus the files derived from them
    /// that no column records — VAD's `<trimmed>.m4a` upload copy (#1513) and
    /// a video import's extracted `.m4a` (see `TranscriptAudioFiles`).
    ///
    /// A derived path is a name guessed from a naming rule, so it is dropped
    /// when any transcript outside `transcripts` records it or derives the same
    /// name. If that ownership fetch fails, every derived path is dropped: a
    /// leaked file is recoverable, a deleted file another row plays is not.
    ///
    /// Must run on `context`'s queue, BEFORE the rows are deleted from it.
    static func audioFilePathsToDelete(
        for transcripts: [Transcript],
        in context: NSManagedObjectContext
    ) -> [String] {
        audioFilePathsToDelete(
            for: transcripts.map(Self.recordedAudioPaths(of:)),
            excluding: transcripts,
            in: context
        )
    }

    /// `audioFilePathsToDelete(for:in:)` for plain recorded paths. `excluding`
    /// lists the rows that do not count as owners (the rows being deleted).
    static func audioFilePathsToDelete(
        for rows: [TranscriptRecordedAudioPaths],
        excluding transcripts: [Transcript],
        in context: NSManagedObjectContext
    ) -> [String] {
        let lookupPaths = TranscriptAudioFiles.ownershipLookupPaths(for: rows)
        guard !lookupPaths.isEmpty else {
            return TranscriptAudioFiles.pathsToDelete(for: rows, protectedPaths: [])
        }

        let protectedPaths = pathsOwnedBySurvivors(of: lookupPaths, excluding: transcripts, in: context)
        return TranscriptAudioFiles.pathsToDelete(for: rows, protectedPaths: protectedPaths)
    }

    /// Of `paths`, the files no remaining transcript records or derives.
    /// For files no row records, such as the VAD output of a retry its row's
    /// delete cancelled (#1743): another row recorded from the same audio
    /// shares those names and keeps its files.
    ///
    /// Must run on `context`'s queue, AFTER the row is deleted from it.
    static func audioPathsNoTranscriptOwns(
        _ paths: [String],
        in context: NSManagedObjectContext
    ) -> [String] {
        guard !paths.isEmpty else { return [] }
        let owned = pathsOwnedBySurvivors(of: paths, excluding: [], in: context)
        return paths.filter { !owned.contains($0) }
    }

    /// Every path the transcripts outside `transcripts` that record any of
    /// `lookupPaths` own or derive. If that fetch fails, every lookup path
    /// counts as owned: a leaked file is recoverable, a deleted file another
    /// row plays is not.
    private static func pathsOwnedBySurvivors(
        of lookupPaths: [String],
        excluding transcripts: [Transcript],
        in context: NSManagedObjectContext
    ) -> Set<String> {
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        request.predicate = NSPredicate(
            format: "NOT (SELF IN %@) AND (audioFilePath IN %@ OR trimmedAudioFilePath IN %@)",
            transcripts as NSArray,
            lookupPaths as NSArray,
            lookupPaths as NSArray
        )

        do {
            let survivors = try context.fetch(request)
            return TranscriptAudioFiles.protectedPaths(for: survivors.map(Self.recordedAudioPaths(of:)))
        } catch {
            AppLogger.coreData.error("Could not check owners of derived audio files; keeping them: \(error.localizedDescription, privacy: .public)")
            return Set(lookupPaths)
        }
    }
}
