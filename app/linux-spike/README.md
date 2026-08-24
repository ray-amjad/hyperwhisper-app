# HyperWhisper Linux integration risk spike

This is a **non-shipping** executable architecture spike for issue #179. It
validates the shape and safety contracts of the Linux platform seams before a
shared C# project or Avalonia UI is introduced.

## What this proves

- evdev `input_event` parsing for the v1 `linux-x64` target;
- a hotkey privacy boundary where non-configured keys are never emitted or
  logged, and public events contain only configured action IDs;
- a single transcript-injection chokepoint that copies first, attempts uinput
  paste second, and leaves text on the clipboard when uinput is unavailable;
- a `libpulse.so.0` capability gate and injectable PulseAudio capture backend;
- explicit active-app capability routing for X11, KDE Wayland D-Bus, and the
  GNOME Wayland companion-extension fallback.

It does not contain an Avalonia shell, production clipboard integration,
uinput device creation, a full `libpulse` capture adapter, or production D-Bus
clients. Those native implementations are intentionally behind interfaces so
they can be tested on the three required desktop environments without leaking
platform code into the future shared core.

## Privacy invariant

The evdev reader necessarily sees system-wide keyboard events. Raw events enter
the internal `HotkeyPrivacyFilter`; only a configured binding's action ID and
pressed/released state can leave it. Non-configured keys are retained only in
the current stack frame and are discarded before chord state is updated.
Diagnostics expose counters without raw codes or key names. The executable
tests enforce the boundary.

## Prerequisites

- .NET 10 SDK
- `libpulse0` for a positive PulseAudio capability result
- `libx11-6` for a positive X11 capability result
- read access to a selected `/dev/input/event*` device for a real hotkey test
- write access to `/dev/uinput` for a real injection test

Input-device permissions are security-sensitive. Do not weaken all input device
permissions to make the spike pass.

## Build and test

From the repository root:

```bash
dotnet build app/linux-spike/HyperWhisper.LinuxSpike/HyperWhisper.LinuxSpike.csproj -c Release
dotnet run --project app/linux-spike/HyperWhisper.LinuxSpike.Tests -c Release
```

Run the read-only capability report:

```bash
dotnet run --project app/linux-spike/HyperWhisper.LinuxSpike.Probe -c Release
```

For desktop-VM routing tests, the temporary environment flags below stand in
for the production D-Bus probe adapters:

```bash
HYPERWHISPER_SPIKE_KDE_DBUS=1 \
  dotnet run --project app/linux-spike/HyperWhisper.LinuxSpike.Probe -c Release

HYPERWHISPER_SPIKE_GNOME_EXTENSION=1 \
  dotnet run --project app/linux-spike/HyperWhisper.LinuxSpike.Probe -c Release
```

## Exit criteria before porting services

On GNOME Wayland, GNOME Xorg, and KDE Wayland:

1. Run the unit tests.
2. Confirm the capability report chooses the expected active-app route.
3. Attach a real evdev adapter test and prove unrelated key activity produces
   no public signal and no diagnostic content.
4. Attach a real uinput backend and inject a known sentence into a disposable
   editor; remove uinput permission and confirm clipboard-only fallback.
5. Attach the libpulse backend and record a deterministic WAV from
   `pipewire-pulse` or PulseAudio.

The spike must remain non-shipping until these checks pass on real desktops.
