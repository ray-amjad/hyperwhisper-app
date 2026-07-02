<div align="center">

# HyperWhisper

**Fast, private speech-to-text for macOS, Windows, and iOS.**

Press a key, speak, and your words appear as text in any app — transcribed
on-device by default, with an optional hosted Cloud for the highest-accuracy
models.

Created by [Ray Amjad](https://www.youtube.com/@RAmjad).

[Website](https://hyperwhisper.com) · [Docs](https://hyperwhisper.com/docs) · [Download](https://hyperwhisper.com) · [YouTube](https://www.youtube.com/@RAmjad)

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](./LICENSE)

</div>

---

## What this is

HyperWhisper is a dictation app: a global hotkey records your voice, transcribes
it (locally via Whisper / Parakeet, or via HyperWhisper Cloud), optionally
post-processes it with an LLM, and pastes the result wherever your cursor is.

This repository is the full source for the apps, the **Fly.io transcription
backend**, the marketing/license website, the documentation, and the shared
schemas — open-sourced under **Apache-2.0**.

## Fully open source — including the backend

Everything in HyperWhisper is open source under **Apache-2.0** — the macOS,
Windows, and iOS apps, the marketing/license website, the docs, the shared
schemas, **and the Fly.io transcription backend**
([`hyperwhisper-cloud/`](https://github.com/ray-amjad/hyperwhisper-app/tree/main/hyperwhisper-cloud)). Nothing is
closed source. **Local transcription runs entirely on your machine** with no
account and no network — clone it, build it, use it.

**HyperWhisper Cloud** is the one *paid* piece — the **managed, hosted instance**
we run for the largest, most accurate models. Its backend lives in this repo, so
you can read, audit, and run it yourself; metering stays **server-side**, so
building from source doesn't unlock our hosted Cloud for free. Everything else
(local models, post-processing, modes, vocabulary, backup/restore) works with no
account.

Cloud runs on **prepaid credits** — one balance for every provider. No per-provider
API keys to manage, no subscription, no juggling separate bills: top up once and
pay only for what you transcribe. [Buy credits](https://hyperwhisper.com), activate
your Account Key in the app, and you're done — it funds development of everything
in this repo.

## Repository layout

| Path | What it is |
|---|---|
| `app/macos` | macOS app (Swift / SwiftUI) |
| `app/windows` | Windows app (C# / WPF / .NET 10) |
| `app/ios` | iOS app *(work in progress; not built here)* |
| `hyperwhisper-cloud` | Fly.io edge transcription service |
| `nextjs` | Marketing & account website (Next.js) |
| `mintlify-help` | Documentation site (Mintlify) |
| `shared-core-rs` | Rust shared core (UniFFI) used by the native apps |
| `shared-models` | Per-model metadata catalog |
| `shared-prompts` | Post-processing prompt templates |
| `shared-app-classification` | App-type & cloud-STT catalogs |
| `shared-backup` | Cross-platform backup schema |
| `shared-types` | Shared TypeScript types |
| `integrations` | External integrations (MCP, editor plugins) |
| `tools` | Native build scripts (parakeet-engine, sherpa-onnx) |

This repo uses a git submodule for the backend:

```bash
git clone --recurse-submodules https://github.com/ray-amjad/hyperwhisper-app.git
# or, after a plain clone:
git submodule update --init --recursive
```

## Building

Each app has its own setup; see the per-directory `AGENTS.md` for details.

- **macOS** — open `app/macos/hyperwhisper.xcodeproj` in Xcode and run (⌘R).
  Dependencies resolve via Swift Package Manager. Requires macOS 14+.
- **Windows** — `dotnet run -c Debug` from `app/windows/HyperWhisper/`. Requires
  the .NET 10 SDK on Windows. See `app/windows/AGENTS.md` for installer builds.
- **Web** — `npm install && npm run dev` in `nextjs/`. Local builds use
  `SKIP_ENV_VALIDATION=1 npm run build`.

Building official, signed/notarized releases additionally requires signing
credentials and a Sentry DSN, injected from CI secrets — these are not needed to
build and run from source.

## Third-party components

See [`THIRD_PARTY_LICENSES.md`](./THIRD_PARTY_LICENSES.md) for attribution of
bundled and linked dependencies (most MIT / Apache-2.0; a few require
attribution, e.g. the Silero VAD model under CC-BY-4.0).

## Contributing

Issues and pull requests are welcome. Please keep changes focused and follow the
conventions documented in the relevant `AGENTS.md`.

## License

Apache License 2.0 — see [`LICENSE`](./LICENSE). The code for everything,
including the backend, is covered by this license. The hosted **HyperWhisper
Cloud** *service* — our managed instance and prepaid credits — is a separate paid
offering, even though its backend source is included here under the same license.

The **"HyperWhisper" name, logo, and app icon are not covered by the Apache
license** — see [`TRADEMARKS.md`](./TRADEMARKS.md). You can fork and ship the
code, but under your own name and branding, not as the official HyperWhisper.
