//
//  ModelSizeLabelTests.swift
//  hyperwhisperTests
//
//  Pins each Model Library size label to what the app really downloads. The
//  Qwen3 label read "~1.3 GB" (a copy of the Nemotron string) while the app
//  pulled 4.19 GB, and the Nemotron and Whisper labels had drifted the same way.
//
//  The byte counts below were measured from the Hugging Face API on 2026-10-07
//  (`/api/models/<repo>/tree/main/<path>?recursive=1`, LFS size), over the files
//  the pinned code fetches. If a repo republishes its files, measure again and
//  change the label and the figure here together.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct ModelSizeLabelTests {

    /// A label may round, but it must stay within 10% of the real download.
    private static let tolerance = 0.10

    private static func expectLabel(_ label: String, matches bytes: Int64, _ name: String) {
        // The Local API reads the same label, so parse it the way the Local API does.
        guard let mb = ModelsEndpoint.parseSizeMB(label) else {
            Issue.record("\(name): the Local API cannot parse the label \"\(label)\"")
            return
        }
        let labelBytes = mb * 1_000_000
        let error = abs(labelBytes - Double(bytes)) / Double(bytes)
        #expect(
            error <= tolerance,
            "\(name): label \"\(label)\" is \(Int(error * 100))% from the measured \(bytes) bytes"
        )
    }

    // MARK: - Qwen3

    /// Every file under `f32/` in `FluidInference/qwen3-asr-0.6b-coreml` on
    /// 2026-10-07. Each `.mlpackage` and the old encoder hold a `weight.bin`,
    /// which `downloadRepo`'s keep-every-`.bin` rule let through.
    private static let qwen3F32Files: [(path: String, bytes: Int64)] = [
        ("f32/metadata.json", 1_802),
        ("f32/qwen3_asr_audio_encoder.mlmodelc/analytics/coremldata.bin", 243),
        ("f32/qwen3_asr_audio_encoder.mlmodelc/coremldata.bin", 443),
        ("f32/qwen3_asr_audio_encoder.mlmodelc/metadata.json", 2_059),
        ("f32/qwen3_asr_audio_encoder.mlmodelc/model.mil", 241_578),
        ("f32/qwen3_asr_audio_encoder.mlmodelc/weights/weight.bin", 372_796_928),
        ("f32/qwen3_asr_audio_encoder.mlpackage/Data/com.apple.CoreML/model.mlmodel", 200_798),
        ("f32/qwen3_asr_audio_encoder.mlpackage/Data/com.apple.CoreML/weights/weight.bin", 372_796_928),
        ("f32/qwen3_asr_audio_encoder.mlpackage/Manifest.json", 617),
        ("f32/qwen3_asr_audio_encoder_v2.mlmodelc/analytics/coremldata.bin", 243),
        ("f32/qwen3_asr_audio_encoder_v2.mlmodelc/coremldata.bin", 385),
        ("f32/qwen3_asr_audio_encoder_v2.mlmodelc/metadata.json", 2_088),
        ("f32/qwen3_asr_audio_encoder_v2.mlmodelc/model.mil", 726_626),
        ("f32/qwen3_asr_audio_encoder_v2.mlmodelc/weights/weight.bin", 372_798_784),
        ("f32/qwen3_asr_audio_encoder_v2.mlpackage/Data/com.apple.CoreML/model.mlmodel", 633_397),
        ("f32/qwen3_asr_audio_encoder_v2.mlpackage/Data/com.apple.CoreML/weights/weight.bin", 372_798_784),
        ("f32/qwen3_asr_audio_encoder_v2.mlpackage/Manifest.json", 617),
        ("f32/qwen3_asr_decoder_stateful.mlmodelc/analytics/coremldata.bin", 243),
        ("f32/qwen3_asr_decoder_stateful.mlmodelc/coremldata.bin", 2_347),
        ("f32/qwen3_asr_decoder_stateful.mlmodelc/metadata.json", 18_785),
        ("f32/qwen3_asr_decoder_stateful.mlmodelc/model.mil", 905_438),
        ("f32/qwen3_asr_decoder_stateful.mlmodelc/weights/weight.bin", 1_192_436_160),
        ("f32/qwen3_asr_decoder_stateful.mlpackage/Data/com.apple.CoreML/model.mlmodel", 894_694),
        ("f32/qwen3_asr_decoder_stateful.mlpackage/Data/com.apple.CoreML/weights/weight.bin", 1_192_436_160),
        ("f32/qwen3_asr_decoder_stateful.mlpackage/Manifest.json", 617),
        ("f32/qwen3_asr_embeddings.bin", 311_164_936),
        ("f32/vocab.json", 2_776_833),
    ]

    /// `downloadSubdirectory` asks the predicate about every path, and for a
    /// directory a `true` skips the whole subtree.
    private static func isKept(_ path: String) -> Bool {
        var prefix = ""
        for component in path.split(separator: "/") {
            prefix = prefix.isEmpty ? String(component) : prefix + "/" + component
            if Qwen3AsrModelManager.Constants.shouldSkipRemotePath(prefix) { return false }
        }
        return true
    }

    @Test func qwen3LabelMatchesTheTrimmedDownload() {
        Self.expectLabel(
            Qwen3AsrModelManager.Constants.sizeDescription,
            matches: Qwen3AsrModelManager.Constants.downloadBytes,
            "Qwen3 ASR"
        )
    }

    @Test func qwen3FilterKeepsExactlyTheBytesTheLabelClaims() {
        let kept = Self.qwen3F32Files.filter { Self.isKept($0.path) }
        let total = Self.qwen3F32Files.reduce(Int64(0)) { $0 + $1.bytes }
        #expect(kept.reduce(Int64(0)) { $0 + $1.bytes } == Qwen3AsrModelManager.Constants.downloadBytes)
        #expect(total == 4_194_437_729)
        #expect(kept.count == 13)
    }

    @Test func qwen3FilterKeepsEveryFileTheLoaderReads() {
        let keptPaths = Set(Self.qwen3F32Files.map(\.path).filter(Self.isKept))
        // `Qwen3AsrModels.modelsExist` and `load` in FluidAudio 0.15.2.
        for required in [
            "f32/qwen3_asr_audio_encoder_v2.mlmodelc/weights/weight.bin",
            "f32/qwen3_asr_audio_encoder_v2.mlmodelc/model.mil",
            "f32/qwen3_asr_audio_encoder_v2.mlmodelc/coremldata.bin",
            "f32/qwen3_asr_decoder_stateful.mlmodelc/weights/weight.bin",
            "f32/qwen3_asr_decoder_stateful.mlmodelc/model.mil",
            "f32/qwen3_asr_decoder_stateful.mlmodelc/coremldata.bin",
            "f32/qwen3_asr_embeddings.bin",
            "f32/vocab.json",
        ] {
            #expect(keptPaths.contains(required), "dropped \(required)")
        }
    }

    @Test func qwen3FilterSkipsSourcePackagesAndTheOldEncoder() {
        let skip = Qwen3AsrModelManager.Constants.shouldSkipRemotePath
        #expect(!skip("f32"))
        #expect(skip("f32/qwen3_asr_audio_encoder.mlmodelc"))
        #expect(skip("f32/qwen3_asr_audio_encoder.mlpackage"))
        #expect(skip("f32/qwen3_asr_audio_encoder_v2.mlpackage"))
        #expect(skip("f32/qwen3_asr_decoder_stateful.mlpackage"))
        #expect(skip("int8"))
        #expect(skip("README.md"))
    }

    // MARK: - Nemotron

    @Test func nemotronLabelsMatchTheirDownloads() {
        // `downloadSubdirectory` of FluidInference/Nemotron-3.5-ASR-Streaming-
        // Multilingual-0.6b-CoreML at `<variant>/2240ms`, the only chunk size the app uses.
        Self.expectLabel(NemotronModelManager.Constants.latinSize, matches: 612_042_646, "Nemotron Latin")
        Self.expectLabel(NemotronModelManager.Constants.multilingualSize, matches: 664_846_846, "Nemotron Multilingual")
    }

    // MARK: - Parakeet

    @Test func parakeetLabelsMatchTheirDownloads() {
        Self.expectLabel(ParakeetModelManager.Constants.v2SizeDescription, matches: 464_413_250, "Parakeet v2")
        Self.expectLabel(ParakeetModelManager.Constants.v3SizeDescription, matches: 483_257_242, "Parakeet v3")
    }

    // MARK: - Whisper

    /// The ggml file each model downloads from ggerganov/whisper.cpp.
    private static let whisperFileBytes: [String: Int64] = [
        "ggml-tiny.bin": 77_691_713,
        "ggml-tiny.en.bin": 77_704_715,
        "ggml-base.bin": 147_951_465,
        "ggml-base.en.bin": 147_964_211,
        "ggml-small.bin": 487_601_967,
        "ggml-small.en.bin": 487_614_201,
        "ggml-medium.bin": 1_533_763_059,
        "ggml-medium.en.bin": 1_533_774_781,
        "ggml-large-v2.bin": 3_094_623_691,
        "ggml-large-v3.bin": 3_095_033_483,
        "ggml-large-v3-turbo.bin": 1_624_555_275,
    ]

    @Test func whisperLabelsAndByteCountsMatchTheirFiles() {
        #expect(!WhisperModelManager.allModels.isEmpty)
        for model in WhisperModelManager.allModels {
            guard let bytes = Self.whisperFileBytes[model.filename] else {
                Issue.record("no measured size for \(model.filename)")
                continue
            }
            Self.expectLabel(model.size, matches: bytes, model.filename)
            // `sizeInBytes` feeds the disk-usage total, so it gets the same bound.
            let error = abs(Double(model.sizeInBytes - bytes)) / Double(bytes)
            #expect(error <= Self.tolerance, "\(model.filename): sizeInBytes \(model.sizeInBytes) vs \(bytes)")
        }
    }

    // MARK: - Local API parser

    @Test func localAPIParserReadsATildeLabel() {
        #expect(ModelsEndpoint.parseSizeMB("~665 MB") == 665)
        #expect(ModelsEndpoint.parseSizeMB("474 MB") == 474)
        #expect(ModelsEndpoint.parseSizeMB("Built-in") == nil)
    }
}
