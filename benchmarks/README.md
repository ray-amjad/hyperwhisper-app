# HyperWhisper benchmarks

Drives the live macOS app's transcription pipeline through every installed engine — cloud and local — over a curated sample corpus, measures latency + WER, and emits a browsable per-sample review with editable ground-truth references.

The whole point: the model library's Speed/Accuracy bars used to be substring heuristics (`if id.contains("turbo") return 4`). This pipeline replaces them with empirical numbers from the **same code path the GUI uses** — no parallel CLI implementation that could drift from real behavior.

## Prerequisites

- macOS with HyperWhisper installed (Debug or Release)
- The Local API server toggle **on** in Settings → API Server (the harness drives `POST /transcribe` through it)
- `ffprobe` (used by the sample picker to read durations): `brew install ffmpeg`
- Python 3.10+ (stdlib only — no pip install needed)
- API keys for whichever cloud providers you want to score, configured in Settings → API Keys

## End-to-end run

```bash
cd benchmarks

# Stage 0 — verify keys + installed local models. Optional but catches
# auth issues before you burn an hour on a sweep that fails every cloud call.
python3 health_check.py

# Stage 1 — pick a corpus from ~/Documents/hyperwhisper/recordings/ and
# ~/Library/Application Support/HyperWhisper/recordings/. Random N per
# duration bucket (<5s, 5-20s, 20-60s, >60s), sha256-pinned for stable IDs.
python3 select_samples.py --n 3 --seed 42        # writes samples.json

# Stage 2 — drive POST /transcribe for every (sample × installed engine).
# Resumable: each engine writes its own file, re-runs skip what's done.
python3 run_transcribe_sweep.py --max-duration 120 --run-id sweep-XX

# Stage 3 — Scribe v2 reference + per-engine WER. Merges in your manual
# corrections from overrides.json (see "Hand-correcting references" below).
python3 score_results.py sweep-XX

# Stage 4 — review UI. Opens browser to leaderboard + per-sample side-by-side.
python3 serve_review.py --run sweep-XX
```

## Hand-correcting references

Scribe v2 is the highest-WER reference we have but it isn't perfect. The review page makes every reference block contenteditable — click into it, fix what's wrong. Edits autosave to `results/<run-id>/overrides.json` and the WER leaderboard live-recomputes in the browser.

Re-running `score_results.py` will pick up the overrides and bake them into `scored.json`. The overrides file is committed (it's load-bearing ground truth); per-engine raw output files are gitignored (regenerable, noisy).

## Updating the model library ratings

After a fresh sweep + scoring:

```bash
python3 propose_ratings.py sweep-XX
```

Prints a per-family table with proposed (speed, accuracy) bars based on the latency + WER buckets at the bottom of that script's output. Manually port the per-model values into the `cloudRatings` / `whisperRatings` / `parakeetRatings` dicts in `app/macos/hyperwhisper/Managers/ModelLibraryManager.swift`.

## Post-processing pipeline

Same idea applied to the post-processing tier (Settings → Model Library → Text). Substring heuristics in `postSpeed(for:)` / `postAccuracy(for:)` get replaced with empirical numbers.

The non-obvious bit is **scoring**: WER against a transcription doesn't apply because LLM rewrites are interpretive. Instead, the reference for each input is the cleaned output Claude produces by applying the `hyper` preset prompt itself — written once into `pp_references.json` and committed. Every model's output is then WER-scored against that reference, mirroring how transcription scoring uses Scribe v2.

```bash
cd benchmarks

# Stage 1 — assemble the input corpus.
# Reuses the hand-corrected transcripts from results/sweep-01/ (already
# cleaned by you on review.html) so the two benchmarks share inputs.
# Adds a few synthetic inputs (code dictation, email draft, bullet notes)
# inline in the picker for shapes the recordings don't cover.
python3 pp_pick_inputs.py                  # writes pp_inputs.json

# Stage 2 — drive POST /post-process for every (input × installed text
# model × preset). Resumable: each (engine, preset) writes its own file.
python3 run_postprocess_sweep.py --run-id pp-01

#   Useful flags:
#     --presets hyper,note,code         multi-preset matrix (default: hyper)
#     --include-providers openai,anthropic
#     --exclude-providers local_llm     skip slow local LLMs
#     --max-input-chars 800             skip inputs slow models would choke on
#     --include-hyperwhisper-cloud      include the routed hyperwhisper-cloud entry

# Stage 3 — score every output against pp_references.json.
python3 score_pp_results.py pp-01

# Stage 4 — turn (WER, p50 latency) into proposed (speed, accuracy) bars.
python3 propose_pp_ratings.py pp-01
```

The proposal script prints a markdown table grouped by provider plus a Swift dict literal ready to paste into the `postProcessingRatings` map in `app/macos/hyperwhisper/Managers/ModelLibraryManager.swift`.

### Updating the reference

`pp_references.json` is the single load-bearing artifact for post-processing scoring. When you decide an output should look different — or you change the `hyper` preset prompt enough that old references no longer reflect it — edit the value for that input, re-run `score_pp_results.py`, and the leaderboard updates.

## File layout

```
benchmarks/
  README.md                 ← this file
  .gitignore                ← skips per-engine raw files + transient state
  health_check.py           ← Stage 0: keys + local models
  select_samples.py         ← Stage 1: corpus picker
  run_transcribe_sweep.py   ← Stage 2: harness driving /transcribe
  score_results.py          ← Stage 3: Scribe v2 reference + WER
  propose_ratings.py        ← (optional) WER+latency → 1-5 bar proposal
  serve_review.py           ← Stage 4: review server (audio + overrides routes)
  review.html               ← leaderboard + editable per-sample refs
  samples.json              ← corpus pins (committed; paths are machine-specific)

  pp_pick_inputs.py         ← post-processing Stage 1: input corpus picker
  pp_inputs.json            ← committed input corpus (cleaned transcripts + synthetic)
  pp_references.json        ← committed gold-standard outputs (Claude applying hyper)
  run_postprocess_sweep.py  ← post-processing Stage 2: harness driving /post-process
  score_pp_results.py       ← post-processing Stage 3: WER vs pp_references.json
  propose_pp_ratings.py     ← (optional) WER+latency → 1-5 bar proposal
  results/
    <run-id>/
      overrides.json        ← your manual reference corrections (committed)
      scored.json           ← publishable per-run summary (committed)
      summary.json          ← regenerable from per-engine files (gitignored)
      <engine>.json         ← raw per-engine output (gitignored)
    pp-<run-id>/
      scored.json           ← publishable per-(engine, preset) summary (committed)
      summary.json          ← regenerable from per-engine files (gitignored)
      <provider>-<model>-<preset>.json  ← raw per-run output (gitignored)
```

## Notes

- The Local API server bearer token is read from `~/Library/Application Support/HyperWhisper/local-api.json`. If `run_transcribe_sweep.py` fails with "local-api.json not found", flip the API Server toggle on in Settings.
- Long clips (>60s) on local Whisper large-v3 can take 10+ seconds. The sweep request timeout is 900s (15 min) so even a 12-minute clip through large-v3 completes.
- Some providers' health probes return 200 (key valid) but reject the actual transcribe POST. We've hit this twice: AssemblyAI's deprecated `speech_model` field, and Google's deprecated Gemini 2.0 Flash. Diagnose by running one sample directly via `curl` against the provider's live API, bypassing HyperWhisper.
- `samples.json` is committed with absolute paths. They only resolve on Ray's machine. To re-run on a fresh machine, run `select_samples.py` to pick a new corpus.
