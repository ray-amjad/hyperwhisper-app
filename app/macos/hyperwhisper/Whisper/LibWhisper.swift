//
//  LibWhisper.swift
//  hyperwhisper
//
//  Wrapper around whisper.cpp for thread-safe transcription
//

import Foundation
import os

// MARK: - Timed transcription value types

/// A single word with approximate start/end times (in seconds) and average
/// token probability. "Approximate" = derived from whisper.cpp's non-DTW
/// `token_timestamps`; reliable enough for captions but can drift at phrase
/// starts / fast speech. A future DTW upgrade swaps the time source.
struct WhisperWordTiming: Sendable {
    let word: String
    let start: Double
    let end: Double
    let probability: Double?
}

/// A whisper segment with start/end times (in seconds) and its text. Segment
/// times come straight from `whisper_full` and are reliable.
struct WhisperSegmentTiming: Sendable {
    let id: Int
    let start: Double
    let end: Double
    let text: String
}

/// Result of a timed transcription run. `rawText` is the concatenated segment
/// text BEFORE `cleanTranscription`; the timestamps align to `rawText`, never
/// to `cleanedText` (which callers use as the user-facing `text`).
struct WhisperTimedTranscription: Sendable {
    let rawText: String
    let cleanedText: String
    let segments: [WhisperSegmentTiming]
    let words: [WhisperWordTiming]?   // nil unless word timestamps were requested
}

/// Errors that can occur during Whisper operations
enum WhisperError: Error, LocalizedError {
    case modelLoadFailed
    case transcriptionFailed
    case invalidAudioFormat
    case contextCreationFailed
    
    var errorDescription: String? {
        switch self {
        case .modelLoadFailed:
            return "Failed to load Whisper model"
        case .transcriptionFailed:
            return "Transcription failed"
        case .invalidAudioFormat:
            return "Invalid audio format"
        case .contextCreationFailed:
            return "Failed to create Whisper context"
        }
    }
}

/// Actor-based wrapper for whisper.cpp to ensure thread safety
/// Uses an actor to meet Whisper's constraint: don't access from more than one thread at a time
actor WhisperContext {
    // MARK: - Properties
    
    /// The underlying whisper.cpp context pointer
    private var context: OpaquePointer?
    
    /// Language for transcription (nil = auto-detect)
    private var language: String?
    
    /// Custom vocabulary/prompt for better accuracy
    private var prompt: String?

    /// Logger for debugging
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "WhisperContext")
    
    // MARK: - Initialization
    
    /// Private initializer to enforce factory method usage
    private init() {}
    
    /// Initialize with an existing context
    init(context: OpaquePointer) {
        self.context = context
    }
    
    deinit {
        // Clean up whisper context when actor is deallocated
        if let context = context {
            whisper_free(context)
        }
    }
    
    // MARK: - Factory Method
    
    /// Create a new WhisperContext by loading a model from disk
    /// - Parameter path: Path to the .bin model file
    /// - Returns: Initialized WhisperContext ready for transcription
    static func createContext(path: String) async throws -> WhisperContext {
        let whisperContext = WhisperContext()
        try await whisperContext.initializeModel(path: path)
        return whisperContext
    }
    
    // MARK: - Model Loading
    
    /// Initialize the Whisper model from a file path
    /// - Parameter path: Path to the .bin model file
    private func initializeModel(path: String) throws {
        // Configure context parameters
        var params = whisper_context_default_params()
        
        #if targetEnvironment(simulator)
        // Disable GPU on simulator as Metal isn't fully supported
        params.use_gpu = false
        logger.info("Running on simulator, using CPU only")
        #else
        // Enable Metal acceleration on real devices
        params.flash_attn = true // Enable flash attention for Metal
        params.use_gpu = true
        logger.info("Metal acceleration enabled")
        #endif
        
        // Load the model from file
        let context = whisper_init_from_file_with_params(path, params)
        
        if let context = context {
            self.context = context
            logger.info("Successfully loaded model from: \(path)")
        } else {
            logger.error("Failed to load model from: \(path)")
            throw WhisperError.modelLoadFailed
        }
    }
    
    // MARK: - Configuration
    
    /// Set the language for transcription
    /// - Parameter language: Language code (e.g., "en", "es") or nil for auto-detect
    func setLanguage(_ language: String?) {
        self.language = language
    }
    
    /// Set a custom prompt for better accuracy with domain-specific vocabulary
    /// - Parameter prompt: Custom vocabulary or context
    func setPrompt(_ prompt: String?) {
        self.prompt = prompt
    }
    
    // MARK: - Transcription
    
    /// Perform transcription on audio samples
    /// - Parameters:
    ///   - samples: Audio samples in Float32 format
    ///   - translate: If true, translate to English instead of transcribing.
    ///                Used as a fallback for empty outputs or when forcing English output.
    ///   - beamSearch: If true, use beam search instead of greedy decoding (slower but more accurate)
    ///   - temperature: Temperature for sampling (0.0 = deterministic/greedy, higher = more random)
    /// - Returns: true if transcription succeeded
    func fullTranscribe(samples: [Float], translate: Bool = false, beamSearch: Bool = false, temperature: Float = 0.0, wordTimestamps: Bool = false) -> Bool {
        guard let context = context else { 
            logger.error("No context available for transcription")
            return false 
        }
        
        // Determine optimal thread count (leave 2 cores for system)
        let maxThreads = max(1, min(8, ProcessInfo.processInfo.processorCount - 2))
        
        // Configure transcription parameters
        // DECODING OPTIONS: Support both greedy (fast) and beam search (accurate)
        let samplingStrategy = beamSearch ? WHISPER_SAMPLING_BEAM_SEARCH : WHISPER_SAMPLING_GREEDY
        var params = whisper_full_default_params(samplingStrategy)
        
        // Configure beam search if enabled
        if beamSearch {
            params.beam_search.beam_size = 5  // Standard beam width for quality/speed balance
            logger.debug("🎯 Using beam search with beam_size=5")
        }
        
        // Configure language detection. The actual C string for `params.language`
        // is bound below, inside the closure that calls `whisper_full`, so its
        // pointer stays valid for the whole call (see note above `runTranscription`).
        if language != nil {
            params.detect_language = false  // Explicitly disable auto-detection when language is set
            logger.debug("🌍 Language set to: \(self.language ?? "")")
        } else {
            // CRITICAL FIX: Enable auto-detect when no language specified
            // Without this, whisper won't detect the language automatically
            params.language = nil
            params.detect_language = true  // Enable automatic language detection
            logger.debug("🌍 Language auto-detection enabled")
        }

        // Configure transcription parameters
        params.print_realtime = false  // Don't print to console
        params.print_progress = false   // Don't show progress
        params.print_timestamps = false // Don't include timestamps in output (we're not using them)
        params.print_special = false    // Don't print special tokens
        params.translate = translate    // Transcribe or translate to English
        params.n_threads = Int32(maxThreads)
        params.offset_ms = 0           // Start from beginning
        params.no_context = true        // Don't use context from previous call
        params.single_segment = false   // Allow multiple segments
        params.temperature = temperature // Use provided temperature (default 0.0)

        // OPT-IN word-level timestamps. When requested, ask whisper.cpp to compute
        // token-level timestamps and split on word boundaries. We deliberately do NOT
        // set `max_len` here: that keeps segment text natural and we recover words via
        // the BPE leading-space heuristic in `getTimedTranscription`. (Risk R3: only add
        // max_len if word splitting proves unsatisfactory.) Segment t0/t1 are populated
        // by `whisper_full` unconditionally, so no flag is needed for segment timestamps.
        if wordTimestamps {
            params.token_timestamps = true
            params.split_on_word = true
        }

        // TEMPERATURE FALLBACK SCHEDULE: Could implement retry with higher temps
        // If output is empty/poor, retry with params.temperature = 0.4, 0.6, 0.8
        // Caller can implement this by calling with different temperatures

        // Reset timing information
        whisper_reset_timings(context)

        // Perform transcription.
        //
        // `params.language` / `params.initial_prompt` are `const char *` that
        // whisper.cpp reads during `whisper_full`. The C string pointers are only
        // valid for the lifetime of the `withCString` closures that produce them,
        // so the `whisper_full` call must happen *inside* those closures — storing
        // the pointers and using them afterwards is undefined behavior (the backing
        // storage may move). We bind language and prompt (when present) and then run
        // the transcription at the innermost level.
        func runTranscription() -> Bool {
            var ok = true
            samples.withUnsafeBufferPointer { samplesBuffer in
                if whisper_full(context, params, samplesBuffer.baseAddress, Int32(samplesBuffer.count)) != 0 {
                    logger.error("whisper_full failed")
                    ok = false
                }
            }
            return ok
        }

        func withPrompt(_ body: () -> Bool) -> Bool {
            guard let prompt = prompt else {
                params.initial_prompt = nil
                return body()
            }
            return prompt.withCString { promptPtr in
                params.initial_prompt = promptPtr
                return body()
            }
        }

        if let language = language {
            return language.withCString { langPtr in
                params.language = langPtr
                return withPrompt(runTranscription)
            }
        } else {
            return withPrompt(runTranscription)
        }
    }
    
    /// Get the transcribed text from the last transcription
    /// - Returns: Transcribed text
    func getTranscription() -> String {
        guard let context = context else { return "" }
        
        var transcription = ""
        
        // Concatenate all segments
        let segmentCount = whisper_full_n_segments(context)
        logger.info("🔎 Segments detected: \(segmentCount)")
        for i in 0..<segmentCount {
            if let text = whisper_full_get_segment_text(context, i) {
                let seg = String(cString: text)
                logger.debug("Segment #\(i) length: \(seg.count)")
                if seg.count <= 200 {
                    logger.debug("Segment #\(i) text: \(seg)")
                } else {
                    logger.debug("Segment #\(i) preview: \(seg.prefix(200))…")
                }
                transcription += seg
            }
        }
        
        // PRIVACY: Don't log actual transcription text - users export diagnostic logs
        // Only log metadata (character count) for debugging purposes
        let rawLen = transcription.count
        logger.info("📝 Raw transcription: \(rawLen) chars")

        // Clean up common hallucinations and artifacts
        transcription = cleanTranscription(transcription)
        logger.info("🧹 Cleaned transcription: \(transcription.count) chars")
        
        return transcription
    }
    
    /// Extract segment- (and optionally word-) level timestamps from the last
    /// transcription run, alongside both the raw and cleaned text.
    ///
    /// Timing units: whisper.cpp `t0`/`t1` are in centiseconds (10 ms units) →
    /// divide by 100.0 for float seconds. Times are relative to the start of the
    /// samples fed to `whisper_full` (offset_ms == 0), i.e. t=0 is the start of
    /// the audio whisper transcribed.
    ///
    /// - Parameter includeWords: when true, also merge BPE sub-word tokens into
    ///   `WhisperWordTiming` entries. Only meaningful if the producing
    ///   `fullTranscribe` call passed `wordTimestamps: true`.
    func getTimedTranscription(includeWords: Bool) -> WhisperTimedTranscription {
        guard let context = context else {
            return WhisperTimedTranscription(rawText: "", cleanedText: "", segments: [], words: includeWords ? [] : nil)
        }

        let eot = whisper_token_eot(context)
        var rawText = ""
        var segments: [WhisperSegmentTiming] = []
        var words: [WhisperWordTiming] = []

        // Accumulator for the word currently being assembled from BPE pieces.
        var pendingPieces = ""
        var pendingStart: Double = 0
        var pendingProbSum: Double = 0
        var pendingProbCount: Int = 0
        var hasPending = false
        // Running end-time of the most recent word token, so the trailing
        // flushWord after the token loop has a valid boundary.
        var pendingLastEnd: Double = 0

        func flushWord(end: Double) {
            guard hasPending else { return }
            let trimmed = pendingPieces.trimmingCharacters(in: .whitespaces)
            if !trimmed.isEmpty {
                let avgProb = pendingProbCount > 0 ? pendingProbSum / Double(pendingProbCount) : nil
                words.append(WhisperWordTiming(word: trimmed, start: pendingStart, end: end, probability: avgProb))
            }
            pendingPieces = ""
            pendingProbSum = 0
            pendingProbCount = 0
            hasPending = false
        }

        let segmentCount = whisper_full_n_segments(context)
        for i in 0..<segmentCount {
            let start = Double(whisper_full_get_segment_t0(context, i)) / 100.0
            let end = Double(whisper_full_get_segment_t1(context, i)) / 100.0
            var segText = ""
            if let cText = whisper_full_get_segment_text(context, i) {
                segText = String(cString: cText)
            }
            rawText += segText
            segments.append(WhisperSegmentTiming(id: Int(i), start: start, end: end, text: segText))

            guard includeWords else { continue }

            // Flush any word still pending from the previous segment at the end
            // of its last token so words never span across segments.
            flushWord(end: pendingLastEnd)

            // LIMITATION (v1, non-DTW): word boundaries are recovered from the
            // leading-space prefix on each BPE token (see `piece.hasPrefix(" ")`
            // below). This is reliable for English and other Latin/space-delimited
            // scripts, where Whisper's tokenizer emits space-prefixed word starts.
            // For CJK / Thai / continuous scripts — and to a lesser extent Arabic —
            // tokens often carry no leading space, so this either collapses a whole
            // segment into one "word" or splits per character. `segments[]` timings
            // stay reliable regardless; only `words[]` granularity degrades. The
            // future DTW upgrade (whisper_token_data.t_dtw) should replace this
            // heuristic with frame-accurate per-token alignment for these scripts.
            let tokenCount = whisper_full_n_tokens(context, i)
            for j in 0..<tokenCount {
                let data = whisper_full_get_token_data(context, i, j)
                // Skip special / timestamp tokens (id >= eot) — they carry no text.
                if data.id >= eot { continue }
                guard let cTokenText = whisper_full_get_token_text(context, i, j) else { continue }
                let piece = String(cString: cTokenText)
                // Skip bracketed special markers like "[_BEG_]" / "[_TT_..]".
                if piece.hasPrefix("[_") { continue }

                let tStart = Double(data.t0) / 100.0
                let tEnd = Double(data.t1) / 100.0

                // A new word begins when the token text carries a leading space.
                if piece.hasPrefix(" ") {
                    flushWord(end: pendingLastEnd)
                    pendingStart = tStart
                    hasPending = true
                }
                if !hasPending {
                    // First token of a segment may have no leading space.
                    pendingStart = tStart
                    hasPending = true
                }
                pendingPieces += piece
                pendingProbSum += Double(data.p)
                pendingProbCount += 1
                // Carry the running end so the final flush has a valid boundary.
                pendingLastEnd = tEnd
            }
        }
        // Flush the trailing word at the end of the last token seen.
        flushWord(end: pendingLastEnd)

        let cleaned = cleanTranscription(rawText)
        return WhisperTimedTranscription(
            rawText: rawText,
            cleanedText: cleaned,
            segments: segments,
            words: includeWords ? words : nil
        )
    }

    /// Clean common whisper hallucinations and artifacts
    /// Notes:
    /// - Do not strip "..." to avoid removing genuine content from some languages.
    /// - Use `.letters` to treat non-Latin scripts as valid when filtering very short noise.
    private func cleanTranscription(_ text: String) -> String {
        var cleaned = text
        
        // Common hallucination patterns to remove
        let hallucinationPatterns = [
            "Thank you for watching!",
            "Thanks for watching!",
            "Please subscribe",
            "Don't forget to like",
            "[Music]",
            "[Applause]",
            "(applause)",
            "(music)",
            "Transcribed by https://otter.ai",
            "Subtitles by",
            "♪♪♪",
            ">>>",
        ]
        let hallucinationRegexPatterns = [
            #"(?i)\btitulky\s+p[řr]ipravil[ay]?\b[^.!?\n]*"#,
            #"(?i)\bsubtitles?\s+(prepared\s+by|by)\b[^.!?\n]*"#,
        ]
        
        for pattern in hallucinationPatterns {
            cleaned = cleaned.replacingOccurrences(of: pattern, with: "", options: .caseInsensitive)
        }
        for pattern in hallucinationRegexPatterns {
            cleaned = cleaned.replacingOccurrences(of: pattern, with: "", options: .regularExpression)
        }
        
        // Remove excessive whitespace
        cleaned = cleaned.replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        cleaned = cleaned.trimmingCharacters(in: .whitespacesAndNewlines)
        
        // If the result is very short and looks like noise, return empty.
        // Use `.letters` so non-Latin scripts (e.g., Chinese, Japanese, Arabic)
        // are treated as valid content rather than noise.
        if cleaned.count < 3 && cleaned.rangeOfCharacter(from: .letters) == nil {
            return ""
        }
        
        return cleaned
    }
    
    /// Get the auto-detected language (2-letter code) from the last transcription, if available
    func getDetectedLanguage() -> String? {
        guard let context = context else { return nil }
        let langId = whisper_full_lang_id(context)
        if langId >= 0 {
            if let cstr = whisper_lang_str(langId) {
                return String(cString: cstr)
            }
        }
        return nil
    }
    
    // MARK: - Resource Management
    
    /// Release all resources associated with this context
    func releaseResources() {
        if let context = context {
            whisper_free(context)
            self.context = nil
        }
        logger.info("Released Whisper resources")
    }
    
    /// Check if the context is ready for transcription
    var isReady: Bool {
        return context != nil
    }
}

// NOTE: Audio loading is handled by LibWhisperProvider using AVFoundation
// The provider loads audio files and converts them to the required format
// (16kHz, mono, Float32) before passing samples to WhisperContext.fullTranscribe()
