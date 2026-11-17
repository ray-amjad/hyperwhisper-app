//
//  PersistenceController+Migrations.swift
//  hyperwhisper
//
//  Launch data repairs and catalog migrations that need no private state.
//  Split out of PersistenceController.swift (#697); every body moved unchanged.
//

import CoreData
import Foundation
import SQLite3

extension PersistenceController {

    /// Deletes the per-request Local API Modes that builds before issue #1509
    /// committed to the store. Returns how many it deleted.
    ///
    /// `/transcribe` and `/post-process` used to build their per-request Mode
    /// in the `viewContext`, and any other save of that context during a
    /// request made it a real row: listed in Select Mode and Transcribe File,
    /// kept across relaunch, and doing nothing when picked. The fuzz that found
    /// it saw 12 of them. New requests can no longer leak one
    /// (`LocalAPITransientMode`), so this pass only clears what is already on
    /// disk, and once a store is clean it deletes nothing.
    ///
    /// A row is deleted only when `LocalAPITransientModeMarker.isLeakedRow`
    /// holds: one of the two endpoint names (bare, or with the " N" suffix
    /// `repairModeNames()` adds), `sortOrder` exactly `Int16.max`, and
    /// neither default nor seeded. Runs every launch and saves only when it
    /// deleted something.
    @discardableResult
    func purgeLeakedLocalAPITransientModes() -> Int {
        let context = container.viewContext
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()
        request.predicate = NSPredicate(
            format: "sortOrder == %@",
            NSNumber(value: LocalAPITransientModeMarker.sortOrder)
        )
        do {
            let leaked = try context.fetch(request).filter { mode in
                LocalAPITransientModeMarker.isLeakedRow(
                    name: mode.name,
                    sortOrder: mode.sortOrder,
                    isDefault: mode.isDefault,
                    isSystemProvided: mode.isSystemProvided
                )
            }
            guard !leaked.isEmpty else { return 0 }
            for mode in leaked {
                context.delete(mode)
            }
            try context.save()
            return leaked.count
        } catch {
            // Leave nothing pending on the shared context: a failed save keeps
            // its changes, and every later save would fail on them. This runs
            // during init, before anything else has written to the context.
            // The shape is read first, while the pending deletes still show.
            let shape = CoreDataSaveDiagnostics.contextShape(context)
            context.rollback()
            let nsError = error as NSError
            AppLogger.logCoreData(
                .save(site: "purge_leaked_local_api_transient_modes", contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: nsError,
                metadata: shape
            )
            return 0
        }
    }

    // MARK: - Google Chirp 3 → Gemini 3.5 Transcribe Tier Migration

    /// Every persisted `cloudAccuracyTier` value that catalog v8 retired, mapped
    /// onto `geminiTranscribe`. Mirrors the entry's `migrateFrom` list in
    /// `shared-app-classification/cloud-stt-catalog.json` and the SQL in the
    /// Windows/Linux migration `20260827090000_MigrateGoogleChirp3TierToGeminiTranscribe`.
    private static let retiredGoogleChirp3TierValues: Set<String> = [
        "googlechirp3", "googlechirp", "google-chirp", "chirp", "chirp_3", "googlespeech",
    ]

    /// What the Chirp 3 → Gemini 3.5 Transcribe migration writes back to one
    /// stored mode row. `nil` fields are left as they are.
    struct GoogleChirp3TierRewrite: Equatable {
        var accuracyTier: String?
        var transcriptionModel: String?
    }

    /// The rewrite a single stored row earns, or `nil` for "leave this row
    /// alone". Pure, so the provider guard below is testable without standing up
    /// a Core Data stack.
    ///
    /// SCOPED TO HYPERWHISPER CLOUD MODES, mirroring the `WHERE CloudProvider =`
    /// clause of the `20260508120000_MigrateRemovedDeepgramModels` precedent.
    /// `cloudAccuracyTier` is only meaningful for HW-Cloud modes, but a mode
    /// switched from HW-Cloud+Chirp3 to a BYOK provider KEEPS the stale tier —
    /// and several BYOK providers store an empty `cloudTranscriptionModel` (Grok's
    /// single model is catalogued under the id `""`). Without the guard, such a
    /// row matches on the stale tier, trips the `storedModel.isEmpty` branch and
    /// has Google's `gemini-3.5-transcribe` stamped onto it — which the next
    /// dictation then POSTs to xAI.
    ///
    /// A nil/empty provider counts as HyperWhisper Cloud: the shared core's
    /// blank-provider rule (`CloudProvider.forMode`, #1709).
    static func googleChirp3TierRewrite(
        cloudProvider: String?,
        cloudAccuracyTier: String?,
        cloudTranscriptionModel: String?
    ) -> GoogleChirp3TierRewrite? {
        let storedTier = (cloudAccuracyTier ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        guard retiredGoogleChirp3TierValues.contains(storedTier) else { return nil }

        let resolvedProvider = CloudProvider.forMode(cloudProvider)
        guard resolvedProvider == .hyperwhisper else { return nil }

        var rewrite = GoogleChirp3TierRewrite(
            accuracyTier: CloudAccuracyTier.geminiTranscribe.rawValue,
            transcriptionModel: nil
        )

        // `chirp_3` is not a model of the new tier, so the editor would
        // fall back to the tier default on next open anyway — write it
        // now so the stored row is honest and the first transcription
        // sends a valid X-STT-Model. A model the user picked that is NOT
        // Chirp's is left alone; it cannot exist on this tier today, but
        // silently overwriting a deliberate choice is the worse failure.
        let storedModel = (cloudTranscriptionModel ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if storedModel.isEmpty || storedModel.caseInsensitiveCompare("chirp_3") == .orderedSame {
            rewrite.transcriptionModel = CloudAccuracyTier.geminiTranscribe.defaultModelId
        }
        return rewrite
    }

    // MARK: - Broken Local Post-Processing Mode Repair

    /// Outcome of a `repairBrokenLocalModes` pass.
    struct LocalModeRepairResult {
        /// Names of modes whose post-processing was silently turned off.
        var disabledModeNames: [String] = []
        /// Model ids referenced by modes that were LEFT as `.local` but whose
        /// weights aren't downloaded yet (capable hardware, restore flow only) —
        /// candidates for a batched "Download all" prompt.
        var pendingDownloadModelIds: Set<String> = []
    }

    /// Silently repairs broken on-device (`.local`) post-processing modes by turning
    /// post-processing OFF (`postProcessingMode = 0`). We NEVER substitute a cloud
    /// LLM — on failure the user's text is delivered raw or post-processing is off,
    /// never silently routed to the cloud.
    ///
    /// A `.local` mode is broken when the machine can't run the local runtime
    /// (Intel), it has no model selected, or its selected model isn't usable. On
    /// capable hardware running under Rosetta the mode is LEFT intact (a native
    /// relaunch fixes it — see `SystemCapability.needsNativeRelaunch`).
    ///
    /// - Parameters:
    ///   - capability: hardware/runtime capability — drives the Intel gate.
    ///   - isCataloged: true if a model id is a known downloadable local model.
    ///   - isDownloaded: true if a model id's weights are present and usable on disk.
    ///   - keepPendingDownloads: when true (restore on capable hardware), a `.local`
    ///     mode whose model is cataloged-but-not-downloaded is LEFT as `.local` and
    ///     reported in `pendingDownloadModelIds` instead of being turned off. When
    ///     false (launch), such a mode is turned off like any other missing model.
    @discardableResult
    func repairBrokenLocalModes(
        capability: SystemCapability,
        isCataloged: (String) -> Bool,
        isDownloaded: (String) -> Bool,
        keepPendingDownloads: Bool
    ) -> LocalModeRepairResult {
        var result = LocalModeRepairResult()
        let context = container.viewContext
        let request: NSFetchRequest<Mode> = Mode.fetchRequest()

        let localRawValue = PostProcessingMode.local.rawValue
        let offRawValue = PostProcessingMode.off.rawValue

        func disable(_ mode: Mode, reason: String) {
            let name = mode.name ?? "Untitled"
            mode.postProcessingMode = offRawValue
            result.disabledModeNames.append(name)
            // Greppable support-visibility line (there is no UI signal for a silent repair).
            AppLogger.coreData.info("PP disabled for Mode · modeId=\(mode.id?.uuidString ?? "nil", privacy: .public) · preset=\(PresetType.reportingValue(for: mode), privacy: .public): \(reason, privacy: .public)")
        }

        do {
            let modes = try context.fetch(request)
            for mode in modes where mode.postProcessingMode == localRawValue {
                // Hardware gate first — on Intel never keep/offer a local mode.
                guard capability.isAppleSiliconHardware else {
                    disable(mode, reason: "unsupported hardware (Intel)")
                    continue
                }
                let modelId = (mode.languageModel ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                guard !modelId.isEmpty else {
                    disable(mode, reason: "no local model selected")
                    continue
                }
                if isDownloaded(modelId) {
                    continue // healthy — model present and usable
                }
                // Not downloaded. On restore (capable HW) keep the intent for a
                // cataloged model and offer a re-download; otherwise turn it off.
                if keepPendingDownloads, isCataloged(modelId) {
                    result.pendingDownloadModelIds.insert(modelId)
                    continue
                }
                disable(mode, reason: isCataloged(modelId) ? "model not downloaded" : "model not available")
            }

            if context.hasChanges {
                try context.save()
            }
        } catch {
            let nsError = error as NSError
            // One event, not two: the sibling SentryService.capture that used to
            // sit here was ungated, stack-trace fingerprinted, and carried the
            // recent_logs blob. `site` now says which caller failed.
            AppLogger.logCoreData(
                .save(site: "repair_broken_local_modes", contextKey: CoreDataSaveDiagnostics.viewContextKey),
                error: nsError,
                metadata: CoreDataSaveDiagnostics.contextShape(context)
            )
        }

        return result
    }

    /// Filesystem-backed `isDownloaded` check for the launch/restore repair: a model
    /// is "present" when its GGUF file exists on disk. (Model id == GGUF filename.)
    /// Checksum validity isn't known synchronously at launch — an invalid file that
    /// exists is caught later by the mode editor and never causes data loss because
    /// the streaming fallback always returns the raw transcript.
    static func localModelFileExists(_ modelId: String) -> Bool {
        let url = LocalModelManager.modelsDirectory.appendingPathComponent(modelId)
        return FileManager.default.fileExists(atPath: url.path)
    }

    // MARK: - One-time sweep of orphaned VAD upload copies (#1524)

    /// UserDefaults flag for the one-time launch sweep of `*_trimmed.m4a` VAD
    /// upload copies that no transcript references. Set only after a run that
    /// listed the folder, read every row's paths and removed every orphan.
    static let orphanedTrimmedUploadCopySweepDefaultsKey = "didSweepOrphanedTrimmedUploadCopiesV1"

    /// Only files last modified at least this long ago are swept (Ray, #1524):
    /// a younger file can belong to a job whose row is not written yet.
    static let orphanedTrimmedUploadCopyMinimumAge: TimeInterval = 24 * 60 * 60

    /// Starts the one-time sweep on a background task at launch. Returns at
    /// once; never blocks launch.
    ///
    /// Builds before #1520 deleted a row's trimmed WAV but not its `.m4a`
    /// upload copy, so those copies stay on disk with no row (the #1513 fuzz
    /// found 11, 159 MB). The sweep looks only in the configured recordings
    /// folder, and only once the user has answered the Documents access
    /// question: listing `~/Documents` before that would raise the system
    /// prompt the app holds back until it has explained it. Until then the
    /// flag stays unset and a later launch runs the sweep.
    func scheduleOrphanedTrimmedUploadCopySweepOnLaunch(defaults: UserDefaults = .standard) {
        guard !defaults.bool(forKey: Self.orphanedTrimmedUploadCopySweepDefaultsKey) else { return }
        guard let folder = Self.recordingsFolderForLaunchSweep(defaults: defaults) else { return }
        Task.detached(priority: .utility) { [self] in
            await self.sweepOrphanedTrimmedUploadCopiesIfNeeded(recordingsFolder: folder, defaults: defaults)
        }
    }

    /// The recordings folder `StorageSettingsManager.recordingsFolder` holds,
    /// read straight from its `@AppStorage` key, or nil while that folder sits
    /// under Documents and the access question is unanswered.
    static func recordingsFolderForLaunchSweep(defaults: UserDefaults = .standard) -> URL? {
        let configured = defaults.string(forKey: "recordingsFolder")
        let folder: URL
        if let configured, !configured.isEmpty {
            folder = URL(fileURLWithPath: configured, isDirectory: true)
        } else if let fallback = StorageSettingsManager.defaultRecordingsFolderURL {
            folder = fallback
        } else {
            return nil
        }

        if let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first,
           folder.path.hasPrefix(documents.path),
           !defaults.bool(forKey: "documentsPermissionExplained"),
           !defaults.bool(forKey: "documentsAccessDenied") {
            return nil
        }
        return folder
    }

    /// Deletes the `*_trimmed.m4a` files directly in `recordingsFolder` that
    /// no transcript references and that are at least 24 h old, then sets the
    /// one-time flag. Returns the files it deleted, or nil when it did not run
    /// (already ran, or the folder or the rows could not be read; the flag
    /// then stays unset so a later launch tries again).
    ///
    /// Never deletes outside `recordingsFolder`, nor a file whose name any
    /// row's original, trimmed or derived path carries
    /// (`TranscriptAudioFiles.orphanedUploadCopies`). A file's date is read
    /// again just before its delete, so one a run rewrote meanwhile is kept.
    @discardableResult
    func sweepOrphanedTrimmedUploadCopiesIfNeeded(
        recordingsFolder: URL,
        now: Date = Date(),
        defaults: UserDefaults = .standard
    ) async -> [URL]? {
        let flag = Self.orphanedTrimmedUploadCopySweepDefaultsKey
        guard !defaults.bool(forKey: flag) else { return nil }

        guard let entries = await Self.recordingsFolderEntries(recordingsFolder) else {
            AppLogger.coreData.info("Orphaned upload copy sweep skipped: the recordings folder could not be listed")
            return nil
        }
        let minimumAge = Self.orphanedTrimmedUploadCopyMinimumAge
        let candidates = TranscriptAudioFiles.orphanedUploadCopies(
            in: entries,
            referencedPaths: [],
            now: now,
            minimumAge: minimumAge
        )
        guard !candidates.isEmpty else {
            defaults.set(true, forKey: flag)
            return []
        }

        guard let referenced = await referencedAudioPathsForSweep() else {
            AppLogger.coreData.error("Orphaned upload copy sweep skipped: could not read the transcripts' audio paths")
            return nil
        }
        let orphans = TranscriptAudioFiles.orphanedUploadCopies(
            in: entries,
            referencedPaths: referenced,
            now: now,
            minimumAge: minimumAge
        )

        let outcome = await Self.removeOrphanedUploadCopies(
            orphans,
            in: recordingsFolder,
            olderThan: now.addingTimeInterval(-minimumAge)
        )
        if outcome.allRemoved {
            defaults.set(true, forKey: flag)
        }
        AppLogger.coreData.info("Orphaned upload copy sweep removed \(outcome.deleted.count, privacy: .public) file(s)")
        return outcome.deleted
    }

    /// Every path a Transcript row records or derives, plus every
    /// RecordingSession path, read on the writer. Nil if a fetch failed.
    private func referencedAudioPathsForSweep() async -> Set<String>? {
        await performWrite { context -> Set<String>? in
            let transcripts = NSFetchRequest<NSDictionary>(entityName: "Transcript")
            transcripts.resultType = .dictionaryResultType
            transcripts.propertiesToFetch = ["audioFilePath", "trimmedAudioFilePath"]
            let sessions = NSFetchRequest<NSDictionary>(entityName: "RecordingSession")
            sessions.resultType = .dictionaryResultType
            sessions.propertiesToFetch = ["audioFilePath"]
            do {
                let rows = try context.fetch(transcripts).map { row in
                    TranscriptRecordedAudioPaths(
                        audioFilePath: row["audioFilePath"] as? String,
                        trimmedAudioFilePath: row["trimmedAudioFilePath"] as? String
                    )
                }
                var paths = TranscriptAudioFiles.protectedPaths(for: rows)
                for row in try context.fetch(sessions) {
                    if let path = row["audioFilePath"] as? String, !path.isEmpty {
                        paths.insert(path)
                    }
                }
                return paths
            } catch {
                return nil
            }
        }
    }

    /// The regular files directly in `folder` with their modification dates,
    /// listed off the calling thread. Nil if the folder cannot be listed.
    private static func recordingsFolderEntries(_ folder: URL) async -> [TranscriptAudioFiles.FolderEntry]? {
        await Task.detached(priority: .utility) { () -> [TranscriptAudioFiles.FolderEntry]? in
            let keys: [URLResourceKey] = [.isRegularFileKey, .contentModificationDateKey]
            guard let urls = try? FileManager.default.contentsOfDirectory(
                at: folder,
                includingPropertiesForKeys: keys,
                options: []
            ) else {
                return nil
            }
            return urls.compactMap { url in
                guard let values = try? url.resourceValues(forKeys: Set(keys)),
                      values.isRegularFile == true,
                      let modified = values.contentModificationDate else {
                    return nil
                }
                return TranscriptAudioFiles.FolderEntry(url: url, modified: modified)
            }
        }.value
    }

    /// Removes `orphans`, each only if it sits directly in `folder` and its
    /// modification date, read again now, is still before `cutoff`.
    private static func removeOrphanedUploadCopies(
        _ orphans: [URL],
        in folder: URL,
        olderThan cutoff: Date
    ) async -> (deleted: [URL], allRemoved: Bool) {
        await Task.detached(priority: .utility) { () -> (deleted: [URL], allRemoved: Bool) in
            let fileManager = FileManager.default
            let folderPath = folder.resolvingSymlinksInPath().path
            var deleted: [URL] = []
            var allRemoved = true
            for url in orphans {
                guard url.deletingLastPathComponent().resolvingSymlinksInPath().path == folderPath,
                      let modified = (try? fileManager.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date,
                      modified <= cutoff else {
                    continue
                }
                do {
                    try fileManager.removeItem(at: url)
                    deleted.append(url)
                } catch {
                    if fileManager.fileExists(atPath: url.path) { allRemoved = false }
                }
            }
            return (deleted, allRemoved)
        }.value
    }
}
