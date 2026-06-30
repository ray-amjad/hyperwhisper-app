// parakeet-engine.exe
// A thin C++ daemon around sherpa-onnx's C API for on-device transcription.
// Communicates with the C# host via stdin/stdout JSON lines protocol.
//
// ENGINES (selected with --engine):
//   nemo_transducer  Parakeet TDT (offline transducer). DirectML -> CPU. DEFAULT.
//   qwen3            Qwen3-ASR 0.6B (offline, autoregressive). CPU-ONLY by design.
//   nemotron_ml      Nemotron-3.5 streaming (ONLINE cache-aware transducer),
//                    multilingual incl. Japanese. CPU. Language is selected at
//                    decode time via the per-stream "language" option, which
//                    sherpa maps to the encoder prompt_index (>= v1.13.3 only).
//
// PROTOCOL:
//   Args:    --model <dir> --vad-model <path> [--engine <name>] [--language <code>] [--join-language <code>]
//   Ready:   stdout: {"status":"ready","provider":"directml"|"cpu"}
//   Request: stdin:  {"audio_path":"/path/to/file.wav"}
//   Result:  stdout: {"text":"Hello world","duration_ms":1234}
//   Quit:    stdin:  {"command":"quit"}
//   Diag:    stderr: [INFO]/[WARN]/[ERROR] messages
//
// EXIT CODES: 0=clean, 1=model fail, 2=bad args, 3=runtime error
//
// WHY QWEN3 IS CPU-ONLY: sherpa's Qwen3 decoder only does GPU I/O binding for
// CUDA; DirectML loads but decodes silently-wrong text on AMD iGPUs (no error,
// so a null-check fallback never fires), and sherpa hard-codes DML device_id=0,
// defeating the host's adapter selection. CPU is the only correctness-safe path.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cmath>
#include <iostream>
#include <string>
#include <vector>

#ifdef _WIN32
#include <io.h>
#include <fcntl.h>
#include <windows.h>
#endif

#include "sherpa-onnx/c-api/c-api.h"

// =========================================================================
// LOGGING (stderr)
// =========================================================================

static void log_info(const char* msg) { fprintf(stderr, "[INFO] %s\n", msg); }
static void log_warn(const char* msg) { fprintf(stderr, "[WARN] %s\n", msg); }
static void log_error(const char* msg) { fprintf(stderr, "[ERROR] %s\n", msg); }

// =========================================================================
// SIMPLE JSON HELPERS
// =========================================================================

// Parse exactly `count` hex digits from `json` starting at `start`.
// Returns false without modifying `out` if any digit is non-hex or the string
// is too short. Used to decode \\uXXXX escapes without throwing.
static bool json_parse_hex(const std::string& json, size_t start, size_t count, unsigned int& out) {
    if (start + count > json.length()) return false;
    unsigned int value = 0;
    for (size_t i = 0; i < count; i++) {
        char c = json[start + i];
        value <<= 4;
        if (c >= '0' && c <= '9') value |= static_cast<unsigned int>(c - '0');
        else if (c >= 'a' && c <= 'f') value |= static_cast<unsigned int>(c - 'a' + 10);
        else if (c >= 'A' && c <= 'F') value |= static_cast<unsigned int>(c - 'A' + 10);
        else return false;
    }
    out = value;
    return true;
}

// Append a Unicode code point to `result` encoded as UTF-8.
static void json_append_utf8(std::string& result, unsigned int cp) {
    if (cp < 0x80) {
        result += static_cast<char>(cp);
    } else if (cp < 0x800) {
        result += static_cast<char>(0xC0 | (cp >> 6));
        result += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        result += static_cast<char>(0xE0 | (cp >> 12));
        result += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        result += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        result += static_cast<char>(0xF0 | (cp >> 18));
        result += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        result += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        result += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

// Extract a string value for a given key from a JSON object string.
// Returns empty string if key not found. Handles simple cases only.
static std::string json_get_string(const std::string& json, const std::string& key) {
    std::string search = "\"" + key + "\"";
    auto pos = json.find(search);
    if (pos == std::string::npos) return "";

    // Find the colon after the key
    pos = json.find(':', pos + search.length());
    if (pos == std::string::npos) return "";

    // Skip whitespace
    pos++;
    while (pos < json.length() && (json[pos] == ' ' || json[pos] == '\t')) pos++;

    if (pos >= json.length() || json[pos] != '"') return "";

    // Read the string value
    pos++; // skip opening quote
    std::string result;
    while (pos < json.length() && json[pos] != '"') {
        if (json[pos] == '\\' && pos + 1 < json.length()) {
            pos++; // skip escape
            switch (json[pos]) {
                case '"': result += '"'; break;
                case '\\': result += '\\'; break;
                case '/': result += '/'; break;
                case 'n': result += '\n'; break;
                case 't': result += '\t'; break;
                case 'u': {
                    unsigned int cp;
                    if (json_parse_hex(json, pos + 1, 4, cp)) {
                        pos += 4; // consume the 4 hex digits
                        unsigned int lo;
                        if (cp >= 0xD800 && cp <= 0xDBFF &&
                            pos + 6 < json.length() &&
                            json[pos + 1] == '\\' && json[pos + 2] == 'u' &&
                            json_parse_hex(json, pos + 3, 4, lo) &&
                            lo >= 0xDC00 && lo <= 0xDFFF) {
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            pos += 6; // consume the low surrogate escape
                        }
                        json_append_utf8(result, cp);
                    } else {
                        result += json[pos];
                    }
                    break;
                }
                default: result += json[pos]; break;
            }
        } else {
            result += json[pos];
        }
        pos++;
    }
    return result;
}

// Escape a string for JSON output
static std::string json_escape(const std::string& s) {
    std::string result;
    result.reserve(s.length() + 16);
    for (char c : s) {
        switch (c) {
            case '"':  result += "\\\""; break;
            case '\\': result += "\\\\"; break;
            case '\b': result += "\\b"; break;
            case '\f': result += "\\f"; break;
            case '\n': result += "\\n"; break;
            case '\r': result += "\\r"; break;
            case '\t': result += "\\t"; break;
            default:
                // JSON requires all control characters U+0000-U+001F to be escaped.
                // Cast through unsigned char so UTF-8 continuation bytes (>= 0x80,
                // which are negative as signed char) are passed through unchanged.
                if (static_cast<unsigned char>(c) < 0x20) {
                    char esc[7];
                    snprintf(esc, sizeof(esc), "\\u%04x",
                             static_cast<unsigned int>(static_cast<unsigned char>(c)));
                    result += esc;
                } else {
                    result += c;
                }
                break;
        }
    }
    return result;
}

// =========================================================================
// MODEL-FILE / LANGUAGE HELPERS
// =========================================================================

static bool file_exists(const std::string& path) {
    FILE* f = fopen(path.c_str(), "rb");
    if (f) { fclose(f); return true; }
    return false;
}

#ifdef _WIN32
static std::wstring utf8_to_wide(const std::string& s) {
    if (s.empty()) return std::wstring();
    int len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                  s.data(), static_cast<int>(s.size()),
                                  nullptr, 0);
    if (len <= 0) return std::wstring();

    std::wstring result(static_cast<size_t>(len), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                        s.data(), static_cast<int>(s.size()),
                        result.data(), len);
    return result;
}

static bool read_file_utf8_path(const std::string& path, std::vector<char>& bytes) {
    bytes.clear();

    std::wstring wide_path = utf8_to_wide(path);
    if (wide_path.empty()) return false;

    HANDLE file = CreateFileW(wide_path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size;
    if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > INT32_MAX) {
        CloseHandle(file);
        return false;
    }

    bytes.resize(static_cast<size_t>(size.QuadPart));
    size_t offset = 0;
    while (offset < bytes.size()) {
        DWORD chunk = static_cast<DWORD>(std::min<size_t>(bytes.size() - offset, 1 << 20));
        DWORD read = 0;
        if (!ReadFile(file, bytes.data() + offset, chunk, &read, nullptr)) {
            CloseHandle(file);
            return false;
        }
        if (read == 0) break;
        offset += read;
    }

    CloseHandle(file);
    return offset == bytes.size();
}
#endif

static const SherpaOnnxWave* read_wave_file(const std::string& path, std::vector<char>& wave_bytes) {
#ifdef _WIN32
    if (!read_file_utf8_path(path, wave_bytes)) return nullptr;
    return SherpaOnnxReadWaveFromBinaryData(wave_bytes.data(), static_cast<int32_t>(wave_bytes.size()));
#else
    (void)wave_bytes;
    return SherpaOnnxReadWave(path.c_str());
#endif
}

// Resolve an ONNX file in a model dir, preferring the int8 variant when present.
// e.g. pick_onnx(dir, "encoder") -> "<dir>/encoder.int8.onnx" if it exists,
// otherwise "<dir>/encoder.onnx". Lets one daemon load either quantization.
static std::string pick_onnx(const std::string& dir, const std::string& base) {
    std::string int8_path = dir + "/" + base + ".int8.onnx";
    if (file_exists(int8_path)) return int8_path;
    return dir + "/" + base + ".onnx";
}

// Qwen3-ASR takes the language as an English NAME via a per-stream option
// (e.g. "Japanese"), NOT an ISO code and NOT a config field. Map the host's
// ISO 639-1 code to the name. Empty result => leave unset (model auto-detects).
static std::string qwen3_language_name(const std::string& code) {
    if (code == "ja") return "Japanese";
    if (code == "en") return "English";
    if (code == "zh") return "Chinese";
    if (code == "ko") return "Korean";
    if (code == "yue") return "Cantonese";
    if (code == "es") return "Spanish";
    if (code == "fr") return "French";
    if (code == "de") return "German";
    if (code == "it") return "Italian";
    if (code == "pt") return "Portuguese";
    if (code == "ru") return "Russian";
    if (code == "ar") return "Arabic";
    return ""; // unknown / "auto" -> no hint
}

// Languages whose segments should be concatenated with no ASCII space between
// VAD chunks (CJK has no inter-word spaces).
static bool is_no_space_language(const std::string& code) {
    return code == "ja" || code == "zh" || code == "ko" || code == "yue";
}

// Root-mean-square amplitude of a float32 PCM segment (samples in [-1, 1]).
static float segment_rms(const float* samples, int32_t n) {
    if (!samples || n <= 0) return 0.0f;
    double acc = 0.0;
    for (int32_t i = 0; i < n; ++i) acc += static_cast<double>(samples[i]) * samples[i];
    return static_cast<float>(std::sqrt(acc / static_cast<double>(n)));
}

// =========================================================================
// ONLINE (STREAMING) ENGINE — Nemotron-3.5 cache-aware FastConformer-RNNT
// =========================================================================

// The Nemotron multilingual mechanism (the per-stream "language" option mapped
// to the encoder prompt_index) only exists in sherpa-onnx >= 1.13.3. On an
// older lib the option is silently ignored, yielding wrong-language output with
// NO error. Refuse to start the online engine rather than ship silent garbage.
static bool sherpa_at_least_1_13_3() {
    const char* v = SherpaOnnxGetVersionStr();
    if (!v) return false;
    int major = 0, minor = 0, patch = 0;
    if (sscanf(v, "%d.%d.%d", &major, &minor, &patch) < 3) return false;
    if (major != 1) return major > 1;
    if (minor != 13) return minor > 13;
    return patch >= 3;
}

// Create the online streaming recognizer for Nemotron-3.5. CPU provider only
// (Phase 2): DirectML correctness for this cache-aware graph is unverified.
// model_type is left empty so sherpa auto-detects the multilingual NeMo path
// from decoder metadata. (A "parakeet_unified" decoder export would NOT honour
// prompt_index; that variant — and a wrong/truncated vocab — are rejected at
// load time by nemotron_validate_model() below before we ever get here.) Same
// flat 4-file layout as Parakeet: encoder/decoder/joiner .int8.onnx + tokens.txt.
static const SherpaOnnxOnlineRecognizer* create_online_recognizer(
        const std::string& encoder, const std::string& decoder,
        const std::string& joiner,  const std::string& tokens) {
    SherpaOnnxOnlineRecognizerConfig oc;
    memset(&oc, 0, sizeof(oc));

    oc.feat_config.sample_rate = 16000;
    oc.feat_config.feature_dim = 128;   // Nemotron encoder is 128-dim fbank
                                        // (sherpa also overrides this from
                                        // model metadata; set to match anyway).

    oc.model_config.transducer.encoder = encoder.c_str();
    oc.model_config.transducer.decoder = decoder.c_str();
    oc.model_config.transducer.joiner  = joiner.c_str();
    oc.model_config.tokens             = tokens.c_str();
    oc.model_config.num_threads        = 2;
    oc.model_config.debug              = 0;
    oc.model_config.provider           = "cpu";
    oc.model_config.model_type         = "";   // auto-detect (NeMo via metadata)

    oc.decoding_method  = "greedy_search";
    oc.max_active_paths = 4;   // ignored by greedy_search; set for completeness
    oc.enable_endpoint  = 0;   // VAD segments the audio; no internal endpointing

    return SherpaOnnxCreateOnlineRecognizer(&oc);
}

// Load-time correctness gates for the multilingual Nemotron model. These turn
// two otherwise-SILENT failure traps into hard refusals (returns "" if OK, else
// a human-readable reason):
//   1. A "nemo_parakeet_unified_streaming" decoder export ignores the language
//      prompt and emits one fixed language with no error. We detect it by
//      scanning the decoder ONNX for that metadata marker string.
//   2. A truncated download, or the English (1025-line) vocab in tokens.txt,
//      misaligns token ids and produces garbage. We require a multilingual-
//      sized vocab (the Nemotron ML vocab is 13088 lines).
static std::string nemotron_validate_model(const std::string& model_dir) {
    // (1) Decoder must NOT be the unified-streaming variant. Scan the file for
    // the marker, keeping an overlap so it can't hide on a chunk boundary.
    std::string decoder = pick_onnx(model_dir, "decoder");
    FILE* f = fopen(decoder.c_str(), "rb");
    if (!f) return "decoder ONNX not found: " + decoder;
    const std::string marker = "nemo_parakeet_unified_streaming";
    const size_t overlap = marker.size() - 1;
    std::string carry;
    char chunk[65536];
    size_t got;
    bool unified = false;
    while ((got = fread(chunk, 1, sizeof(chunk), f)) > 0) {
        std::string window = carry;
        window.append(chunk, got);
        if (window.find(marker) != std::string::npos) { unified = true; break; }
        carry = window.size() > overlap ? window.substr(window.size() - overlap)
                                        : window;
    }
    fclose(f);
    if (unified)
        return "decoder is the nemo_parakeet_unified_streaming variant, which "
               "ignores the language prompt (multilingual selection unavailable)";

    // (2) tokens.txt must be a multilingual-sized vocab. Floor well above the
    // 1025-line English vocab to also catch a truncated download.
    std::string tokens = model_dir + "/tokens.txt";
    FILE* tf = fopen(tokens.c_str(), "rb");
    if (!tf) return "tokens.txt not found: " + tokens;
    int lines = 0, c;
    while ((c = fgetc(tf)) != EOF) if (c == '\n') ++lines;
    fclose(tf);
    const int kMinMultilingualTokens = 8000;
    if (lines < kMinMultilingualTokens) {
        char msg[192];
        snprintf(msg, sizeof(msg),
                 "tokens.txt has only %d lines (expected a multilingual vocab "
                 ">= %d); wrong or truncated model", lines, kMinMultilingualTokens);
        return msg;
    }
    return "";
}

// Stream one buffer of samples through a fresh online stream and return the
// final text. `language` is an ISO code ("en"/"ja") or "auto"/""; it is applied
// ONLY via the per-stream "language" option (sherpa maps it to prompt_index;
// the English-only export ignores it).
static std::string decode_online_segment(
        const SherpaOnnxOnlineRecognizer* rec,
        const std::string& language,
        int32_t sample_rate,
        const float* samples, int32_t n) {
    if (!rec || !samples || n <= 0) return "";

    const SherpaOnnxOnlineStream* stream = SherpaOnnxCreateOnlineStream(rec);
    if (!stream) return "";

    if (!language.empty()) {
        SherpaOnnxOnlineStreamSetOption(stream, "language", language.c_str());
    }

    // Feed in fixed 200ms chunks; sherpa buffers internally and emits on its
    // own chunk_shift schedule, so the feed size is independent of the model.
    const int32_t kChunk = 3200; // 200ms @ 16kHz
    for (int32_t off = 0; off < n; off += kChunk) {
        int32_t len = (off + kChunk <= n) ? kChunk : (n - off);
        SherpaOnnxOnlineStreamAcceptWaveform(stream, sample_rate, samples + off, len);
        while (SherpaOnnxIsOnlineStreamReady(rec, stream)) {
            SherpaOnnxDecodeOnlineStream(rec, stream);
        }
    }

    // A cache-aware model needs trailing lookahead to flush the final sub-chunk;
    // without it the last word of a short (< chunk) segment is dropped. Feed
    // 0.5s of silence before signalling end-of-input.
    std::vector<float> tail(sample_rate / 2, 0.0f);
    SherpaOnnxOnlineStreamAcceptWaveform(stream, sample_rate, tail.data(),
                                         static_cast<int32_t>(tail.size()));
    SherpaOnnxOnlineStreamInputFinished(stream);
    while (SherpaOnnxIsOnlineStreamReady(rec, stream)) {
        SherpaOnnxDecodeOnlineStream(rec, stream);
    }

    std::string text;
    const SherpaOnnxOnlineRecognizerResult* r =
        SherpaOnnxGetOnlineStreamResult(rec, stream);
    if (r && r->text) text = r->text;
    SherpaOnnxDestroyOnlineRecognizerResult(r);
    SherpaOnnxDestroyOnlineStream(stream);
    return text;
}

// =========================================================================
// ARGUMENT PARSING
// =========================================================================

struct DaemonArgs {
    std::string model_dir;
    std::string language = "en";
    std::string join_language;
    std::string vad_model_path;
    std::string engine = "nemo_transducer"; // nemo_transducer | qwen3 | nemotron_ml
};

static bool parse_args(int argc, char** argv, DaemonArgs& args) {
    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--model") == 0 && i + 1 < argc) {
            args.model_dir = argv[++i];
        } else if (strcmp(argv[i], "--language") == 0 && i + 1 < argc) {
            args.language = argv[++i];
        } else if (strcmp(argv[i], "--join-language") == 0 && i + 1 < argc) {
            args.join_language = argv[++i];
        } else if (strcmp(argv[i], "--vad-model") == 0 && i + 1 < argc) {
            args.vad_model_path = argv[++i];
        } else if (strcmp(argv[i], "--engine") == 0 && i + 1 < argc) {
            args.engine = argv[++i];
        }
    }

    if (args.model_dir.empty()) {
        log_error("Missing required argument: --model <directory>");
        return false;
    }

    if (args.engine != "nemo_transducer" && args.engine != "qwen3" &&
        args.engine != "nemotron_ml") {
        log_error(("Unknown --engine value: " + args.engine).c_str());
        return false;
    }

    return true;
}

// =========================================================================
// MAIN
// =========================================================================

int main(int argc, char** argv) {
#ifdef _WIN32
    // Set stdin/stdout to binary mode to prevent CRLF mangling
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif

    // ── Parse arguments ──
    DaemonArgs args;
    if (!parse_args(argc, argv, args)) {
        fprintf(stdout, "{\"status\":\"error\",\"error\":\"Invalid arguments\"}\n");
        fflush(stdout);
        return 2;
    }

    const bool is_qwen3 = (args.engine == "qwen3");
    const bool is_online = (args.engine == "nemotron_ml");

    if (is_online && !sherpa_at_least_1_13_3()) {
        log_error("Engine nemotron_ml requires sherpa-onnx >= 1.13.3 "
                  "(older builds silently ignore the per-stream language option)");
        fprintf(stdout, "{\"status\":\"error\",\"error\":\"sherpa-onnx too old for nemotron_ml\"}\n");
        fflush(stdout);
        return 1;
    }

    log_info(("Engine: " + args.engine).c_str());
    log_info(("Model directory: " + args.model_dir).c_str());
    if (is_qwen3 || is_online) {
        log_info(("Language: " + args.language).c_str());
    } else {
        log_info("Language: auto-detected by Parakeet TDT (not applied)");
    }

    // ── Build file paths (kept alive for the recognizer's lifetime) ──
    // Parakeet (nemo_transducer) path
    std::string encoder_path   = args.model_dir + "/encoder.int8.onnx";
    std::string decoder_path   = args.model_dir + "/decoder.int8.onnx";
    std::string joiner_path    = args.model_dir + "/joiner.int8.onnx";
    std::string tokens_path    = args.model_dir + "/tokens.txt";
    // Qwen3 path (tokenizer is a DIRECTORY, not a tokens.txt file)
    std::string q_conv_path    = pick_onnx(args.model_dir, "conv_frontend");
    std::string q_encoder_path = pick_onnx(args.model_dir, "encoder");
    std::string q_decoder_path = pick_onnx(args.model_dir, "decoder");
    std::string q_tokenizer    = args.model_dir + "/tokenizer";

    // Qwen3 language hint (English name; empty => auto)
    std::string qwen3_lang = is_qwen3 ? qwen3_language_name(args.language) : "";
    // Inter-segment joiner: "" for CJK, " " otherwise
    const std::string join_language = args.join_language.empty() ? args.language : args.join_language;
    const char* segment_join = is_no_space_language(join_language) ? "" : " ";

    // ── Configure the offline recognizer (unused when --engine nemotron_ml,
    //    which builds an online recognizer below instead) ──
    SherpaOnnxOfflineRecognizerConfig config;
    memset(&config, 0, sizeof(config));

    if (is_qwen3) {
        // Qwen3-ASR offline branch — autoregressive decoder, separate config sub-struct.
        // ABI note: SherpaOnnxOfflineQwen3ASRModelConfig must match the EXACT sherpa-onnx
        // tag the shipped DLL is built from (the struct gained a `hotwords` field in
        // v1.12.36 / #3468). Build header + DLL from one pinned tag (>= v1.13.3).
        config.model_config.qwen3_asr.conv_frontend = q_conv_path.c_str();
        config.model_config.qwen3_asr.encoder       = q_encoder_path.c_str();
        config.model_config.qwen3_asr.decoder       = q_decoder_path.c_str();
        config.model_config.qwen3_asr.tokenizer     = q_tokenizer.c_str();
        // IMPORTANT: the C API struct is memset to 0, and the C wrapper does NOT
        // re-apply the C++ struct defaults — so every numeric field below must be
        // set explicitly or OfflineQwen3ASRModelConfig::Validate() rejects the
        // config (it requires max_total_len > 0 and max_new_tokens > 0) and
        // recognizer creation fails. Values mirror sherpa's C++ defaults, except
        // max_new_tokens which we raise from 128 so dense Japanese isn't truncated.
        config.model_config.qwen3_asr.max_total_len  = 512;
        config.model_config.qwen3_asr.max_new_tokens = 256;
        config.model_config.qwen3_asr.temperature    = 1e-6f;  // ~greedy
        config.model_config.qwen3_asr.top_p          = 0.8f;
        config.model_config.qwen3_asr.seed           = 42;
        // hotwords intentionally empty (issue #140: hotwords make Qwen3 spew the
        // hotword list on phonetically-similar audio).
        config.model_config.tokens     = "";   // NO tokens.txt for Qwen3
        config.model_config.num_threads = 4;   // autoregressive decode is CPU-bound
        config.model_config.debug       = 0;
        // Empty (not null) model_type: sherpa selects Qwen3 from the populated
        // sub-struct; "" avoids any strlen() on a null pointer in the C wrapper.
        config.model_config.model_type  = "";
        config.decoding_method          = "greedy_search";
    } else {
        // Parakeet TDT (nemo_transducer) — unchanged.
        config.model_config.transducer.encoder = encoder_path.c_str();
        config.model_config.transducer.decoder = decoder_path.c_str();
        config.model_config.transducer.joiner  = joiner_path.c_str();
        config.model_config.tokens             = tokens_path.c_str();
        config.model_config.num_threads        = 2;
        config.model_config.debug              = 0;
        config.model_config.model_type         = "nemo_transducer";
    }

    // ── Select provider / create recognizer ──
    std::string active_provider;
    const SherpaOnnxOfflineRecognizer* recognizer = nullptr;
    const SherpaOnnxOnlineRecognizer*  online_recognizer = nullptr;

    if (is_online) {
        // Nemotron-3.5 streaming: online cache-aware transducer. CPU only for
        // now (DirectML correctness on this graph is unverified). The offline
        // `config` built above is unused on this path; language is selected
        // per-stream at decode time, not here.
        //
        // Hard-refuse a unified-variant decoder or a wrong/truncated vocab
        // rather than silently emitting wrong-language garbage (see review).
        std::string verr = nemotron_validate_model(args.model_dir);
        if (!verr.empty()) {
            log_error(("Nemotron model validation failed: " + verr).c_str());
            fprintf(stdout, "{\"status\":\"error\",\"error\":\"Invalid model for nemotron_ml\"}\n");
            fflush(stdout);
            return 1;
        }
        online_recognizer = create_online_recognizer(
            encoder_path, decoder_path, joiner_path, tokens_path);
        if (online_recognizer) {
            active_provider = "cpu";
            log_info("Online Nemotron recognizer initialized (cpu)");
        } else {
            log_error("Failed to create online (Nemotron) recognizer");
            fprintf(stdout, "{\"status\":\"error\",\"error\":\"Failed to load model\"}\n");
            fflush(stdout);
            return 1;
        }
    } else if (is_qwen3) {
        // CPU-only by design — do NOT attempt DirectML (silent-garbage risk).
        config.model_config.provider = "cpu";
        log_info("Qwen3: forcing CPU provider (DirectML disabled by design)");
        recognizer = SherpaOnnxCreateOfflineRecognizer(&config);
        if (recognizer) {
            active_provider = "cpu";
            log_info("CPU provider initialized successfully");
        } else {
            log_error("Failed to create Qwen3 offline recognizer");
            fprintf(stdout, "{\"status\":\"error\",\"error\":\"Failed to load model\"}\n");
            fflush(stdout);
            return 1;
        }
    } else {
        // Parakeet: try DirectML, fall back to CPU — unchanged.
        config.model_config.provider = "directml";
        log_info("Attempting DirectML provider...");

        recognizer = SherpaOnnxCreateOfflineRecognizer(&config);

        if (recognizer) {
            active_provider = "directml";
            log_info("DirectML provider initialized successfully");
        } else {
            log_warn("DirectML failed, falling back to CPU provider...");
            config.model_config.provider = "cpu";
            recognizer = SherpaOnnxCreateOfflineRecognizer(&config);

            if (recognizer) {
                active_provider = "cpu";
                log_info("CPU provider initialized successfully");
            } else {
                log_error("Failed to create offline recognizer with any provider");
                fprintf(stdout, "{\"status\":\"error\",\"error\":\"Failed to load model\"}\n");
                fflush(stdout);
                return 1;
            }
        }
    }

    // ── Initialize VAD (optional) ──
    const SherpaOnnxVoiceActivityDetector* vad = nullptr;
    bool use_vad = false;

    if (!args.vad_model_path.empty()) {
        SherpaOnnxVadModelConfig vad_config;
        memset(&vad_config, 0, sizeof(vad_config));

        vad_config.silero_vad.model       = args.vad_model_path.c_str();
        vad_config.silero_vad.threshold    = 0.5f;
        vad_config.silero_vad.min_silence_duration = 0.5f;
        vad_config.silero_vad.min_speech_duration   = 0.25f;
        vad_config.silero_vad.window_size  = 512;
        vad_config.sample_rate             = 16000;
        vad_config.num_threads             = 1;
        vad_config.debug                   = 0;
        // Use CPU for VAD (lightweight model, not worth GPU overhead)
        vad_config.provider                = "cpu";

        vad = SherpaOnnxCreateVoiceActivityDetector(&vad_config, 30.0f);
        if (vad) {
            use_vad = true;
            log_info("VAD model loaded successfully");
        } else {
            log_warn("Failed to load VAD model, proceeding without VAD");
        }
    }

    // ── Send READY signal ──
    fprintf(stdout, "{\"status\":\"ready\",\"provider\":\"%s\"}\n", active_provider.c_str());
    fflush(stdout);

    log_info("Daemon is ready, entering main loop...");

    // Near-silent segments fed to Qwen3 with a language hint produce hallucinated
    // filler (#3509). Skip any VAD segment quieter than this RMS (effectively
    // digital silence the VAD let through). Qwen3 only — Parakeet behaviour is
    // unchanged.
    const float kSilenceRmsFloor = 1e-3f;

    // ── Main loop ──
    std::string line;
    while (std::getline(std::cin, line)) {
        // Strip trailing \r if present (Windows CRLF)
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }

        if (line.empty()) continue;

        // Check for quit command
        std::string command = json_get_string(line, "command");
        if (command == "quit") {
            log_info("Received quit command, shutting down...");
            break;
        }

        // Get audio path
        std::string audio_path = json_get_string(line, "audio_path");
        if (audio_path.empty()) {
            log_warn(("Unknown command or missing audio_path: " + line).c_str());
            fprintf(stdout, "{\"error\":\"Missing audio_path\"}\n");
            fflush(stdout);
            continue;
        }

        log_info(("Processing: " + audio_path).c_str());

        auto start_time = std::chrono::steady_clock::now();

        // Read the WAV file using sherpa-onnx's built-in reader. On Windows,
        // first load bytes through Win32 wide-path APIs so UTF-8 JSON paths
        // work even when the CRT/sherpa narrow fopen path cannot resolve them.
        std::vector<char> wave_bytes;
        const SherpaOnnxWave* wave = read_wave_file(audio_path, wave_bytes);
        if (!wave) {
            log_error(("Failed to read WAV file: " + audio_path).c_str());
            fprintf(stdout, "{\"error\":\"Failed to read audio file\"}\n");
            fflush(stdout);
            continue;
        }

        char buf[128];
        snprintf(buf, sizeof(buf), "Audio: %d samples at %d Hz (%.1f sec)",
                 wave->num_samples, wave->sample_rate,
                 static_cast<float>(wave->num_samples) / wave->sample_rate);
        log_info(buf);

        // Get audio data (sherpa-onnx reader already returns 16kHz mono)
        const float* samples = wave->samples;
        int32_t num_samples = wave->num_samples;
        int32_t sample_rate = wave->sample_rate;

        std::string full_text;
        bool stream_error = false;

        if (use_vad && vad && !is_online) {
            // ── VAD-based chunked transcription (offline engines only) ──
            // Feed audio through VAD to get speech segments, then transcribe each.
            // The online Nemotron engine deliberately SKIPS VAD: a cache-aware
            // streaming model carries encoder state across chunks, so segmenting
            // on silence and cold-starting each segment drops the start/end of
            // utterances (measured). It streams the whole file in one pass below.

            SherpaOnnxVoiceActivityDetectorReset(vad);

            const int window_size = 512;

            // Feed all audio through VAD in windows
            for (int32_t offset = 0; offset + window_size <= num_samples; offset += window_size) {
                SherpaOnnxVoiceActivityDetectorAcceptWaveform(vad, samples + offset, window_size);
            }

            // Flush any remaining audio
            SherpaOnnxVoiceActivityDetectorFlush(vad);

            // Process each detected speech segment
            while (!SherpaOnnxVoiceActivityDetectorEmpty(vad)) {
                const SherpaOnnxSpeechSegment* segment =
                    SherpaOnnxVoiceActivityDetectorFront(vad);

                if (segment && segment->n > 0) {
                    // Qwen3 silence guard: skip near-silent segments to avoid
                    // hallucinated filler (#3509). Parakeet is unaffected.
                    bool skip_segment = is_qwen3 &&
                        segment_rms(segment->samples, segment->n) < kSilenceRmsFloor;

                    if (!skip_segment) {
                        // Transcribe this segment (offline: Parakeet / Qwen3).
                        // The online engine never reaches here (it skips VAD).
                        const SherpaOnnxOfflineStream* stream =
                            SherpaOnnxCreateOfflineStream(recognizer);

                        if (!stream) {
                            log_error("Failed to create offline stream (VAD branch)");
                            SherpaOnnxDestroySpeechSegment(segment);
                            SherpaOnnxVoiceActivityDetectorPop(vad);
                            SherpaOnnxFreeWave(wave);
                            fprintf(stdout,
                                    "{\"error\":\"Failed to create offline stream (provider=%s)\"}\n",
                                    active_provider.c_str());
                            fflush(stdout);
                            stream_error = true;
                            break;
                        }

                        // Qwen3 takes the language as a per-stream option (English name).
                        if (is_qwen3 && !qwen3_lang.empty()) {
                            SherpaOnnxOfflineStreamSetOption(stream, "language", qwen3_lang.c_str());
                        }

                        SherpaOnnxAcceptWaveformOffline(stream, sample_rate,
                                                        segment->samples, segment->n);
                        SherpaOnnxDecodeOfflineStream(recognizer, stream);

                        const SherpaOnnxOfflineRecognizerResult* result =
                            SherpaOnnxGetOfflineStreamResult(stream);

                        if (result && result->text && result->text[0] != '\0') {
                            if (!full_text.empty()) full_text += segment_join;
                            full_text += result->text;
                        }

                        SherpaOnnxDestroyOfflineRecognizerResult(result);
                        SherpaOnnxDestroyOfflineStream(stream);
                    }
                }

                SherpaOnnxDestroySpeechSegment(segment);
                SherpaOnnxVoiceActivityDetectorPop(vad);
            }

            if (stream_error) {
                continue;
            }
        } else if (is_online) {
            // ── Online streaming (Nemotron): always full-file, one stream ──
            // This is the online engine's only decode path — it streams the
            // entire clip through a single cache-aware online stream so encoder
            // state carries across chunks (VAD is intentionally skipped above).
            full_text = decode_online_segment(online_recognizer, args.language,
                                              sample_rate, samples, num_samples);
        } else {
            // ── Full-file transcription (no VAD) ──
            const SherpaOnnxOfflineStream* stream =
                SherpaOnnxCreateOfflineStream(recognizer);

            if (!stream) {
                log_error("Failed to create offline stream (no-VAD branch)");
                SherpaOnnxFreeWave(wave);
                fprintf(stdout,
                        "{\"error\":\"Failed to create offline stream (provider=%s)\"}\n",
                        active_provider.c_str());
                fflush(stdout);
                continue;
            }

            if (is_qwen3 && !qwen3_lang.empty()) {
                SherpaOnnxOfflineStreamSetOption(stream, "language", qwen3_lang.c_str());
            }

            SherpaOnnxAcceptWaveformOffline(stream, sample_rate,
                                            samples, num_samples);
            SherpaOnnxDecodeOfflineStream(recognizer, stream);

            const SherpaOnnxOfflineRecognizerResult* result =
                SherpaOnnxGetOfflineStreamResult(stream);

            if (result && result->text) {
                full_text = result->text;
            }

            SherpaOnnxDestroyOfflineRecognizerResult(result);
            SherpaOnnxDestroyOfflineStream(stream);
        }

        // Free the WAV data
        SherpaOnnxFreeWave(wave);

        auto end_time = std::chrono::steady_clock::now();
        auto duration_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
            end_time - start_time).count();

        // Trim leading/trailing whitespace from transcription
        size_t start = full_text.find_first_not_of(" \t\n\r");
        size_t end = full_text.find_last_not_of(" \t\n\r");
        if (start != std::string::npos && end != std::string::npos) {
            full_text = full_text.substr(start, end - start + 1);
        } else if (start == std::string::npos) {
            full_text.clear();
        }

        snprintf(buf, sizeof(buf), "Transcription complete: %zu chars in %lldms",
                 full_text.length(), (long long)duration_ms);
        log_info(buf);

        // Write result JSON to stdout
        fprintf(stdout, "{\"text\":\"%s\",\"duration_ms\":%lld}\n",
                json_escape(full_text).c_str(), (long long)duration_ms);
        fflush(stdout);
    }

    // ── Cleanup ──
    log_info("Shutting down...");

    if (vad) {
        SherpaOnnxDestroyVoiceActivityDetector(vad);
    }
    if (online_recognizer) SherpaOnnxDestroyOnlineRecognizer(online_recognizer);
    if (recognizer)        SherpaOnnxDestroyOfflineRecognizer(recognizer);

    log_info("Clean exit");
    return 0;
}
