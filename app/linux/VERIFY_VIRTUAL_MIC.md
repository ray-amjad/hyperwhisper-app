# Tier-2 virtual microphone verification

The deterministic CI harness exercises the production recording workflow,
SQLite history repository, and an isolated injection target without accessing a
desktop clipboard, keyboard, microphone, or speaker:

```bash
dotnet run --project app/linux/HyperWhisper.Linux.E2E.Tests/HyperWhisper.Linux.E2E.Tests.csproj -c Release
```

The live Tier-2 test starts a private PipeWire graph and its Pulse compatibility
server, creates a null-sink monitor virtual microphone, and plays a known WAV
into it. WirePlumber runs with its policy-only profile, which disables ALSA,
Bluetooth, and video hardware monitoring. The test records the monitor through
the production `PulseAudioRecorder`, verifies captured speech timing before ASR,
transcribes it with the production local Whisper adapter, then checks normalized
transcript equality, completed SQLite history, retained private audio, and the
isolated injection target. It never changes the user's default audio server or
admits physical devices into the private graph.

Install PipeWire, WirePlumber, `pulseaudio-utils`, `curl`, and `ffmpeg`, then
provide a ggml model:

```bash
HW_E2E_WHISPER_MODEL=/absolute/path/ggml-tiny.en.bin \
  app/linux/scripts/run-virtual-mic-e2e.sh
```

For release evidence, use a reviewed 16 kHz mono PCM WAV whose expected text is
`Ray is verifying the Hyper Whisper Linux speech transcription build.`:

```bash
HW_E2E_WHISPER_MODEL=/absolute/path/ggml-tiny.en.bin \
HW_E2E_AUDIO_PATH=/absolute/path/known-16k.wav \
  app/linux/scripts/run-virtual-mic-e2e.sh
```

When `HW_E2E_AUDIO_PATH` is omitted, the test reproducibly generates its known
WAV with `generate-virtual-mic-fixture.sh`. The generator pins the archived MIT
licensed Piper 2023.11.14-2 executable and every download by SHA-256. Its
LJSpeech voice is trained from the public-domain LJSpeech dataset, and the final
16 kHz PCM fixture is also checksum-gated (`974376044d4af6b2dff131c58f5c73827670de1422a184b9e5d240aadb42553d`).
The punctuation in the synthesis prompt creates audible separation between the
product name's two words; transcript comparison discards punctuation only.

The script prints an explicit `SKIP` only when no Whisper model is supplied.
Missing audio/runtime commands and all verification mismatches fail the test.
