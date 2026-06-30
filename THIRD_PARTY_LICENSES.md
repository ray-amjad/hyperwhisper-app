# Third-Party Licenses

HyperWhisper is licensed under Apache-2.0 (see [`LICENSE`](./LICENSE)). It ships
with, links against, or bundles the third-party components listed below. Each
remains under its own license and copyright; this file is provided for
attribution. Where a component bundles its own `LICENSE`/`NOTICE`, that file
governs.

Most dependencies are MIT or Apache-2.0. The non-permissive or
attribution-required exceptions are called out under **Notable / attribution
required**.

---

## Notable / attribution required

| Component | License | Notes |
|---|---|---|
| Silero VAD (`silero_vad.onnx` model, Windows) | **CC-BY-4.0** | Voice-activity-detection model. Attribution required. https://github.com/snakers4/silero-vad |
| uniffi (Rust FFI bindings generator) | **MPL-2.0** | Build/codegen tooling for `shared-core-rs`. https://github.com/mozilla/uniffi-rs |
| mediaremote-adapter (macOS) | **BSD-3-Clause** | https://github.com/jonasberge/mediaremote-adapter |
| sherpa-onnx (Windows native DLLs) | **Apache-2.0** | https://github.com/k2-fsa/sherpa-onnx |
| Sparkle (macOS auto-update) | **MIT** | Bundles sub-components under MIT/other permissive terms. https://github.com/sparkle-project/Sparkle |

### AI models — downloaded at runtime, not redistributed

HyperWhisper fetches speech-recognition model weights at runtime; **no model
weights are vendored in this repository.** Each model's own license governs your
download, independently of this repo's Apache-2.0:

| Model | License | Notes |
|---|---|---|
| NVIDIA Parakeet TDT 0.6b (v2/v3, `parakeet_tdt_ctc`) | **CC-BY-4.0** | Fetched from Hugging Face at runtime. Credit NVIDIA, link the license, indicate changes. The ONNX builds (Windows, via k2-fsa/`csukuangfj`) and CoreML builds (macOS, via FluidInference) are CC-BY-4.0 derivatives of the NVIDIA originals. https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2 · https://creativecommons.org/licenses/by/4.0/ |
| Whisper models | MIT | Fetched at runtime. https://github.com/openai/whisper |

The `parakeet-engine` binaries under
`app/windows/HyperWhisper/Resources/parakeet-engine/` are **first-party** (built
from the open `tools/parakeet-engine/`, Apache-2.0) plus redistributable
dependencies only — sherpa-onnx (Apache-2.0), ONNX Runtime + DirectML (MIT),
NAudio (MIT), and `silero_vad.onnx` (CC-BY-4.0). No Parakeet weights are bundled.

---

## macOS app (`app/macos`)

### Swift Package Manager — direct

| Package | License | Author |
|---|---|---|
| HotKey | MIT | Sam Soffes |
| async-http-client | Apache-2.0 | Apple / SSWG |
| ZIPFoundation | MIT | Thomas Zoechling |
| sentry-cocoa | MIT | Sentry |
| swift-atomics | Apache-2.0 | Apple |
| FlyingFox | MIT | Simon Whitty |
| AXSwift | MIT | Tyler Mandry et al. |
| KeyboardShortcuts | MIT | Sindre Sorhus |
| KeySender | MIT | Sindre Sorhus |
| LaunchAtLogin-Modern | MIT | Sindre Sorhus |
| SelectedTextKit | MIT | — |
| Jinja | MIT | Jinja (Swift) contributors |
| swift-transformers | MIT | Hugging Face |
| WhisperKit | MIT | Argmax, Inc. |
| swift-argument-parser | Apache-2.0 | Apple |

### Swift Package Manager — transitive (selected)

Apple / Swift Server ecosystem, all **Apache-2.0**: swift-algorithms,
swift-asn1, swift-async-algorithms, swift-certificates, swift-collections,
swift-crypto, swift-http-structured-headers, swift-http-types, swift-log,
swift-nio (+ -extras, -http2, -ssl, -transport-services), swift-numerics,
swift-service-lifecycle, swift-system.

### Vendored native libraries

| Component | License | Source |
|---|---|---|
| llama.cpp / ggml dylibs (libllama, libmtmd, libggml-base, libggml-metal, libggml-cpu, libggml-blas) | MIT | https://github.com/ggml-org/llama.cpp |
| whisper.cpp | MIT | https://github.com/ggml-org/whisper.cpp |

---

## Windows app (`app/windows`)

NuGet packages — all **MIT** unless noted:

| Package | License | Author |
|---|---|---|
| NAudio | MIT | Mark Heath |
| Whisper.net (+ Runtime, Runtime.NoAvx, Runtime.Vulkan) | MIT | Sandro Hawke / const-me upstream |
| LLamaSharp (+ Backend.Cpu, Backend.Cuda12) | MIT | SciSharp / LLamaSharp contributors |
| System.Management | MIT | Microsoft |
| GregsStack.InputSimulatorStandard | MIT | Gregory Morse |
| CommunityToolkit.Mvvm | MIT | Microsoft |
| Microsoft.EntityFrameworkCore.Sqlite / .Design | MIT | Microsoft |
| Microsoft.AspNetCore.App (framework reference) | MIT | Microsoft |
| Sentry (.NET) | MIT | Sentry |
| NetSparkleUpdater.SparkleUpdater | MIT | NetSparkle contributors |

Vendored under `Resources/parakeet-engine/`: sherpa-onnx DLLs (Apache-2.0),
ONNX Runtime + DirectML (MIT, Microsoft), NAudio (MIT), `silero_vad.onnx`
(CC-BY-4.0), and the first-party `parakeet-engine.*` (Apache-2.0, built from
`tools/parakeet-engine/`). Also `hyperwhisper_core.dll` (first-party, built from
`shared-core-rs`). See "AI models" above for the runtime-downloaded Parakeet
weights (CC-BY-4.0, not vendored).

---

## Shared Rust core (`shared-core-rs`)

First-party crates (`hw-core`, `hw-phonetic`, `hw-text`, `hw-net`, `hw-license`,
`hw-backup`, `hw-catalog`) are part of this repository (Apache-2.0). Third-party
crates: **uniffi** (MPL-2.0); serde, serde_json, thiserror, clap, askama,
cargo_metadata and their transitive deps (dual **MIT / Apache-2.0**).

---

## Web (`nextjs`) and backend (`hyperwhisper-cloud`)

All Node.js / npm dependencies use permissive licenses (**MIT**, **Apache-2.0**,
or **ISC**). See each project's `package.json` for the authoritative dependency
list. Notable: Next.js, React, TanStack Query, tRPC, Drizzle ORM, Tailwind CSS,
better-auth, Zod (web); Hono, @upstash/redis, google-auth-library (backend) —
all MIT.

---

*This inventory is maintained on a best-effort basis. If you believe a component
is missing or misattributed, please open an issue.*
