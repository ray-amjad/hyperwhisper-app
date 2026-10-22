//
//  CloudTempAudioSweepTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1581 (sibling of #1484).
//
//  A cloud request writes the user's audio into one of four temp files and
//  deletes it only in-process. A `kill -9` mid-upload left it in `$TMPDIR`
//  for good. These tests pin the sweep rule for each of the four names on an
//  injected directory with injected process facts.
//

import Darwin
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Cloud temp audio files do not outlive a dead process (#1581)")
struct CloudTempAudioSweepTests {

    private static let appPath = "app/macos/hyperwhisper/hyperwhisperApp.swift"

    private static let me: pid_t = 4_000
    private static let deadPID: pid_t = 4_001
    private static let livePID: pid_t = 4_002

    private static func alive(_ pid: pid_t) -> Bool { pid == me || pid == livePID }

    // MARK: - Fixtures

    private static func makeSandbox() throws -> URL {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("CloudTempAudioSweepTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    @discardableResult
    private static func makeFile(_ name: String, in dir: URL, modified: Date = Date()) throws -> URL {
        let file = dir.appendingPathComponent(name)
        try Data([0x52, 0x49, 0x46, 0x46]).write(to: file)
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: file.path)
        return file
    }

    private static func exists(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.path)
    }

    @discardableResult
    private static func sweep(
        _ dir: URL,
        now: Date = Date(),
        processStartTime: (pid_t) -> Date? = { _ in nil },
        anotherCopyIsRunning: () -> Bool = { true }
    ) -> [URL] {
        CloudTempAudioSweep.sweep(
            in: dir,
            now: now,
            currentPID: me,
            isProcessAlive: alive,
            processStartTime: processStartTime,
            anotherCopyIsRunning: anotherCopyIsRunning
        )
    }

    // MARK: - Naming

    @Test(arguments: CloudTempAudioSweep.Kind.allCases)
    func aNewNameCarriesThePidAndParsesBack(kind: CloudTempAudioSweep.Kind) {
        let id = UUID()
        let name = CloudTempAudioSweep.fileName(kind, pid: 1234, id: id)
        #expect(name == "\(kind.prefix)1234-\(id.uuidString)\(kind.suffix)")
        #expect(CloudTempAudioSweep.owner(ofEntryNamed: name) == .process(1234))
        #expect(CloudTempAudioSweep.owner(ofEntryNamed: CloudTempAudioSweep.fileName(kind)) == .process(getpid()))
    }

    @Test func theFourNamesAreTheIssuesFour() {
        let uuid = UUID()
        let id = uuid.uuidString
        #expect(CloudTempAudioSweep.fileName(.multipartBody, pid: 7, id: uuid) == "hw-multipart-7-\(id).tmp")
        #expect(CloudTempAudioSweep.fileName(.jsonBase64Body, pid: 7, id: uuid) == "hw-jsonb64-7-\(id).tmp")
        #expect(CloudTempAudioSweep.fileName(.reencodedWAV, pid: 7, id: uuid) == "hw-reencode-7-\(id).wav")
        #expect(CloudTempAudioSweep.fileName(.dictationWAV, pid: 7, id: uuid) == "hw-dictation-7-\(id).wav")
    }

    @Test func preFixNamesAreLegacyExceptTheGenericDictationOne() {
        let id = UUID().uuidString
        #expect(CloudTempAudioSweep.owner(ofEntryNamed: "hw-multipart-\(id).tmp") == .legacy)
        #expect(CloudTempAudioSweep.owner(ofEntryNamed: "hw-jsonb64-\(id).tmp") == .legacy)
        #expect(CloudTempAudioSweep.owner(ofEntryNamed: "hw-reencode-\(id).wav") == .legacy)
        // `$TMPDIR` is shared with other apps; `dictation-<UUID>.wav` is too
        // generic to claim, and no build ever wrote `hw-dictation-<UUID>.wav`.
        #expect(CloudTempAudioSweep.owner(ofEntryNamed: "dictation-\(id).wav") == .notOurs)
        #expect(CloudTempAudioSweep.owner(ofEntryNamed: "hw-dictation-\(id).wav") == .notOurs)
    }

    @Test func anyOtherNameIsNotOurs() {
        let uuid = UUID().uuidString
        let names = [
            "",
            "hw-multipart-",
            "hw-multipart-.tmp",
            "hw-multipart-12-\(uuid)",
            "hw-multipart-12-\(uuid).wav",
            "hw-multipart-12-\(uuid).tmp.bak",
            "hw-multipart-tests-\(uuid)",
            "hw-reencode-missing-\(uuid).wav",
            "hw-reencode-test.wav",
            "hw-reencode-0-\(uuid).wav",
            "hw-reencode--12-\(uuid).wav",
            "hw-jsonb64-99999999999-\(uuid).tmp",
            "hw-jsonb64-12-notauuid.tmp",
            "HW-multipart-12-\(uuid).tmp",
            "x-hw-multipart-12-\(uuid).tmp",
            "dictation-12-\(uuid).wav",
            "hyperwhisper-local-api-12-\(uuid)",
        ]
        for name in names {
            #expect(CloudTempAudioSweep.owner(ofEntryNamed: name) == .notOurs, "\(name)")
        }
    }

    // MARK: - The sweep rule, per kind

    @Test(arguments: CloudTempAudioSweep.Kind.allCases)
    func aFileOfADeadProcessIsRemoved(kind: CloudTempAudioSweep.Kind) throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let stale = try Self.makeFile(CloudTempAudioSweep.fileName(kind, pid: Self.deadPID), in: dir)

        let removed = Self.sweep(dir)

        #expect(!Self.exists(stale))
        #expect(removed.map(\.lastPathComponent) == [stale.lastPathComponent])
    }

    @Test(arguments: CloudTempAudioSweep.Kind.allCases)
    func aFreshFileOfAnotherLiveCopyIsKept(kind: CloudTempAudioSweep.Kind) throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let live = try Self.makeFile(CloudTempAudioSweep.fileName(kind, pid: Self.livePID), in: dir)

        #expect(Self.sweep(dir).isEmpty)
        #expect(Self.exists(live))
    }

    /// The sweep runs while this launch may already be uploading.
    @Test(arguments: CloudTempAudioSweep.Kind.allCases)
    func thisProcessesOwnFileIsKept(kind: CloudTempAudioSweep.Kind) throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let mine = try Self.makeFile(
            CloudTempAudioSweep.fileName(kind, pid: Self.me),
            in: dir,
            modified: Date(timeIntervalSinceNow: -3 * LocalAPIStagingSweep.liveOwnerMaxAge)
        )

        let removed = CloudTempAudioSweep.sweep(
            in: dir,
            currentPID: Self.me,
            isProcessAlive: { _ in false },
            processStartTime: { _ in .distantFuture },
            anotherCopyIsRunning: { false }
        )

        #expect(removed.isEmpty)
        #expect(Self.exists(mine))
    }

    /// The pid is alive but its process started after the file was written:
    /// the owner died and the pid was reused.
    @Test(arguments: CloudTempAudioSweep.Kind.allCases)
    func aFileWhosePidWasReusedIsRemoved(kind: CloudTempAudioSweep.Kind) throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let written = now.addingTimeInterval(-2 * 60 * 60)
        let orphan = try Self.makeFile(CloudTempAudioSweep.fileName(kind, pid: Self.livePID), in: dir, modified: written)

        Self.sweep(dir, now: now, processStartTime: { pid in
            pid == Self.livePID ? written.addingTimeInterval(60 * 60) : nil
        })

        #expect(!Self.exists(orphan))
    }

    @Test(arguments: CloudTempAudioSweep.Kind.allCases)
    func aLivePidOnADayOldFileIsTreatedAsReused(kind: CloudTempAudioSweep.Kind) throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let old = try Self.makeFile(
            CloudTempAudioSweep.fileName(kind, pid: Self.livePID),
            in: dir,
            modified: now.addingTimeInterval(-LocalAPIStagingSweep.liveOwnerMaxAge - 60)
        )

        Self.sweep(dir, now: now)

        #expect(!Self.exists(old))
    }

    @Test func preFixFilesGoAtOnceWhenNoOtherCopyRunsElseAfter24Hours() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let now = Date()
        let id = { UUID().uuidString }
        let names = ["hw-multipart-\(id()).tmp", "hw-jsonb64-\(id()).tmp", "hw-reencode-\(id()).wav"]

        let young = try names.map { try Self.makeFile($0, in: dir, modified: now.addingTimeInterval(-60 * 60)) }
        Self.sweep(dir, now: now, anotherCopyIsRunning: { true })
        for file in young { #expect(Self.exists(file), "\(file.lastPathComponent)") }

        Self.sweep(dir, now: now, anotherCopyIsRunning: { false })
        for file in young { #expect(!Self.exists(file), "\(file.lastPathComponent)") }

        let dayOld = try names.map {
            try Self.makeFile($0, in: dir, modified: now.addingTimeInterval(-LocalAPIStagingSweep.liveOwnerMaxAge - 60))
        }
        Self.sweep(dir, now: now, anotherCopyIsRunning: { true })
        for file in dayOld { #expect(!Self.exists(file), "\(file.lastPathComponent)") }
    }

    @Test func entriesThatAreNotOurFilesAreUntouched() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let fm = FileManager.default
        let old = Date(timeIntervalSinceNow: -2 * LocalAPIStagingSweep.liveOwnerMaxAge)

        // Another app's generic name, and our own test fixtures' names.
        let others = try [
            "dictation-\(UUID().uuidString).wav",
            "hw-multipart-tests-\(UUID().uuidString)",
            "hw-reencode-test.wav",
        ].map { try Self.makeFile($0, in: dir, modified: old) }

        // A FOLDER with a dead-pid name.
        let folder = dir.appendingPathComponent(CloudTempAudioSweep.fileName(.multipartBody, pid: Self.deadPID))
        try fm.createDirectory(at: folder, withIntermediateDirectories: false)

        // A SYMLINK with a dead-pid name, pointing outside the swept directory.
        let outside = try Self.makeSandbox()
        defer { try? fm.removeItem(at: outside) }
        let target = try Self.makeFile("precious.wav", in: outside)
        let link = dir.appendingPathComponent(CloudTempAudioSweep.fileName(.reencodedWAV, pid: Self.deadPID))
        try fm.createSymbolicLink(at: link, withDestinationURL: target)

        let removed = Self.sweep(dir, anotherCopyIsRunning: { false })

        #expect(removed.isEmpty)
        for file in others { #expect(Self.exists(file), "\(file.lastPathComponent)") }
        #expect(Self.exists(folder))
        #expect((try? fm.destinationOfSymbolicLink(atPath: link.path)) != nil)
        #expect(Self.exists(target))
    }

    /// The two sweeps share one loop; neither claims the other's entries.
    @Test func theLocalAPISweepAndThisOneLeaveEachOthersEntries() throws {
        let dir = try Self.makeSandbox()
        defer { try? FileManager.default.removeItem(at: dir) }
        let file = try Self.makeFile(CloudTempAudioSweep.fileName(.multipartBody, pid: Self.deadPID), in: dir)
        let folder = dir.appendingPathComponent(LocalAPIStagingSweep.directoryName(pid: Self.deadPID), isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)

        LocalAPIStagingSweep.sweep(in: dir, currentPID: Self.me, isProcessAlive: Self.alive, processStartTime: { _ in nil }, anotherCopyIsRunning: { true })
        #expect(Self.exists(file))
        #expect(!Self.exists(folder))

        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        Self.sweep(dir)
        #expect(!Self.exists(file))
        #expect(Self.exists(folder))
    }

    // MARK: - Wiring

    @Test func theFourSitesNameTheirFilesThroughTheSweep() throws {
        let sites: [(String, String)] = [
            ("app/macos/hyperwhisper/Managers/Transcription/Support/RustHTTPExecutor.swift", "CloudTempAudioSweep.temporaryURL(.multipartBody)"),
            ("app/macos/hyperwhisper/Managers/Transcription/Support/RustHTTPExecutor.swift", "CloudTempAudioSweep.temporaryURL(.jsonBase64Body)"),
            ("app/macos/hyperwhisper/Managers/Transcription/Support/CloudAudioFormatRecovery.swift", "CloudTempAudioSweep.temporaryURL(.reencodedWAV)"),
            ("app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/AssemblyAIProvider.swift", "CloudTempAudioSweep.temporaryURL(.dictationWAV)"),
        ]
        for (path, call) in sites {
            let code = try ProductionSource.code(of: path)
            #expect(code.contains(call), "\(path) \(call)")
            for old in ["\"hw-multipart-\\(", "\"hw-jsonb64-\\(", "\"hw-reencode-\\(", "\"dictation-\\("] {
                #expect(!code.contains(old), "\(path) still writes \(old)")
            }
        }
    }

    @Test func launchRunsTheSweep() throws {
        let bootstrap = try ProductionSource.slice(
            of: Self.appPath,
            from: "private func bootstrapAppServices() {",
            to: "LocalAPIServer.shared.configure("
        )
        #expect(bootstrap.contains("CloudTempAudioSweep.sweep()"))
    }
}
