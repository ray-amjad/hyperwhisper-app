# HyperWhisper Linux

HyperWhisper Linux uses C# 14, .NET 10, and Avalonia 12 on x86_64 Linux.
Stable Avalonia renders through X11/XWayland; Wayland-specific portals and
desktop integration live behind platform abstractions and are tested separately.

## Build and smoke test

Run from the repository root:

```bash
dotnet build app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj -c Release
app/linux/scripts/run-ui-smoke.sh
```

The smoke command must create and render the real main window, navigate every
top-level page, and shut down with exit code zero. Do not replace it with a
compile-only or timeout-based check.

Expected platform failures use `PlatformResult<T>`. Raw evdev key events must
not leave the Linux hotkey module; only configured action identifiers may be
emitted, stored, or logged.
