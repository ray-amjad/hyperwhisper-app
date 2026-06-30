#!/usr/bin/env python3
"""
HyperWhisper post-processing benchmark — input corpus picker.

Reuses the hand-corrected references from the transcription benchmark
(results/sweep-01/overrides.json + Scribe v2 fallback) as the input
text for /post-process. Adds a handful of synthetic inputs to cover
gaps the real recordings don't (code dictation, structured email,
bulleted notes).

Run:
    python3 pp_pick_inputs.py                 # writes pp_inputs.json
    python3 pp_pick_inputs.py --sweep sweep-01

Output schema:
    [{"id": "<sample_id|synth_…>", "source": "sweep-01|synthetic",
      "text": "...", "bucket": "lt5s|5to20s|20to60s|gt60s|synthetic"}]
"""

import argparse
import json
import sys
from pathlib import Path

BENCH_DIR = Path(__file__).parent
RESULTS_DIR = BENCH_DIR / "results"

# Synthetic inputs for content shapes the recording corpus doesn't cover.
# Written as a dictation transcript would arrive — verbal punctuation,
# self-corrections, fillers — so /post-process has something to actually
# clean up.
SYNTHETIC_INPUTS = [
    {
        "id": "synth_code_dictation",
        "bucket": "synthetic",
        "text": (
            "okay so let's create a function called process underscore data "
            "that takes a list parameter and returns a sorted version. "
            "make sure to handle the empty case by returning an empty list. "
            "use the sorted built-in with a key argument lambda x colon "
            "x dot timestamp"
        ),
    },
    {
        "id": "synth_email_draft",
        "bucket": "synthetic",
        "text": (
            "Hey Sarah comma new paragraph just a heads up that the meeting "
            "moved from Tuesday to Wednesday at 3pm. Conference room is now "
            "Atlas instead of Beacon. Let me know if that works for you. "
            "new paragraph Thanks comma new paragraph John"
        ),
    },
    {
        "id": "synth_meeting_bullets",
        "bucket": "synthetic",
        "text": (
            "Action items from the standup. First, Mike is going to look "
            "into the auth bug. Second, we need to push the migration to "
            "staging by Friday. Third, Lisa is doing the API audit next "
            "sprint and will report back at the next sync."
        ),
    },
]

# This sample transcribed to silence — no input text to feed /post-process.
SKIP_IDS = {"95e665d941f5"}

# Engine key that produced the fallback (transcript) reference in sweep-01.
REFERENCE_ENGINE_KEY = "cloud-elevenlabs-scribe_v2"


def load_sweep_inputs(sweep_id: str) -> list[dict]:
    run_dir = RESULTS_DIR / sweep_id
    scored_path = run_dir / "scored.json"
    overrides_path = run_dir / "overrides.json"
    if not scored_path.exists():
        sys.exit(f"scored.json missing at {scored_path} — run score_results.py {sweep_id} first.")

    scored = json.loads(scored_path.read_text())
    overrides = json.loads(overrides_path.read_text()) if overrides_path.exists() else {}

    samples = scored.get("samples", [])
    bucket_by_id = {s["id"]: s.get("bucket", "?") for s in samples}

    ref_by_id: dict[str, str] = {}
    for e in scored.get("engines", []):
        if e.get("engine_key") != REFERENCE_ENGINE_KEY:
            continue
        for r in e.get("results", []):
            sid = r.get("sample_id")
            text = (r.get("text") or "").strip()
            if sid and text:
                ref_by_id[sid] = text
        break

    # Manual corrections beat the transcription engine.
    for sid, text in overrides.items():
        text = (text or "").strip()
        if text:
            ref_by_id[sid] = text

    inputs: list[dict] = []
    for sid in sorted(ref_by_id.keys()):
        if sid in SKIP_IDS:
            continue
        inputs.append({
            "id": sid,
            "source": sweep_id,
            "text": ref_by_id[sid],
            "bucket": bucket_by_id.get(sid, "?"),
        })
    return inputs


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sweep", default="sweep-01",
                    help="transcription-sweep run-id to mine references from (default: sweep-01)")
    ap.add_argument("--out", type=Path, default=BENCH_DIR / "pp_inputs.json")
    ap.add_argument("--no-synthetic", action="store_true",
                    help="omit the synthetic gap-fill inputs")
    args = ap.parse_args()

    sweep_inputs = load_sweep_inputs(args.sweep)
    inputs = list(sweep_inputs)
    if not args.no_synthetic:
        inputs.extend({"source": "synthetic", **item} for item in SYNTHETIC_INPUTS)

    args.out.write_text(json.dumps(inputs, indent=2) + "\n")
    print(f"Wrote {len(inputs)} inputs to {args.out.relative_to(BENCH_DIR.parent)}")
    print(f"  sweep:     {len(sweep_inputs)} (from {args.sweep})")
    print(f"  synthetic: {len(inputs) - len(sweep_inputs)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
