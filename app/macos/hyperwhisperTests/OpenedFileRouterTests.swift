//
//  OpenedFileRouterTests.swift
//  hyperwhisperTests
//
//  Issue #656: files opened with Finder "Open With" or `open -a HyperWhisper
//  <file>` go through OpenedFileRouter into FileTranscriptionFlow. These cover
//  the parts that need no pipeline: which files are accepted, that files opened
//  before bootstrap wait and then run in order, one at a time, and that the
//  Info.plist document types match the picker's types.
//

import Foundation
import Testing
import UniformTypeIdentifiers
@testable import HyperWhisper

@MainActor
@Suite("Files opened with Open With / open -a")
struct OpenedFileRouterTests {

    /// A path that need not exist: the type then comes from the extension.
    private func file(_ name: String) -> URL {
        URL(fileURLWithPath: "/tmp/hyperwhisper-656-\(UUID().uuidString)/\(name)")
    }

    // MARK: - Type filter

    @Test(arguments: ["sample.wav", "sample.mp3", "sample.m4a", "sample.aiff",
                      "sample.flac", "sample.ogg", "clip.mp4", "clip.mov",
                      "UPPER.WAV"])
    func acceptsWhatThePickerAccepts(name: String) {
        #expect(OpenedFileRouter.isSupported(file(name)))
    }

    @Test(arguments: ["notes.txt", "paper.pdf", "image.png", "no-extension"])
    func rejectsOtherFiles(name: String) {
        #expect(!OpenedFileRouter.isSupported(file(name)))
    }

    @Test func rejectsANonFileURL() throws {
        let url = try #require(URL(string: "https://example.com/sample.wav"))
        #expect(!OpenedFileRouter.isSupported(url))
    }

    @Test func readsTheTypeOfARealFileAndRejectsAFolder() throws {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("open-656-\(UUID().uuidString).wav", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }

        // A folder named like audio is still a folder.
        #expect(!OpenedFileRouter.isSupported(folder))

        let wav = folder.appendingPathComponent("real.wav")
        try Data(repeating: 0, count: 64).write(to: wav)
        #expect(OpenedFileRouter.isSupported(wav))
    }

    // MARK: - Queue

    @Test func filesOpenedBeforeBootstrapWaitThenRunInOrder() async {
        let router = OpenedFileRouter()
        let first = file("first.wav")
        let ignored = file("readme.txt")
        let second = file("second.m4a")

        let accepted = router.open([first, ignored, second])
        #expect(accepted == [first, second])
        #expect(router.pendingURLs == [first, second])
        #expect(!router.isReady)
        #expect(router.drainTask == nil)

        var processed: [URL] = []
        router.configure { url in processed.append(url) }
        await router.drainTask?.value

        #expect(processed == [first, second])
        #expect(router.pendingURLs.isEmpty)
        #expect(!router.isDraining)
    }

    @Test func filesRunOneAtATimeIncludingOnesOpenedMidImport() async {
        let router = OpenedFileRouter()
        let a = file("a.wav"), b = file("b.wav"), c = file("c.wav")
        var events: [String] = []

        router.configure { url in
            events.append("start \(url.lastPathComponent)")
            // Suspend, as a real import does, so an overlap would show.
            for _ in 0..<5 { await Task.yield() }
            events.append("end \(url.lastPathComponent)")
        }

        router.open([a, b])
        #expect(router.isDraining)
        // Opened while the first batch is queued or running: joins the same loop.
        router.open([c])
        await router.drainTask?.value

        #expect(events == [
            "start a.wav", "end a.wav",
            "start b.wav", "end b.wav",
            "start c.wav", "end c.wav",
        ])
        #expect(!router.isDraining)
    }

    @Test func aFileOpenedAfterTheQueueEmptiedStartsANewRun() async {
        let router = OpenedFileRouter()
        var processed: [URL] = []
        router.configure { url in processed.append(url) }

        let first = file("one.wav")
        router.open([first])
        await router.drainTask?.value

        let second = file("two.mp3")
        router.open([second])
        await router.drainTask?.value

        #expect(processed == [first, second])
    }

    @Test func aSecondConfigureIsIgnored() async {
        let router = OpenedFileRouter()
        var firstCalls = 0
        var secondCalls = 0
        router.configure { _ in firstCalls += 1 }
        router.configure { _ in secondCalls += 1 }

        router.open([file("x.wav")])
        await router.drainTask?.value

        #expect(firstCalls == 1)
        #expect(secondCalls == 0)
    }

    @Test func nothingSupportedQueuesNothing() {
        let router = OpenedFileRouter()
        #expect(router.open([file("a.txt"), file("b.pdf")]).isEmpty)
        #expect(router.pendingURLs.isEmpty)
    }

    // MARK: - Info.plist stays in sync with the picker

    /// The app's CFBundleDocumentTypes. The test host is the app, so this is the
    /// built Info.plist.
    private var documentTypes: [[String: Any]] {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleDocumentTypes") as? [[String: Any]] ?? []
    }

    @Test func everyPickerTypeIsDeclaredAsADocumentType() {
        let declared = Set(documentTypes.flatMap { $0["LSItemContentTypes"] as? [String] ?? [] })
        #expect(!declared.isEmpty, "Info.plist declares no CFBundleDocumentTypes")

        for type in FileTranscriptionFlow.supportedAudioTypes {
            // An extension the system has no type for maps to a dynamic `dyn.`
            // identifier, which cannot be declared; public.audio stands for it.
            if type.identifier.hasPrefix("dyn.") { continue }
            #expect(declared.contains(type.identifier), "\(type.identifier) is in supportedAudioTypes but not in Info.plist")
        }
    }

    @Test func documentTypesAreViewerAndNeverTheDefaultOwner() {
        #expect(!documentTypes.isEmpty)
        for entry in documentTypes {
            #expect(entry["CFBundleTypeRole"] as? String == "Viewer")
            #expect(entry["LSHandlerRank"] as? String == "Alternate")
        }
    }
}
