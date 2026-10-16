# Spec: Windows on-device Whisper in an idle-exiting worker process

Status: DRAFT 4. The interview is not done. Items marked *(open)* wait for Ray.

- Repo: `ray-amjad/hyperwhisper-app`, base `main` (`4166f4b0` when this draft was written).
- Issue: #1194 (follow-up of #1122; PR #1193 closed).
- The PR body carries `Fixes #1194`. The issue keeps `needs-ray-spec` until the PR opens, so the auto-dev consumer does not build it too.

## 1. Problem

With on-device Whisper on Windows, the GPU stays at P0 (2460 MHz) and the app wakes about 110 times a second, also when idle.

Cause: Whisper.net 1.9.0 (ggml-vulkan) caches its Vulkan device once per process. Nothing in the managed API closes it.

Measured on an RTX 4060 (#1194):

- Model loaded, model disposed, or `FreeLibrary` on the native DLL: all stay P0, 105-112 wakes/s. A reload after `FreeLibrary` crashes.
- Destroy the raw Vulkan device: P8, 1.5 wakes/s.
- #1193 (in-process unload after 5 idle minutes): all 5 samples read P0.
- Process exit: P8 within 4 s.

Process exit is the only safe, measured way to P8. So the Vulkan work must run in a process that can exit.

## 2. Goal and acceptance (from #1122)

- A Release build, idle 6 minutes after a Whisper dictation: 5 `nvidia-smi` samples all read `P8`.
- A dictation after that returns text.
- A second dictation 10 s later loads no model (the worker is still warm).
- The app process itself does not load any Vulkan or ggml DLL while it is idle.

## 3. Non-goals

- No change to the Parakeet, Qwen3 or Nemotron daemon.
- No change to transcription quality, the decoding options or `CollapseRepetitionLoops` output.
- No change on macOS or Linux.
- No change to local post-processing. Note: `LLamaSharp.Backend.Vulkan` (`HyperWhisper.csproj` 76) can also hold a Vulkan device in the app. If a user has a local LLM loaded, the GPU can stay at P0 for that reason. That is a separate issue.
- No other Whisper.net change than the 1.9.0 to 1.9.1 step that D4 brings.

## 4. Code on main today

- `app/windows/HyperWhisper/Services/TranscriptionService.cs`
  - Static ctor forces runtime order Vulkan, Cpu, CpuNoAvx (74-81).
  - `InitializeAsync` (202): picks `GpuInfoService.GetBestGpu()`, loads with `UseGpu`, `UseFlashAttention`, `GpuDevice = AdapterIndex` (274-287).
  - `UnloadModelAsync` (371-389): waits for in-flight work, disposes the factory.
  - `TranscribeFileInternalAsync`: `PrepareAudioStream` (NAudio decode, mono, 16 kHz, trailing-silence trim), the builder options (607-675), an ARM64 thread rule (522-544), `IsModelFileLoadFault` (347), `CollapseRepetitionLoops` (1111).
- Shared instance: `Services/Transcription/TranscriptionRuntime.cs` `LocalProvider` (`new(isShared: true)`), used by the GUI and the Local API.
- Callers that test "loaded" as `IsInitialized && LoadedModelPath == path`: `MainViewModel.cs` 853, 1129, 1137, 1345, 1392; `MainViewModel.FileTranscription.cs` 584, 592; `TranscriptionRetryHandler.cs` 182; `OnboardingLiveAudioGateway.cs` 804-823; `TranscribeEndpoints.cs` 277 (type check) and 311-317.
- Parakeet precedent, `Services/ParakeetTranscriptionService.cs`: JSON lines over stdio, `{"status":"ready",...}` within 30 s, one auto-restart on a crash, no Job object.
- Packaging: `HyperWhisper.csproj` 59-66 (Whisper.net 1.9.0, Runtime, NoAvx, Vulkan); `setup-x64.iss` `[InstallDelete]` (61-62), `[Files]` (66), `KillProcess('parakeet-engine.exe')` (145); same shape in `setup-arm64.iss`; `build-release.ps1` publishes one project.
- Reusable core: `app/shared-dotnet/HyperWhisper.LocalInference/LocalWhisperService.cs` (Linux today). Whisper.net 1.9.1 with Runtime, Vulkan and Cuda12; no NoAvx.

## 5. Design (Option A, decided in D1)

### 5.1 The worker: `app/windows/HyperWhisper.WhisperWorker`

- A console app, `net10.0`, self-contained publish for `win-x64` and `win-arm64` (D3).
- Every Whisper path runs in the worker: x64 with a GPU, x64 CPU-only, and ARM64 (D3). The ARM64 thread rule (`TranscriptionService.cs` 522-544) moves into the worker.
- The worker references Whisper.net and its native runtimes. The app csproj removes Whisper.net, its 3 runtime packages and the static `RuntimeLibraryOrder` ctor. No Whisper DLL loads in the app process.
- It references `app/shared-dotnet/HyperWhisper.LocalInference` and uses `LocalWhisperService` for load and transcribe (D4). No second copy of the decoding code.
- It does the native work only: load the model, run the processor with the options the app sends, return segments and text.
- Audio preparation stays in the app. The app writes the 16 kHz mono WAV to a temp file and sends the path. So NAudio stays out of the worker.
- It exits on stdin EOF. So it dies with the app, also on a crash of the app.

### 5.1a Shared project changes (D4)

- `HyperWhisper.LocalInference.csproj` keeps only `Whisper.net` 1.9.1. The runtime PackageReferences move to each head.
- `HyperWhisper.Linux.csproj` adds `Whisper.net.Runtime`, `.Vulkan` and `.Cuda12` 1.9.1, so the Linux build output does not change. The Linux test projects add the same if they load a model.
- The worker csproj adds `Whisper.net.Runtime`, `.NoAvx` and `.Vulkan` 1.9.1. No Cuda12.
- Windows moves from Whisper.net 1.9.0 to 1.9.1.
- `LocalWhisperRequest` gets the 2 Windows deltas as options, default off so Linux does not change: `MaxLastTextTokensZero` (`WithMaxLastTextTokens(0)`) and the ARM64 thread rule.
- `LocalWhisperLoadOptions` gets a runtime order (Vulkan, Cpu, CpuNoAvx on Windows), set before the first factory.
- `LoadAsync` today returns `ModelLoadFailed` for every exception. It must split a native-runtime fault (`DllNotFoundException`, `BadImageFormatException`, `EntryPointNotFoundException` inside) into `RuntimeUnavailable`, so `IsModelFileLoadFault` (#1598) still holds across the process boundary.
- Tests: `HyperWhisper.LocalInference.Tests` covers the new options and the error split.

### 5.2 Protocol (JSON lines over stdio, like Parakeet)

- Worker to app, at start: `{"status":"ready","runtime":"vulkan|cpu|cpu-noavx","pid":n}`.
- `{"id":n,"cmd":"load","model":"<path>","gpuDevice":n,"flashAttention":true}` → `{"id":n,"ok":true,"runtime":"...","gpu":"<name>"}` or `{"id":n,"ok":false,"code":"model_file|runtime|...","message":"..."}`.
- `{"id":n,"cmd":"transcribe","wav":"<path>","language":"en|null","options":{...}}` → `{"id":n,"ok":true,"text":"...","segments":[...]}`.
- `{"id":n,"cmd":"cancel","target":n}` for a cancelled dictation.
- `{"cmd":"shutdown"}` → the worker disposes and exits 0.
- stderr goes to the app log with a `[whisper-worker]` prefix.
- One request at a time. The client serializes.

### 5.3 The client: `WhisperWorkerClient` behind `TranscriptionService`

- `TranscriptionService` keeps its public API, so the callers in §4 do not change.
- `InitializeAsync(path)` records the selected model and starts the worker, sends `load`, waits for the answer.
- `IsInitialized` / `LoadedModelPath` mean "this model is selected and can serve", not "the process is alive". After an idle exit they stay true. The next transcribe starts the worker and loads again.
- `UnloadModelAsync` sends `shutdown` and clears the selection.
- Idle policy *(open, D2)*: after 5 minutes with no request, the client sends `shutdown`. A request during shutdown waits, then starts a new worker.
- Keep-warm: at recording start, if the worker is down, start it and send `load` in parallel with the recording. So most of the start + load cost hides behind the speech.
- Crash: if the worker dies during a request, restart once and retry, like Parakeet. A second failure returns the error.
- `IsModelFileLoadFault` maps to the worker's `code`, so `LocalModelHealth` still sees a broken model file and not a runtime fault.

### 5.4 Packaging

- `build-release.ps1` publishes the worker into `{app}\whisper-worker\`.
- Each `.iss`: one `[InstallDelete]` line for `{app}\whisper-worker`, and `KillProcess('HyperWhisper.WhisperWorker.exe')` next to the Parakeet one.
- Debug `dotnet run` of the app finds the worker by a project reference or a copy step, so the dev loop works.

## 6. Decisions

- D1 *(decided 2026-10-16)*: Option A, a new console project `HyperWhisper.WhisperWorker`. Not B (re-launch `HyperWhisper.exe --whisper-worker`: WPF host starts in the child, Whisper DLLs stay in the app). Not C (accept P0).
- D2 *(open)*: the idle exit — fixed 5 minutes, or a setting.
- D3 *(decided 2026-10-16)*: every Whisper path runs in the worker (x64 GPU, x64 CPU, ARM64). One code path. The app drops Whisper.net. Not chosen: worker only for x64 with a discrete GPU.
- D4 *(decided 2026-10-16)*: reuse `LocalWhisperService` from `HyperWhisper.LocalInference`. Move the runtime packages from the shared csproj to each head (Linux keeps Cuda12; the worker gets Runtime, NoAvx, Vulkan). Windows moves to Whisper.net 1.9.1. Not chosen: a worker-only copy of the code on 1.9.0.
- D5 *(open)*: the cold-start cost — keep-warm at recording start only, or also at app start.

## 7. Risks

- First dictation after idle pays process start + model load (a few seconds for a large model). Keep-warm hides part of it. Because of D3, CPU-only and ARM64 users pay it too; they get the model RAM back in exchange.
- Antivirus and SmartScreen can slow or block a new unsigned exe. The worker needs the same signing as the app.
- Two processes hold the model during a model switch if the old worker does not exit first. The client waits for the exit.
- Temp WAV files must be deleted after each request, also on a crash.
- Whisper.net 1.9.1 on Windows Vulkan is new. The RTX 4060 smoke and one CPU-only run must pass before the PR is ready.
- Installer size grows if the worker carries its own self-contained .NET runtime.

## 8. Test plan

- Unit: protocol parse and build, client state machine (start, load, idle shutdown, crash restart once, cancel), with a fake worker process.
- ARM64: one dictation through the worker on a Windows 11 ARM64 build (or the ARM64 publish at least starts and answers `ready`).
- Smoke on the Windows dev box (RTX 4060): the #1122 acceptance in §2, with `nvidia-smi` samples and the app log as evidence.
- Local API: `/transcribe` with a Whisper model before and after an idle exit.
- Installer: upgrade over the last release; check that `{app}\whisper-worker` is replaced and that no worker survives the upgrade.
