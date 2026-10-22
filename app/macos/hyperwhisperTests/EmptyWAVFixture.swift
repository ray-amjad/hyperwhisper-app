//
//  EmptyWAVFixture.swift
//  hyperwhisperTests
//
//  The two WAV files with no audio frames, written byte by byte, in one place.
//
//  Issues #1515 (Apple Speech hung) and #1516 (Nemotron and Qwen3 ASR answered
//  an empty success) came from the same two shapes: a 44-byte header-only WAV
//  whose `data` chunk is empty, and a WAV with a `fmt ` chunk, a `LIST` chunk
//  and no `data` chunk at all. `AppleSpeechAudioInputTests` and
//  `LocalAudioSampleLoaderTests` each had their own RIFF builder for them, and
//  the two had already drifted. One builder, so a fix to it reaches both.
//

import Foundation

enum EmptyWAVFixture {

    /// 44 bytes: RIFF header, `fmt `, and a `data` chunk of length 0.
    static func writeHeaderOnly(prefix: String) throws -> URL {
        let url = temporaryURL(prefix: prefix)
        try riff(pcmFormatChunk() + Array("data".utf8) + le32(0)).write(to: url)
        return url
    }

    /// `fmt ` plus a sizeable `LIST` chunk, and no `data` chunk at all — the
    /// "audio bytes: 0" file from the issues. At 8,248 bytes it is far past
    /// Nemotron's 5,000-byte pre-flight size guard, so only the frame check
    /// can refuse it.
    static func writeFormatOnly(prefix: String) throws -> URL {
        let url = temporaryURL(prefix: prefix)
        var listBody = Array("INFO".utf8) + Array("ISFT".utf8)
        let text = Array(String(repeating: "x", count: 8191).utf8) + [0] // even length, NUL-terminated
        listBody += le32(UInt32(text.count)) + text
        let list = Array("LIST".utf8) + le32(UInt32(listBody.count)) + listBody
        try riff(pcmFormatChunk() + list).write(to: url)
        return url
    }

    // MARK: - RIFF

    private static func temporaryURL(prefix: String) -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("\(prefix)-\(UUID().uuidString).wav")
    }

    private static func le32(_ value: UInt32) -> [UInt8] {
        withUnsafeBytes(of: value.littleEndian) { Array($0) }
    }

    private static func le16(_ value: UInt16) -> [UInt8] {
        withUnsafeBytes(of: value.littleEndian) { Array($0) }
    }

    /// A PCM `fmt ` chunk: 16 kHz, mono, 16-bit.
    private static func pcmFormatChunk() -> [UInt8] {
        let sampleRate: UInt32 = 16000
        let channels: UInt16 = 1
        let bitsPerSample: UInt16 = 16
        let blockAlign = channels * bitsPerSample / 8
        var body: [UInt8] = []
        body += le16(1) // PCM
        body += le16(channels)
        body += le32(sampleRate)
        body += le32(sampleRate * UInt32(blockAlign))
        body += le16(blockAlign)
        body += le16(bitsPerSample)
        return Array("fmt ".utf8) + le32(UInt32(body.count)) + body
    }

    private static func riff(_ chunks: [UInt8]) -> Data {
        let payload = Array("WAVE".utf8) + chunks
        return Data(Array("RIFF".utf8) + le32(UInt32(payload.count)) + payload)
    }
}
