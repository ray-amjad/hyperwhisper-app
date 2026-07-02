# AGENTS.md

HyperWhisper macOS — Swift 5 + SwiftUI app, MVVM, macOS 14+ deployment target. Audio via AVFoundation (`AVAudioEngine`), menu-bar UI via `MenuBarExtra`.

## Project map

- `hyperwhisper/Managers/Transcription/` — `Coordinators/` (router), `Providers/Local/` + `Providers/Cloud/`, `Flows/`
- `hyperwhisper/Models/` — `CloudTranscriptionModels.swift`, `TranscriptionError.swift`, etc.
- `hyperwhisper/Managers/LocalAPI/` — in-app HTTP API endpoints (Local API)
- `hyperwhisper/Runtime/` — pre-built llama.cpp dylibs (built externally — see below)
- `hyperwhisper/CoreData/HyperWhisper.xcdatamodeld/` — Core Data model versions
- `references/cloudkit-schema-promotion.md` — full CloudKit promotion playbook + v2.33.0 incident

<important if="you need to build, run, or test the macOS app">

| Command | What it does |
|---|---|
| Open `hyperwhisper.xcodeproj` + Cmd+R | Run in Xcode (preferred) |
| `xcodebuild -project hyperwhisper.xcodeproj -scheme hyperwhisper -configuration Debug build` | CLI Debug build |
| `xcodebuild -project hyperwhisper.xcodeproj -scheme hyperwhisper -configuration Debug clean build` | Clean Debug build (use when files were deleted/renamed) |
| `open ~/Library/Developer/Xcode/DerivedData/hyperwhisper-*/Build/Products/Debug/HyperWhisper.app` | Launch the built app |

Dependencies resolve via Swift Package Manager (no manual install step).

Do not run the full `xcodebuild ... test` action or broad UI/screenshot/launch-performance tests for narrow macOS code changes unless the user explicitly asks for that level of verification or the change directly touches UI-test-covered behavior. For PR merge lanes and small Swift changes, prefer the strongest targeted build/typecheck that matches the touched code, plus specific unit tests only when they are clearly relevant. The macOS UI test suite is permission- and environment-sensitive and can produce noisy accessibility/appearance-mode failures unrelated to the change.
</important>

<important if="you are about to launch the macOS app to verify a change">

A stale "BUILD SUCCEEDED" from before your last edit is the common failure mode — you re-launch the app, the change isn't there, and you'll be confused.

1. Run `xcodebuild build` AFTER your final source edit (prefer `clean build` if anything was deleted/renamed — file-system-synchronized groups + incremental builds keep stale references).
2. Map worktree → DerivedData via WorkspacePath (see the `feedback_correct_worktree_debug_build` memory) — do NOT pick the latest `.app` by mtime.
3. Verify the bundle reflects your edits before opening: `stat -f '%Sm' "$APP/Contents/MacOS/HyperWhisper"` must be newer than your last source edit; `grep` the bundled `Localizable.strings` for new/removed keys.
</important>

<important if="you replaced any dylib in hyperwhisper/Runtime/ or rebuilt llama.cpp">

Verify deployment target — this is a CRITICAL silent crash class:

```bash
otool -l libggml-metal.0.*.dylib | grep -A 2 "minos"
```

`minos` MUST be `14.0`. A higher value (e.g. `26.0`) means the dylib was built with the wrong SDK and will `DYLD Symbol missing` abort at launch on older macOS BEFORE any app code runs — Sentry won't see it.

When rebuilding llama.cpp: `cmake -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0 ...`
</important>

<important if="you are touching observable state, managers, or the app's central state container">

- `@StateObject`, `@EnvironmentObject`, `@Published` for reactive UI updates
- Central `AppState` class manages navigation and UI state
- Managers are injected as environment objects for app-wide access
- `TranscriptionProvider` protocol abstracts local vs cloud backends — extend it rather than special-casing the call sites
</important>

<important if="you are modifying any Core Data entity in HyperWhisper.xcdatamodeld">

NEVER edit the current `.xcdatamodel` directly — always create a new version, then point `.xccurrentversion` at it:

```bash
cd hyperwhisper/CoreData/HyperWhisper.xcdatamodeld
cp -R HyperWhisper.xcdatamodel HyperWhisper_v<N>.xcdatamodel
/usr/libexec/PlistBuddy -c "Set :_XCCurrentVersionName HyperWhisper_v<N>.xcdatamodel" .xccurrentversion
```

Lightweight migration (automatic): new optional attributes, new entities, renaming with identifiers, default values on new required attributes.

Heavy migration (mapping model required): attribute type changes, moving attributes between entities, merge/split entities.
</important>

<important if="your Core Data change touches an entity in the `Cloud` configuration (currently `Vocabulary`)">

You MUST promote the new CloudKit schema from Development → Production BEFORE shipping the release. Otherwise every synced user hits a `CKPartialFailure` loop on next launch with `"Cannot create new type CD_X in production schema"` and sync silently fails.

Full playbook (`cktool` commands + CloudKit Console walkthrough + the v2.33.0 incident write-up): `references/cloudkit-schema-promotion.md`.
</important>

<important if="you are bridging a callback-based API to async/await with withCheckedContinuation or withCheckedThrowingContinuation">

Use Swift Atomics (`ManagedAtomic<Bool>`) to guard `continuation.resume(...)` whenever more than one path can call it (timer vs event, multi-fire callback, cancellation + completion). The pattern: `if finished.exchange(true, ordering: .acquiring) == false { continuation.resume(...) }`.

Lock-free, ~1–5 ns vs ~100–200 ns for `NSLock`, no deadlock risk. Reference implementations:
- `AudioFileConverter.swift` — `AVAssetWriter` callback protection
- `WhisperModelManager.swift` — `URLSession` delegate completion
</important>

<important if="you are adding or modifying `os.Logger` log statements">

- Use `os.Logger` everywhere; no `print()`.
- In closure contexts, explicit `self.` is required inside the interpolation: `logger.info("Path: \(self.recordingsFolder, privacy: .public)")`.
- Privacy-aware interpolation does NOT support nested interpolations with ternaries. Compute the display string first, then log a single interpolation.
- To debug an in-flight issue, prefer the `macos-log-analyzer` subagent — it runs `log show` predicates against unified logs (see the `macos-log-debugging` memory for working predicates).
</important>
