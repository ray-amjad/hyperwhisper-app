# Spec: Windows on-device Whisper in an idle-exiting worker process

Status: DRAFT 1. The interview is not done. Items marked *(open)* wait for Ray.

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
- No upgrade of the Whisper.net native runtime for its own sake (see decision D4).

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

## 5. Design (draft — recommended Option A, decision D1)

### 5.1 The worker: `app/windows/HyperWhisper.WhisperWorker`

- A console app, `net10.0`, self-contained publish for `win-x64` (and `win-arm64` if D3 says so).
- It references Whisper.net and its native runtimes. The main app no longer does (if D3 moves every path).
- It does the native work only: load the model, run the processor with the options the app sends, return segments and text.
- Audio preparation stays in the app. The app writes the 16 kHz mono WAV to a temp file and sends the path. So NAudio stays out of the worker.
- It exits on stdin EOF. So it dies with the app, also on a crash of the app.

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

- D1 *(open)*: the worker design — A new console project (recommended), B re-launch `HyperWhisper.exe --whisper-worker`, or C accept P0 and close #1194.
- D2 *(open)*: the idle exit — fixed 5 minutes, or a setting.
- D3 *(open)*: which paths use the worker — every platform, or x64 GPU only with CPU and ARM64 in-process.
- D4 *(open)*: the Whisper.net version in the worker — reuse `LocalInference` (1.9.1, with Cuda12 and no NoAvx), or the app's 1.9.0 set.
- D5 *(open)*: the cold-start cost — keep-warm at recording start only, or also at app start.

## 7. Risks

- First dictation after idle pays process start + model load (a few seconds for a large model). Keep-warm hides part of it.
- Antivirus and SmartScreen can slow or block a new unsigned exe. The worker needs the same signing as the app.
- Two processes hold the model during a model switch if the old worker does not exit first. The client waits for the exit.
- Temp WAV files must be deleted after each request, also on a crash.
- Installer size grows if the worker carries its own self-contained .NET runtime.

## 8. Test plan

- Unit: protocol parse and build, client state machine (start, load, idle shutdown, crash restart once, cancel), with a fake worker process.
- Smoke on the Windows dev box (RTX 4060): the #1122 acceptance in §2, with `nvidia-smi` samples and the app log as evidence.
- Local API: `/transcribe` with a Whisper model before and after an idle exit.
- Installer: upgrade over the last release; check that `{app}\whisper-worker` is replaced and that no worker survives the upgrade.
