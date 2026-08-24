#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 OUTPUT_WAV" >&2
  exit 2
fi

for command_name in curl tar sha256sum ffmpeg ffprobe; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "ERROR: fixture generator requires: $command_name" >&2
    exit 1
  fi
done

output_path="$(realpath -m "$1")"
work_dir="$(mktemp -d /tmp/hyperwhisper-voice-fixture.XXXXXX)"
if [[ "$work_dir" != /tmp/hyperwhisper-voice-fixture.* ]]; then
  echo "ERROR: unsafe temporary directory returned by mktemp." >&2
  exit 1
fi
chmod 700 "$work_dir"
cleanup() { find -P "$work_dir" -depth -delete 2>/dev/null || true; }
trap cleanup EXIT INT TERM

piper_url='https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_linux_x86_64.tar.gz'
voice_root='https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/ljspeech/medium'
curl -L --fail --silent --show-error "$piper_url" -o "$work_dir/piper.tar.gz"
curl -L --fail --silent --show-error "$voice_root/en_US-ljspeech-medium.onnx" -o "$work_dir/voice.onnx"
curl -L --fail --silent --show-error "$voice_root/en_US-ljspeech-medium.onnx.json" -o "$work_dir/voice.onnx.json"

(
  cd "$work_dir"
  sha256sum --check <<'CHECKSUMS'
a50cb45f355b7af1f6d758c1b360717877ba0a398cc8cbe6d2a7a3a26e225992  piper.tar.gz
6f52a751e2349abe7a76735eb09dc1875298c77ea2342ffd2fef79ff81b87f22  voice.onnx
141d612cc0a95ed7efc1ca936b845c2364967f2e9217c5dbfcf69fc4d6c65860  voice.onnx.json
CHECKSUMS
)

tar --extract --gzip --file "$work_dir/piper.tar.gz" --directory "$work_dir" --no-same-owner
printf '%s\n' 'Ray is verifying the Hyper. Whisper. Linux speech transcription build.' \
  | "$work_dir/piper/piper" \
      --quiet \
      --noise_scale 0 \
      --noise_w 0 \
      --model "$work_dir/voice.onnx" \
      --config "$work_dir/voice.onnx.json" \
      --output_file "$work_dir/generated.wav" \
      >/dev/null
ffmpeg -hide_banner -loglevel error -y \
  -i "$work_dir/generated.wav" -ar 16000 -ac 1 -c:a pcm_s16le "$work_dir/known-16k.wav"
audio_spec="$(ffprobe -v error -select_streams a:0 \
  -show_entries stream=codec_name,sample_rate,channels \
  -of csv=p=0 "$work_dir/known-16k.wav")"
if [[ "$audio_spec" != 'pcm_s16le,16000,1' ]]; then
  echo "ERROR: generated fixture has unexpected audio format: $audio_spec" >&2
  exit 1
fi

# Piper output can differ at the sample level across CPU implementations even
# with noise disabled. The dependency hashes above protect fixture provenance;
# the live E2E validates timing and the exact recognized sentence.

install -m 600 "$work_dir/known-16k.wav" "$output_path"
printf '%s\n' "$output_path"
