namespace HyperWhisper.Models;

/// <summary>
/// Which sherpa-onnx engine family the parakeet-engine daemon should load for a
/// model. Both run through the same daemon (parakeet-engine.exe) but need
/// different sherpa config sub-structs and a different --engine selector value.
/// </summary>
public enum ParakeetEngine
{
    /// <summary>NVIDIA Parakeet TDT — offline transducer, DirectML→CPU.</summary>
    NemoTransducer,

    /// <summary>Qwen3-ASR — offline autoregressive, CPU-only, tokenizer directory.</summary>
    Qwen3,

    /// <summary>
    /// NVIDIA Nemotron-3.5 — online/streaming cache-aware transducer, multilingual
    /// (incl. Japanese), CPU-only. Same flat 4-file layout as Parakeet; language is
    /// selected at decode time via the daemon's per-stream prompt mechanism.
    /// </summary>
    NemotronMl
}

/// <summary>
/// PARAKEET MODEL METADATA
///
/// Represents an NVIDIA Parakeet ONNX model available for download and use.
/// Parakeet models use sherpa-onnx with DirectML for vendor-agnostic GPU acceleration
/// (AMD, Intel, NVIDIA) and CPU fallback on both x64 and ARM64.
///
/// MODEL FORMAT:
/// Each model consists of 4 ONNX files stored in a directory.
/// Downloaded from Hugging Face model repositories.
///
/// Unlike Whisper models (single .bin file), Parakeet models are multi-file
/// and stored in subdirectories under %LOCALAPPDATA%\HyperWhisper\Models\Parakeet\{model-id}\
/// </summary>
public class ParakeetModelInfo
{
    /// <summary>
    /// Unique model identifier (e.g., "parakeet-v2", "parakeet-v3").
    /// Used for directory naming and mode configuration.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Human-readable display name for the UI.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Approximate total download size (for user information).
    /// </summary>
    public string Size { get; }

    /// <summary>
    /// Approximate total size in bytes (for progress calculation during download).
    /// </summary>
    public long SizeInBytes { get; }

    /// <summary>
    /// Whether this model only supports English.
    /// </summary>
    public bool IsEnglishOnly { get; }

    /// <summary>
    /// ISO 639-1 language codes supported by this model.
    /// </summary>
    public string[] SupportedLanguages { get; }

    /// <summary>
    /// ONNX file names that must be downloaded for this model.
    /// Typically: encoder, decoder, joiner, and tokens.
    /// </summary>
    public string[] OnnxFileNames { get; }

    /// <summary>
    /// HuggingFace repository path for downloading model files.
    /// Format: "organization/repo-name"
    /// </summary>
    public string HuggingFaceRepo { get; }

    /// <summary>
    /// Which sherpa-onnx engine the daemon loads for this model. Determines the
    /// <c>--engine</c> value, provider policy, download shape, and timeouts.
    /// Defaults to <see cref="ParakeetEngine.NemoTransducer"/> (Parakeet).
    /// </summary>
    public ParakeetEngine Engine { get; }

    /// <summary>
    /// Display provider family for library rows. Several model families share the
    /// Parakeet daemon host, but should not all be presented to users as Parakeet.
    /// </summary>
    public string ProviderDisplayName => Engine switch
    {
        ParakeetEngine.Qwen3 => "Qwen3 ASR",
        ParakeetEngine.NemotronMl => "Nemotron",
        _ => "Parakeet"
    };

    /// <summary>
    /// Provider asset used by Model Library rows. Qwen3 and Nemotron share the
    /// local Parakeet daemon icon until dedicated brand marks are added.
    /// </summary>
    public string ProviderAssetName => "providerParakeet";

    /// <summary>
    /// The value passed to the daemon's <c>--engine</c> flag.
    /// </summary>
    public string DaemonEngineArg => Engine switch
    {
        ParakeetEngine.Qwen3 => "qwen3",
        ParakeetEngine.NemotronMl => "nemotron_ml",
        _ => "nemo_transducer"
    };

    // NOTE: CPU-only enforcement is NOT a C# concern — the daemon picks the
    // provider from the --engine value (qwen3 and nemotron_ml are CPU-only inside
    // parakeet-engine.exe). There is deliberately no ForceCpu property here; a C#
    // flag would only be honoured if the daemon also read a --provider arg, which
    // it does not, so it would be misleading dead code.

    /// <summary>
    /// Whether the model is distributed as a multi-file tree (incl. a tokenizer
    /// directory) that must be enumerated from HuggingFace rather than downloaded
    /// as a fixed flat file list. True for Qwen3.
    /// </summary>
    public bool IsHuggingFaceTreeDownload => Engine == ParakeetEngine.Qwen3;

    public ParakeetModelInfo(
        string id,
        string displayName,
        string size,
        long sizeInBytes,
        bool isEnglishOnly,
        string[] supportedLanguages,
        string[] onnxFileNames,
        string huggingFaceRepo,
        ParakeetEngine engine = ParakeetEngine.NemoTransducer)
    {
        Id = id;
        DisplayName = displayName;
        Size = size;
        SizeInBytes = sizeInBytes;
        IsEnglishOnly = isEnglishOnly;
        SupportedLanguages = supportedLanguages;
        OnnxFileNames = onnxFileNames;
        HuggingFaceRepo = huggingFaceRepo;
        Engine = engine;
    }

    /// <summary>
    /// Checks if the model supports a given language code.
    /// English-only models return true only for "en".
    /// </summary>
    public bool IsLanguageSupported(string languageCode)
    {
        if (string.IsNullOrEmpty(languageCode) || languageCode == "auto")
            return true; // Auto-detect is always supported

        return SupportedLanguages.Contains(languageCode, StringComparer.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{DisplayName} ({Size})";

    /// <summary>
    /// All available Parakeet models for download.
    ///
    /// MODELS:
    /// - Parakeet v2 (English-only): ~320 MB, fast English transcription
    /// - Parakeet v3 (Multilingual): ~640 MB, 25 European languages
    ///
    /// DOWNLOAD URLS:
    /// Base URL: https://huggingface.co/{HuggingFaceRepo}/resolve/main/{filename}
    /// </summary>
    public static readonly ParakeetModelInfo[] AllModels =
    [
        new ParakeetModelInfo(
            id: "parakeet-v2",
            displayName: "Parakeet v2 (English)",
            size: "661 MB",
            sizeInBytes: 661_000_000,
            isEnglishOnly: true,
            supportedLanguages: ["en"],
            onnxFileNames:
            [
                "encoder.int8.onnx",
                "decoder.int8.onnx",
                "joiner.int8.onnx",
                "tokens.txt"
            ],
            huggingFaceRepo: "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"),

        new ParakeetModelInfo(
            id: "parakeet-v3",
            displayName: "Parakeet v3 (Multilingual)",
            size: "671 MB",
            sizeInBytes: 671_000_000,
            isEnglishOnly: false,
            supportedLanguages:
            [
                "en", "de", "es", "fr", "it", "pt", "nl", "pl", "ro", "sv",
                "da", "fi", "no", "cs", "sk", "hu", "hr", "sl", "bg", "uk",
                "el", "lt", "lv", "et", "ca", "eu"
            ],
            onnxFileNames:
            [
                "encoder.int8.onnx",
                "decoder.int8.onnx",
                "joiner.int8.onnx",
                "tokens.txt"
            ],
            huggingFaceRepo: "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"),

        // Qwen3-ASR 0.6B (int8) — on-device multilingual ASR, CPU-only.
        // Unlike Parakeet (4 flat files), the Qwen3 model is a tree: conv_frontend
        // / encoder / decoder ONNX plus a `tokenizer/` directory. It is downloaded
        // by enumerating the HuggingFace repo (Engine == Qwen3 / tree download).
        //
        // Repo = the sherpa-onnx maintainer's (csukuangfj) official HF mirror of
        // the GitHub-release export `sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25`.
        // VERIFIED end-to-end on x64/CPU: transcribes Japanese correctly. A community
        // mirror (pantinor/...) shipped a corrupt decoder.int8.onnx that produced
        // garbage on x64 — do NOT switch the repo without re-running a Japanese
        // smoke test. Requires onnxruntime supporting ONNX IR v9 (>= ~1.17; sherpa's
        // DirectML build pins 1.14.1, which is too old — see build notes).
        new ParakeetModelInfo(
            id: "qwen3-asr-0.6b",
            displayName: "Qwen3 ASR 0.6B",
            size: "~985 MB",
            sizeInBytes: 985_000_000,
            isEnglishOnly: false,
            supportedLanguages:
            [
                "ja", "en", "zh", "ko", "es", "fr", "de", "it", "pt", "ru", "ar"
            ],
            onnxFileNames:
            [
                // Representative top-level artifacts; the tokenizer is a directory.
                // Used for documentation — the tree download/verify is engine-aware.
                "conv_frontend.onnx",
                "encoder.int8.onnx",
                "decoder.int8.onnx"
            ],
            huggingFaceRepo: "csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25",
            engine: ParakeetEngine.Qwen3),

        // Nemotron-3.5 streaming 0.6B (int8, 560ms tier) — multilingual on-device
        // ASR (English + Japanese), online/streaming cache-aware FastConformer-RNNT.
        // Same flat 4-file layout as Parakeet (encoder/decoder/joiner .int8.onnx +
        // tokens.txt), so it downloads/verifies through the standard flat path — NOT
        // a HuggingFace tree like Qwen3. The daemon loads it via --engine nemotron_ml
        // (online recognizer) and selects language per-stream (prompt_index).
        //
        // Repo = the sherpa-onnx maintainer's (csukuangfj2) official export. Runs
        // CPU-only in Phase 2 (DirectML correctness for the cache-aware graph is
        // unverified). Requires sherpa-onnx >= 1.13.3 (the daemon refuses to start
        // the online engine on older builds — the language option is a silent no-op
        // before then).
        //
        // SILENT-FAILURE GATES (now enforced at daemon load time, see
        // nemotron_validate_model() in tools/parakeet-engine/main.cpp): the
        // multilingual export must be the multi-output-node NeMo decoder, NOT a
        // "nemo_parakeet_unified_streaming" decoder (the unified variant silently
        // ignores the language prompt), and tokens.txt must be the multilingual
        // vocab (13088 lines, vs 1025 for English). If either check fails the
        // daemon refuses to start with a clear error instead of emitting
        // wrong-language garbage. Verified on this pinned export: decoder is the
        // multi-output NeMo variant (encoder has prompt_index), tokens.txt is
        // 13088 lines, and a Japanese clip transcribes to Japanese. Re-run that
        // Japanese smoke test if the repo is ever re-exported or mirror-swapped.
        new ParakeetModelInfo(
            id: "nemotron-3.5-ml-560ms",
            displayName: "Nemotron 3.5 Streaming (Multilingual)",
            size: "~660 MB",
            sizeInBytes: 682_000_000,
            isEnglishOnly: false,
            supportedLanguages:
            [
                "en", "ja"
            ],
            onnxFileNames:
            [
                "encoder.int8.onnx",
                "decoder.int8.onnx",
                "joiner.int8.onnx",
                "tokens.txt"
            ],
            huggingFaceRepo: "csukuangfj2/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11",
            engine: ParakeetEngine.NemotronMl)
    ];
}
