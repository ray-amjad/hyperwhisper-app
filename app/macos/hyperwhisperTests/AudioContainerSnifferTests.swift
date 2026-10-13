//
//  AudioContainerSnifferTests.swift
//  hyperwhisperTests
//
//  File import reads a file's format from its header, not its name (issue #1426):
//  a WAV named .mp3 or with no extension is copied under .wav so AVFoundation can
//  open it, and WebM is no longer offered by the open panel.
//

import AVFoundation
import Foundation
import Testing
import UniformTypeIdentifiers
@testable import HyperWhisper

@Suite("File import reads the format from the header")
struct AudioContainerSnifferTests {

    // MARK: - Helpers

    private func header(_ ascii: String, at offset: Int = 0, length: Int = 16) -> Data {
        var bytes = [UInt8](repeating: 0, count: length)
        for (i, byte) in ascii.utf8.enumerated() where offset + i < length {
            bytes[offset + i] = byte
        }
        return Data(bytes)
    }

    private func riffWave() -> Data {
        var data = header("RIFF")
        data.replaceSubrange(8..<12, with: Array("WAVE".utf8))
        return data
    }

    /// A real 16 kHz mono 16-bit WAV, 0.1 s of silence, written to `url`.
    private func writeWAV(to url: URL) throws {
        let format = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true)!
        let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: .pcmFormatInt16, interleaved: true)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 1_600)!
        buffer.frameLength = 1_600
        try file.write(from: buffer)
    }

    private func temporaryURL(_ name: String) -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("sniff-\(UUID().uuidString)")
            .appendingPathComponent(name)
    }

    // MARK: - Header detection

    @Test func recognisesEachContainer() {
        #expect(AudioContainerSniffer.detect(header: riffWave()) == .wav)
        var aiff = header("FORM")
        aiff.replaceSubrange(8..<12, with: Array("AIFF".utf8))
        #expect(AudioContainerSniffer.detect(header: aiff) == .aiff)
        #expect(AudioContainerSniffer.detect(header: header("caff")) == .caf)
        #expect(AudioContainerSniffer.detect(header: header("fLaC")) == .flac)
        #expect(AudioContainerSniffer.detect(header: header("OggS")) == .ogg)
        #expect(AudioContainerSniffer.detect(header: header("ID3")) == .mp3)
        #expect(AudioContainerSniffer.detect(header: Data([0xFF, 0xFB, 0x90, 0x64])) == .mp3)
        #expect(AudioContainerSniffer.detect(header: Data([0xFF, 0xF1, 0x50, 0x80])) == .aac)
        #expect(AudioContainerSniffer.detect(header: Data([0x1A, 0x45, 0xDF, 0xA3, 0x9F])) == .webm)
    }

    @Test func mpeg4BrandPicksTheExtension() {
        func ftyp(_ brand: String) -> Data {
            var data = header("ftyp", at: 4)
            data.replaceSubrange(8..<12, with: Array(brand.utf8))
            return data
        }
        #expect(AudioContainerSniffer.detect(header: ftyp("M4A ")) == .mpeg4(preferredExtension: "m4a"))
        #expect(AudioContainerSniffer.detect(header: ftyp("qt  ")) == .mpeg4(preferredExtension: "mov"))
        #expect(AudioContainerSniffer.detect(header: ftyp("isom")) == .mpeg4(preferredExtension: "mp4"))
    }

    @Test func unknownOrShortHeaderIsNil() {
        #expect(AudioContainerSniffer.detect(header: Data()) == nil)
        #expect(AudioContainerSniffer.detect(header: Data("RIF".utf8)) == nil)
        #expect(AudioContainerSniffer.detect(header: header("hello, world")) == nil)
    }

    // MARK: - Extension for the copy

    @Test func mismatchedExtensionTakesTheSniffedOne() {
        let wav = AudioContainerSniffer.detect(header: riffWave())
        #expect(AudioContainerSniffer.effectiveExtension(for: URL(fileURLWithPath: "/t/wrong-ext.mp3"), detected: wav) == "wav")
        #expect(AudioContainerSniffer.effectiveExtension(for: URL(fileURLWithPath: "/t/no-extension"), detected: wav) == "wav")
    }

    @Test func matchingOrUnknownKeepsTheFileExtension() {
        #expect(AudioContainerSniffer.effectiveExtension(for: URL(fileURLWithPath: "/t/a.WAV"), detected: .wav) == "WAV")
        // An M4A named .mp4 is still ISO-BMFF; AVFoundation opens it either way.
        #expect(AudioContainerSniffer.effectiveExtension(for: URL(fileURLWithPath: "/t/a.mp4"), detected: .mpeg4(preferredExtension: "m4a")) == "mp4")
        #expect(AudioContainerSniffer.effectiveExtension(for: URL(fileURLWithPath: "/t/a.mp3"), detected: nil) == "mp3")
        #expect(AudioContainerSniffer.effectiveExtension(for: URL(fileURLWithPath: "/t/blob"), detected: nil) == "")
    }

    // MARK: - Real files

    /// The issue's case: a valid WAV named .mp3 or with no extension. Under its
    /// own name AVAudioFile refuses it; under the sniffed name it opens.
    @Test(arguments: ["wrong-ext.mp3", "no-extension"])
    func misnamedWAVOpensUnderTheSniffedExtension(_ name: String) throws {
        let source = temporaryURL("real.wav")
        try FileManager.default.createDirectory(at: source.deletingLastPathComponent(), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: source.deletingLastPathComponent()) }
        try writeWAV(to: source)

        let misnamed = source.deletingLastPathComponent().appendingPathComponent(name)
        try FileManager.default.copyItem(at: source, to: misnamed)

        let detected = AudioContainerSniffer.detect(url: misnamed)
        #expect(detected == .wav)
        let ext = AudioContainerSniffer.effectiveExtension(for: misnamed, detected: detected)
        #expect(ext == "wav")

        let renamed = misnamed.deletingLastPathComponent().appendingPathComponent("copy.\(ext)")
        try FileManager.default.copyItem(at: misnamed, to: renamed)
        let file = try AVAudioFile(forReading: renamed)
        #expect(file.length == 1_600)
    }

    @Test func missingFileIsNil() {
        #expect(AudioContainerSniffer.detect(url: temporaryURL("absent.wav")) == nil)
    }

    // MARK: - Open panel and copy

    @Test func openPanelNoLongerOffersWebM() throws {
        // The panel enables a file whose type conforms to any allowed type, so
        // the umbrella `.audio` entry must not cover WebM either.
        let webm = try #require(UTType(filenameExtension: "webm"))
        let covering = FileTranscriptionFlow.supportedAudioTypes.filter { webm.conforms(to: $0) }
        #expect(covering.isEmpty, "WebM is still offered through \(covering.map(\.identifier))")
    }

    @Test func everyLocaleHasTheNewAlertKeys() throws {
        let keys = ["transcribe.file.error.webm", "transcribe.file.error.unreadableAudio"]
        let localizations = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(
            at: localizations,
            includingPropertiesForKeys: nil
        ).filter { $0.pathExtension == "lproj" }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.text(of: locale.appendingPathComponent("Localizable.strings"))
                .components(separatedBy: .newlines)
            for key in keys {
                let matches = lines.filter { $0.hasPrefix("\"\(key)\" = \"") }
                #expect(matches.count == 1, "\(locale.lastPathComponent) needs exactly one \(key)")
                guard let line = matches.first else { continue }
                #expect(line.trimmingCharacters(in: .whitespaces).hasSuffix("\";"), "\(locale.lastPathComponent) \(key)")
                #expect(!line.contains("%"), "\(locale.lastPathComponent) \(key) takes no format argument")
            }
        }
    }
}
