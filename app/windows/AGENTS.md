# AGENTS.md

HyperWhisper Windows — C# 12 + WPF (XAML) app on .NET 10, Windows 10+. Audio via NAudio, transcription via WhisperNet (Const-me/Whisper) with DirectCompute GPU acceleration. MVVM, EF Core for persistence.

## Project map

- `HyperWhisper.csproj` — root project (run from here)
- `Views/Windows/` + `Views/Pages/` — WPF XAML + code-behind
- `ViewModels/` — MVVM view models
- `Services/` — `TranscriptionService`, `AudioRecorderService`, `AudioDeviceService`, `WhisperModelService`, `LoggingService`, `HyperWhisperCloudService`, `HyperWhisperRoutedTranscriptionClient`
- `Models/` — `WhisperModelInfo`, `CloudTranscriptionProvider`, `CloudTranscriptionModel`
- `Data/Entities/` — EF Core entities (modifying these requires a migration; see below)
- `Migrations/` — EF Core migrations + `HyperWhisperDbContextModelSnapshot.cs`
- `Resources/Strings*.resx` — localization (base `Strings.resx`; one per language)
- `Resources/` (runtime) — Whisper native DLLs under `runtimes/win-x64/` and `runtimes/win-arm64/`

<important if="you need to build, run, publish, or release the Windows app (run these from Windows PowerShell, or `powershell.exe` from WSL — .NET SDK lives on Windows)">

| Command | What it does |
|---|---|
| `dotnet run -c Debug` (from `HyperWhisper/`) | Run in Debug |
| `dotnet build -c Debug` | Build Debug only |
| `dotnet publish -c Release -r win-x64 --self-contained true` | Release publish for x64 |
| `dotnet publish -c Release -r win-arm64 --self-contained true` | Release publish for ARM64 |
| `.\build-release.ps1 -Architecture x64 -Version <X.Y.Z>` (from `app/windows/`) | Build installer (x64) |
| `.\build-release.ps1 -Architecture arm64 -Version <X.Y.Z>` | Build installer (ARM64) |
| `.\build-release.ps1 -Architecture both -Version <X.Y.Z>` | Build both installers |
| `.\build-release.ps1 -Architecture both -Version <X.Y.Z> -Sign` | + NetSparkle signing |
| `.\build-release.ps1 -Architecture x64 -Version <X.Y.Z> -SkipBuild` | Reuse existing publish, only repack the installer |

Installer output: `app/windows-installers/`. Prereqs: .NET 10 SDK on Windows, Inno Setup 6 (searched in `Program Files (x86)`, `Program Files`, `%LOCALAPPDATA%\Programs\Inno Setup 6\`).
</important>

<important if="you modified any entity class in Data/Entities/">

You MUST add an EF migration in the SAME change — the app throws `PendingModelChangesWarning` on startup if the model has unmigrated edits.

```powershell
cd <repo>\app\windows\HyperWhisper
dotnet ef migrations add <DescriptiveName>
```

Commit the entity edit, the generated `Migrations/<timestamp>_<name>.cs` + `.Designer.cs`, AND `HyperWhisperDbContextModelSnapshot.cs` together. To reset the dev DB: delete `%LOCALAPPDATA%\HyperWhisper\hyperwhisper.db` and restart.
</important>

<important if="you need to load, debug, or analyze the Windows app logs">

Logs: `%LOCALAPPDATA%\HyperWhisper\Logs\hyperwhisper-{YYYY-MM-DD}.log`. Format: `[YYYY-MM-DD HH:mm:ss.fff] [LEVEL] [T####] Message`.

Quick PowerShell tail:
```powershell
Get-Content "$env:LOCALAPPDATA\HyperWhisper\Logs\hyperwhisper-$(Get-Date -Format 'yyyy-MM-dd').log" -Tail 100
Get-Content "$env:LOCALAPPDATA\HyperWhisper\Logs\hyperwhisper-$(Get-Date -Format 'yyyy-MM-dd').log" -Wait -Tail 50  # follow
```

For multi-line diagnostics use the `windows-log-explorer` subagent — it reads today's log file and extracts entries scoped to a specific failure.

API surface (`Services/LoggingService.cs`): `Debug`/`Info`/`Warn`/`Error`, plus `LogSystemInfo`, `LogRuntimesDirectory`, `LogLoadedAssemblies`, `LogPathEnvironment`, `OpenLogDirectory`, `CleanupOldLogs(days)`.
</important>

<important if="you are debugging a `Fail to load native Wispr library` / WhisperNet load failure">

`LoggingService.LogRuntimesDirectory()` dumps the DLL inventory at startup. Key signals:

| Log entry | What it means |
|---|---|
| `Process Architecture: X64` / `Arm64` | Process arch — needs matching DLL set |
| `runtimes directory does not exist` | NuGet packages not restored properly |
| `[FILE] win-x64/whisper.dll` | DLL present |
| `Exception Type: DllNotFoundException` | DLL missing or blocked by Mark of the Web |
| `Exception Type: BadImageFormatException` | Architecture mismatch (x64 process loading ARM64 DLL or vice versa) |

WhisperNet requires Windows 10+, VC++ Redistributable 2015-2022, DirectX 11. Windows-only; x64 + ARM64 only (no Linux/macOS).
</important>

<important if="you are writing a Service method or any operation where failure is expected (API calls, file ops, device access)">

Return `Result<T>` instead of throwing. Forces callers to handle both cases and keeps `try/catch` out of every call site. Reference implementation: `Services/` (existing services follow this pattern — match the shape). Caller pattern: `result.Match(onSuccess: ..., onFailure: ...)` or `if (result.IsSuccess)` / `else`.

Throw only for true programmer errors (invalid arguments at API boundaries). Use `ArgumentException` with `nameof(param)` in that case.
</important>

<important if="you are writing or modifying a public method">

Open with guard clauses that validate preconditions and return/log early — keeps the happy path at the lowest indent. Example shape lives in `ViewModels/MainViewModel.cs` (`StartRecordingAsync`). For null/empty argument checks at the API boundary, `throw new ArgumentException(..., nameof(arg))`.
</important>

<important if="you are writing a class that holds IDisposable resources, COM objects, or event subscriptions">

Implement `IDisposable` with a `_disposed` guard and a `SafeDispose<T>(ref T?)` helper that nulls the ref before disposing and catches+logs any exception (one resource's failure must not block cleanup of the others). Dispose in reverse order of creation. Always unsubscribe from events in `finally` before disposing the source.

Reference: existing disposable services in `Services/` (search for `SafeDispose`).
</important>

<important if="you are raising events or dispatching from a background/COM thread to the UI">

Raise via a `RaiseEvent(handler, args)` helper that null-checks and try/catches every invocation — one bad subscriber must not break others. For UI updates from background or COM callbacks (e.g. `MMDeviceEnumerator` device-change notifications), dispatch via `Application.Current?.Dispatcher` and check `HasShutdownStarted` before `BeginInvoke`. Existing pattern: `Services/AudioDeviceService.cs`.
</important>

<important if="you are enumerating hardware devices or any external collection that can change mid-iteration">

Capture the count once, wrap each per-item access in its own try/catch, and skip items that throw (the device may have been removed between count + access). Wrap the whole loop in a try/catch so a failed enumeration returns a partial list instead of throwing. Reference: `Services/AudioDeviceService.cs` → `GetAvailableDevices`.
</important>

<important if="you are wiring up an event that can fire faster than you can meaningfully respond (device-change storms, typing, resize)">

Debounce via a `System.Timers.Timer` (`AutoReset = false`) on a ~250 ms delay — cancel + restart the timer on every incoming event, fire the handler once on `Elapsed`. Existing pattern lives in `Services/AudioDeviceService.cs` (`OnDevicesChanged`).
</important>
