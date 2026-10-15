//
//  AudioContainerSniffer.swift
//  HyperWhisper
//
//  Reads an imported file's container format from its first bytes, so the file
//  is decoded by what it is rather than by what it is called (issue #1426).
//
//  AVFoundation picks its parser from the file extension: a valid WAV named
//  `.mp3` fails in `AVAudioFile(forReading:)` with 'dta?', and a WAV with no
//  extension fails in `AVURLAsset.load(.duration)` with "Cannot Open". The same
//  bytes under the right name decode fine, so the import names its copy after
//  the sniffed format.
//

import Foundation

/// A container format recognised from a file's header.
enum AudioContainerFormat: Equatable {
    case wav
    case aiff
    case caf
    case flac
    case ogg
    case mp3
    case aac
    /// ISO base media (`ftyp`): MP4, M4A, MOV. The major brand picks the extension.
    case mpeg4(preferredExtension: String)
    /// Matroska / WebM (EBML header). AVFoundation cannot decode it.
    case webm

    /// Extensions that already name this format. A file whose extension is in
    /// the set keeps it; any other extension is replaced by `preferredExtension`.
    var acceptedExtensions: Set<String> {
        switch self {
        case .wav: return ["wav", "wave"]
        case .aiff: return ["aiff", "aif", "aifc"]
        case .caf: return ["caf"]
        case .flac: return ["flac"]
        case .ogg: return ["ogg", "oga", "opus"]
        case .mp3: return ["mp3", "mpga", "mpeg"]
        case .aac: return ["aac", "adts"]
        // Every ISO-BMFF extension AVFoundation opens: an M4A named .mp4 is fine.
        case .mpeg4: return ["mp4", "m4a", "m4b", "m4v", "mov", "3gp", "3g2"]
        case .webm: return ["webm", "mkv", "mka"]
        }
    }

    var preferredExtension: String {
        switch self {
        case .wav: return "wav"
        case .aiff: return "aiff"
        case .caf: return "caf"
        case .flac: return "flac"
        case .ogg: return "ogg"
        case .mp3: return "mp3"
        case .aac: return "aac"
        case .mpeg4(let ext): return ext
        case .webm: return "webm"
        }
    }
}

enum AudioContainerSniffer {
    /// Bytes read from the start of the file. Enough for every header below.
    static let headerLength = 16

    /// The format of the file at `url`, or nil when the header is not one we know
    /// (or the file cannot be read). Nil means "trust the extension".
    static func detect(url: URL) -> AudioContainerFormat? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let header = try? handle.read(upToCount: headerLength) else { return nil }
        return detect(header: header)
    }

    /// The format named by `header`, the first bytes of a file.
    static func detect(header: Data) -> AudioContainerFormat? {
        let bytes = [UInt8](header.prefix(headerLength))
        func ascii(_ range: Range<Int>) -> String? {
            guard bytes.count >= range.upperBound else { return nil }
            return String(bytes: bytes[range], encoding: .ascii)
        }

        if ascii(0..<4) == "RIFF", ascii(8..<12) == "WAVE" { return .wav }
        // RF64 / BW64: 64-bit WAV, which AVFoundation reads as WAV.
        if ascii(0..<4) == "RF64" || ascii(0..<4) == "BW64", ascii(8..<12) == "WAVE" { return .wav }
        if ascii(0..<4) == "FORM", let kind = ascii(8..<12), kind == "AIFF" || kind == "AIFC" { return .aiff }
        if ascii(0..<4) == "caff" { return .caf }
        if ascii(0..<4) == "fLaC" { return .flac }
        if ascii(0..<4) == "OggS" { return .ogg }
        if bytes.starts(with: [0x1A, 0x45, 0xDF, 0xA3]) { return .webm }
        if ascii(4..<8) == "ftyp" {
            switch ascii(8..<12) {
            case "qt  ": return .mpeg4(preferredExtension: "mov")
            case "M4A ", "M4B ", "M4P ": return .mpeg4(preferredExtension: "m4a")
            default: return .mpeg4(preferredExtension: "mp4")
            }
        }
        if ascii(0..<3) == "ID3" { return .mp3 }
        // An MPEG audio frame sync: 11 set bits. Layer bits 00 are ADTS AAC,
        // anything else is MPEG audio (MP3).
        if bytes.count >= 2, bytes[0] == 0xFF, bytes[1] & 0xE0 == 0xE0 {
            return bytes[1] & 0x06 == 0 ? .aac : .mp3
        }
        return nil
    }

    /// The extension the import should give its copy of `url`.
    ///
    /// - Returns: the file's own extension when it matches its contents, or when
    ///   the header is unknown; otherwise the sniffed format's extension.
    static func effectiveExtension(for url: URL, detected: AudioContainerFormat?) -> String {
        let ext = url.pathExtension.lowercased()
        guard let detected, !detected.acceptedExtensions.contains(ext) else { return url.pathExtension }
        return detected.preferredExtension
    }
}
