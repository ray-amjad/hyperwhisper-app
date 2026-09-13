//
//  SilenceTrimmerWriterCloseTests.swift
//  hyperwhisperTests
//
//  Regression guard for issue #612: `SilenceTrimmer.writeAudioFile` must close
//  the `AVAudioFile` writer BEFORE it resumes its continuation.
//
//  WHY THE ORDER MATTERS:
//  ======================
//  An audio container is only valid once its writer is disposed — that is when
//  the RIFF chunk sizes (WAV) or the `moov` atom (MP4) are written. A caller
//  that resumes first and re-opens the URL can get a stub. `FileTranscriptionFlow`
//  re-opens it immediately: `getAudioDuration(finalAudioURL)` —
//  `AVURLAsset.load(.duration)` — runs on the line after VAD processing.
//
//  WHY THIS IS A SOURCE ASSERTION AND NOT A BEHAVIOURAL ONE:
//  ========================================================
//  The obvious test is to trim a file and re-open the artifact on the next
//  line. It was written, run on CI, and it does not work — for two independent
//  reasons, both measured rather than assumed:
//
//  1. IT CANNOT GO RED. `continuation.resume()` does not run the awaiting task
//     inline; it schedules it on the cooperative pool, which costs microseconds.
//     The GCD block that called `resume()` only has to fall out of a scope to
//     release the writer, which costs nanoseconds. The writer wins effectively
//     every time, so the artifact is already complete by the time any caller can
//     look at it. The behavioural test passed on unfixed `main` — it asserts a
//     race that the scheduler decides, not the ordering this file is about.
//
//  2. IT IS UNAFFORDABLE. Any test that reaches `writeAudioFile` has to go
//     through `trimSilence`, which runs the real Silero VAD. In the Debug test
//     host that costs minutes per call: one such test took the macOS CI suite
//     from 15 seconds to 25 minutes, and starved a neighbouring timing-sensitive
//     suite into failing. (The same model over the same 36 seconds of audio
//     costs 0.14 s against a release build of whisper.cpp, so this is the Debug
//     configuration, not the model.)
//
//  So the ordering is wiring: real, worth guarding, and not reachable by calling
//  anything. That is what `ProductionSource` is for — see the note at the top of
//  `ProductionSource.swift` about this being the last resort rather than the
//  first. This test reads the writer's body and asserts the shape of it.
//

import Foundation
import Testing

struct SilenceTrimmerWriterCloseTests {

    private let trimmerPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/Processing/SilenceTrimmer.swift"

    @Test func theWriterIsClosedBeforeTheContinuationResumes() throws {
        let body = try writeAudioFileBody()

        let pool = try #require(
            body.firstIndex { $0.contains("autoreleasepool") },
            "writeAudioFile no longer scopes its AVAudioFile. The writer must be released before continuation.resume(), and on macOS 14.6 a scope is the only way to say so — AVAudioFile.close() is macOS 15."
        )
        let create = try #require(
            body.firstIndex { $0.contains("AVAudioFile(") },
            "writeAudioFile no longer creates an AVAudioFile — update this guard rather than deleting it."
        )
        let write = try #require(
            body.firstIndex { $0.contains(".write(from:") },
            "writeAudioFile no longer writes a buffer — update this guard rather than deleting it."
        )
        let resume = try #require(
            body.firstIndex { $0.trimmingCharacters(in: .whitespaces) == "continuation.resume()" },
            "writeAudioFile no longer has a bare continuation.resume() — update this guard rather than deleting it."
        )

        // The writer is created and written inside the scope...
        #expect(
            pool < create,
            "the AVAudioFile is created outside the autoreleasepool, so the scope does not release it"
        )
        #expect(
            create < write,
            "the buffer is written before the file is created"
        )

        // ...and the success resume comes after it.
        #expect(
            write < resume,
            "continuation.resume() runs before the buffer is written"
        )

        // THE REGRESSION ITSELF: `resume()` must sit OUTSIDE the writer's braces,
        // not merely after the write. Indentation is what distinguishes the two —
        // the bug being guarded had resume() as the next statement after the
        // write, at the same depth, with the writer still alive.
        let writeDepth = indentation(of: body[write])
        let resumeDepth = indentation(of: body[resume])
        #expect(
            resumeDepth < writeDepth,
            "continuation.resume() is indented \(resumeDepth) spaces and the write is indented \(writeDepth), so resume() is still inside the writer's scope — the AVAudioFile is alive and the file on disk is not finished (issue #612)"
        )
    }

    // MARK: - Helpers

    /// The lines of `writeAudioFile`, comments stripped.
    ///
    /// `writeAudioFile` is the last member of `SilenceTrimmer`, so the slice runs
    /// to the end of the file.
    private func writeAudioFileBody() throws -> [String] {
        let source = try ProductionSource.code(of: trimmerPath)
        let anchor = "private func writeAudioFile("
        guard let start = source.range(of: anchor) else {
            throw ProductionSource.Failure.anchorNotFound(
                anchor: anchor,
                file: URL(fileURLWithPath: trimmerPath).lastPathComponent
            )
        }
        return String(source[start.upperBound...]).components(separatedBy: .newlines)
    }

    private func indentation(of line: String) -> Int {
        line.prefix { $0 == " " }.count
    }
}
