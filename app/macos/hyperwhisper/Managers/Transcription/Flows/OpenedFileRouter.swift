//
//  OpenedFileRouter.swift
//  hyperwhisper
//
//  Issue #656: the second, scriptable entry point into file import. Finder
//  "Open With" and `open -a HyperWhisper <file>` deliver files to
//  `AppDelegate.application(_:open:)`, which hands them to this router.
//

import Foundation
import UniformTypeIdentifiers

/// Queues files the app was asked to open and transcribes them, one at a time,
/// through `FileTranscriptionFlow` with the currently selected Mode.
///
/// **Why a queue:** on a cold `open -a` launch AppKit delivers the files before
/// `HyperWhisperApp.bootstrapAppServices()` has wired the transcription pipeline
/// (bootstrap waits for `applicationDidFinishLaunching`, and the selected Mode is
/// restored a main-actor turn after that). Files that arrive early wait here until
/// bootstrap calls `configure(processor:)`, then run in the order they were opened.
///
/// **Why one at a time:** every import drives the one shared
/// `FileTranscriptionPopupManager`, whose `show()` first dismisses whatever is on
/// screen, so two concurrent imports would tear down each other's popup and leave
/// one invisible and uncancellable (the same hazard as two pickers, see
/// `FileTranscriptionFlow.isFilePickerOpen`). The processor returns only when its
/// import has ended, and the next file starts after that.
///
/// MainActor-isolated, so no locking is needed.
@MainActor
final class OpenedFileRouter {

    /// The app's router. `AppDelegate` feeds it; `HyperWhisperApp` configures it.
    static let shared = OpenedFileRouter()

    /// Imports one file and returns when that import has finished, failed or been
    /// cancelled.
    typealias Processor = @MainActor (URL) async -> Void

    /// Accepted files not yet started, in the order they were opened.
    private(set) var pendingURLs: [URL] = []

    /// Set once by `configure(processor:)`. Until then files only queue.
    private var processor: Processor?

    /// True while a drain loop is running, so a second `open` joins it instead
    /// of starting a concurrent import.
    private(set) var isDraining = false

    /// The running drain loop. Exposed so tests can await it.
    private(set) var drainTask: Task<Void, Never>?

    init() {}

    /// Whether bootstrap has configured the router.
    var isReady: Bool { processor != nil }

    // MARK: - Intake

    /// Queues the supported files among `urls` and starts them if the router is ready.
    ///
    /// Anything that is not a local file of a type the Transcribe File picker
    /// accepts — a folder, a text file passed with `open -a` — is logged and
    /// dropped. LaunchServices only offers the app for the declared document types,
    /// but `open -a` passes any file.
    ///
    /// - Returns: The URLs that were accepted, in order.
    @discardableResult
    func open(_ urls: [URL]) -> [URL] {
        let accepted = urls.filter { url in
            if Self.isSupported(url) { return true }
            // lastPathComponent only, never the path (privacy).
            AppLogger.transcription.info("📂 Ignoring opened item that is not a supported audio/video file: \(url.lastPathComponent)")
            return false
        }
        guard !accepted.isEmpty else { return [] }

        pendingURLs.append(contentsOf: accepted)
        let queued = pendingURLs.count
        let ready = isReady
        AppLogger.transcription.info("📂 Queued \(accepted.count, privacy: .public) opened file(s) · pending=\(queued, privacy: .public) · ready=\(ready, privacy: .public)")
        drainIfReady()
        return accepted
    }

    /// Supplies the import step and starts any files that arrived before it.
    ///
    /// Called once, from `HyperWhisperApp.bootstrapAppServices()`. A second call is
    /// ignored: the first processor already owns the queue.
    func configure(processor: @escaping Processor) {
        guard self.processor == nil else {
            AppLogger.transcription.debug("📂 OpenedFileRouter already configured — ignoring")
            return
        }
        self.processor = processor
        drainIfReady()
    }

    // MARK: - Drain

    private func drainIfReady() {
        guard let processor, !isDraining, !pendingURLs.isEmpty else { return }
        isDraining = true
        // Inherits the main actor. The check of `pendingURLs` and the reset of
        // `isDraining` below sit in one synchronous stretch, so a file opened
        // while an import runs is always picked up by this loop or starts a new one.
        drainTask = Task {
            while !self.pendingURLs.isEmpty {
                let url = self.pendingURLs.removeFirst()
                await processor(url)
            }
            self.isDraining = false
        }
    }

    // MARK: - Type filter

    /// Whether `url` is a local file of a type `FileTranscriptionFlow.supportedAudioTypes`
    /// accepts — the same list the Transcribe File picker uses.
    static func isSupported(_ url: URL) -> Bool {
        guard url.isFileURL, let type = contentType(of: url) else { return false }
        return isSupported(contentType: type)
    }

    /// Whether `contentType` conforms to one of the picker's types.
    static func isSupported(contentType: UTType) -> Bool {
        FileTranscriptionFlow.supportedAudioTypes.contains { contentType.conforms(to: $0) }
    }

    /// The file's type as the system reports it, or from its extension when the
    /// file cannot be read (it may have gone, and the import then reports that).
    static func contentType(of url: URL) -> UTType? {
        if let type = try? url.resourceValues(forKeys: [.contentTypeKey]).contentType {
            return type
        }
        let ext = url.pathExtension
        guard !ext.isEmpty else { return nil }
        return UTType(filenameExtension: ext)
    }

    // MARK: - The app's processor

    /// The import step the app configures: resolve the selected Mode, then run the
    /// file through `FileTranscriptionFlow.transcribeOpenedFile(_:mode:)`.
    ///
    /// The Mode is resolved when the file STARTS, not when it was opened: on a cold
    /// launch the selection is only restored after bootstrap, and a later file in a
    /// queue follows a Mode the user picks meanwhile — as dictation does.
    static func makeFileImportProcessor(
        transcriptionPipeline: TranscriptionPipeline,
        settingsManager: SettingsManager,
        appState: AppState,
        licenseManager: LicenseManager
    ) -> Processor {
        return { [weak transcriptionPipeline, weak settingsManager, weak appState, weak licenseManager] url in
            guard let appState else {
                AppLogger.transcription.error("❌ AppState unavailable — cannot import opened file")
                return
            }
            // The same selection dictation records with (RecordingTranscriptionFlow
            // reads appState.selectedModeId), fetched off the main thread as
            // MainAppView.transcribeFile(with:) does.
            let modeId = appState.selectedModeId
            guard let mode = await PersistenceController.shared.fetchModeInBackground(withId: modeId) else {
                // The Mode's id, never its name (issue #795).
                AppLogger.coreData.error("Unable to resolve the selected mode for an opened file: id=\(modeId, privacy: .public)")
                return
            }

            // A local, as the menu path keeps it: the awaited call below and the
            // flow's own Task keep it alive until the import ends.
            let flow = FileTranscriptionFlow(
                transcriptionPipeline: transcriptionPipeline,
                settingsManager: settingsManager,
                appState: appState,
                licenseManager: licenseManager,
                onOpenMainWindow: {
                    // No SwiftUI view to call openWindow(id:) from here. The flow has
                    // already tried MainWindowStore and the identifier lookup, so
                    // there is no main window: take the Dock-click path, which
                    // SwiftUI's WindowGroup answers by opening one.
                    SingleInstanceGuard.sendReopenToSelf()
                }
            )
            await flow.transcribeOpenedFile(url, mode: mode)
        }
    }
}
