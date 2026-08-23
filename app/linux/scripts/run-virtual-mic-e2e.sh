#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
project="$repo_root/app/linux/HyperWhisper.Linux.E2E.Tests/HyperWhisper.Linux.E2E.Tests.csproj"

model_path="${HW_E2E_WHISPER_MODEL:-${HYPERWHISPER_MODEL_PATH:-}}"
if [[ -z "$model_path" || ! -f "$model_path" ]]; then
  echo "SKIP: set HW_E2E_WHISPER_MODEL to an existing ggml Whisper model for the live Tier-2 virtual-microphone test."
  exit 0
fi

for command_name in dotnet pipewire pipewire-pulse wireplumber pw-cli pactl paplay; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "ERROR: required Tier-2 runtime command is unavailable: $command_name" >&2
    exit 1
  fi
done

run_dir="$(mktemp -d /tmp/hyperwhisper-virtual-mic.XXXXXX)"
if [[ "$run_dir" != /tmp/hyperwhisper-virtual-mic.* ]]; then
  echo "ERROR: unsafe temporary directory returned by mktemp." >&2
  exit 1
fi
chmod 700 "$run_dir"
pipewire_pid=''
pipewire_pulse_pid=''
wireplumber_pid=''
cleanup() {
  for process_id in "$pipewire_pulse_pid" "$wireplumber_pid" "$pipewire_pid"; do
    [[ -z "$process_id" ]] || kill "$process_id" 2>/dev/null || true
  done
  for process_id in "$pipewire_pulse_pid" "$wireplumber_pid" "$pipewire_pid"; do
    [[ -z "$process_id" ]] || wait "$process_id" 2>/dev/null || true
  done
  find -P "$run_dir" -depth -delete 2>/dev/null || true
}
trap cleanup EXIT INT TERM

audio_path="${HW_E2E_AUDIO_PATH:-}"
if [[ -z "$audio_path" ]]; then
  audio_path="$run_dir/known-16k.wav"
  "$repo_root/app/linux/scripts/generate-virtual-mic-fixture.sh" "$audio_path" >/dev/null
elif [[ ! -f "$audio_path" ]]; then
  echo "ERROR: HW_E2E_AUDIO_PATH does not identify a WAV file: $audio_path" >&2
  exit 1
fi

export XDG_RUNTIME_DIR="$run_dir"
export XDG_CONFIG_HOME="$run_dir/config"
export PIPEWIRE_RUNTIME_DIR="$run_dir"
export PIPEWIRE_REMOTE='pipewire-0'
export PULSE_SERVER="unix:$run_dir/pulse/native"

pipewire >"$run_dir/pipewire.log" 2>&1 &
pipewire_pid=$!
for _ in $(seq 1 100); do
  [[ -S "$run_dir/pipewire-0" ]] && break
  if ! kill -0 "$pipewire_pid" 2>/dev/null; then
    echo "ERROR: isolated PipeWire core exited during startup." >&2
    sed -n '1,160p' "$run_dir/pipewire.log" >&2 || true
    exit 1
  fi
  sleep 0.05
done
if [[ ! -S "$run_dir/pipewire-0" ]]; then
  echo "ERROR: isolated PipeWire core socket was not created." >&2
  exit 1
fi

# The policy-only profile links the private graph but disables ALSA, Bluetooth,
# and video hardware monitors, so no physical device can enter this test graph.
wireplumber --profile policy >"$run_dir/wireplumber.log" 2>&1 &
wireplumber_pid=$!
pipewire-pulse >"$run_dir/pipewire-pulse.log" 2>&1 &
pipewire_pulse_pid=$!

for _ in $(seq 1 100); do
  [[ -S "$run_dir/pulse/native" ]] && break
  if ! kill -0 "$pipewire_pulse_pid" 2>/dev/null; then
    echo "ERROR: isolated PipeWire Pulse compatibility server exited during startup." >&2
    sed -n '1,160p' "$run_dir/pipewire-pulse.log" >&2 || true
    exit 1
  fi
  sleep 0.05
done
if [[ ! -S "$run_dir/pulse/native" ]]; then
  echo "ERROR: isolated PipeWire Pulse compatibility socket was not created." >&2
  exit 1
fi

pactl info | grep -F 'Server Name: PulseAudio (on PipeWire' >/dev/null
if pw-cli ls Device | grep -Eq '^[[:space:]]*id [0-9]+'; then
  echo "ERROR: a hardware device entered the policy-only PipeWire graph." >&2
  exit 1
fi
pactl load-module module-null-sink sink_name=hw_e2e_sink rate=48000 channels=1 >/dev/null
sleep 0.25
pactl list short sources | grep -F $'hw_e2e_sink.monitor\t' >/dev/null

HW_E2E_AUDIO_PATH="$audio_path" \
HW_E2E_WHISPER_MODEL="$model_path" \
HW_E2E_SOURCE='hw_e2e_sink.monitor' \
HW_E2E_SINK='hw_e2e_sink' \
HW_E2E_PLAYER="$(command -v paplay)" \
dotnet run --project "$project" --configuration Release -- --live
