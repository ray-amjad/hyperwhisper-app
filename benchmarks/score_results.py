#!/usr/bin/env python3
"""
HyperWhisper benchmark — score sweep results.

Pairs every engine's per-sample output against the ElevenLabs Scribe v2
reference for that sample and computes WER. Writes scored.json with
per-engine summary stats and the merged per-result rows.

Run:
    python3 score_results.py <run-id>
    python3 score_results.py latest         # latest run by mtime
"""

import argparse
import json
import re
import sys
from pathlib import Path

BENCH_DIR = Path(__file__).parent
RESULTS_DIR = BENCH_DIR / "results"

REFERENCE_ENGINE_KEY = "cloud-elevenlabs-scribe_v2"

# Words/symbols to drop entirely before WER comparison. Punctuation handled
# separately by re.sub.
DROP_TOKENS = {"uh", "um", "uhm", "ah", "er", "hmm", "mm"}


def normalize(text: str, drop_fillers: bool = False) -> list[str]:
    s = text.lower()
    s = re.sub(r"[^a-z0-9'\s]", " ", s)
    tokens = [t for t in s.split() if t]
    if drop_fillers:
        tokens = [t for t in tokens if t not in DROP_TOKENS]
    return tokens


def wer(ref_tokens: list[str], hyp_tokens: list[str]) -> tuple[float, int, int, int, int]:
    """
    Word error rate via Levenshtein DP. Returns (wer, S, D, I, N).
    N is len(ref). If N==0, returns 0.0 when hyp also empty, else 1.0.
    """
    n, m = len(ref_tokens), len(hyp_tokens)
    if n == 0:
        return (0.0 if m == 0 else 1.0, 0, 0, m, 0)

    # dp[i][j] = edit distance ref[:i] -> hyp[:j], with operation backtrack.
    dp = [[0] * (m + 1) for _ in range(n + 1)]
    bt = [[""] * (m + 1) for _ in range(n + 1)]
    for i in range(n + 1):
        dp[i][0] = i
        bt[i][0] = "D"
    for j in range(m + 1):
        dp[0][j] = j
        bt[0][j] = "I"
    bt[0][0] = ""
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            if ref_tokens[i - 1] == hyp_tokens[j - 1]:
                dp[i][j] = dp[i - 1][j - 1]
                bt[i][j] = "M"
            else:
                sub = dp[i - 1][j - 1] + 1
                dele = dp[i - 1][j] + 1
                ins = dp[i][j - 1] + 1
                best = min(sub, dele, ins)
                dp[i][j] = best
                bt[i][j] = "S" if best == sub else ("D" if best == dele else "I")

    s_count = d_count = i_count = 0
    i, j = n, m
    while i > 0 or j > 0:
        op = bt[i][j]
        if op == "M":
            i -= 1; j -= 1
        elif op == "S":
            s_count += 1; i -= 1; j -= 1
        elif op == "D":
            d_count += 1; i -= 1
        elif op == "I":
            i_count += 1; j -= 1
        else:
            break

    return ((s_count + d_count + i_count) / n, s_count, d_count, i_count, n)


def resolve_run(run_id: str) -> Path:
    if run_id == "latest":
        runs = sorted([p for p in RESULTS_DIR.iterdir() if p.is_dir()],
                      key=lambda p: p.stat().st_mtime, reverse=True)
        if not runs:
            sys.exit("No runs in results/")
        return runs[0]
    p = RESULTS_DIR / run_id
    if not p.is_dir():
        sys.exit(f"Run not found: {p}")
    return p


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("run_id", help="run directory name under results/, or 'latest'")
    ap.add_argument("--drop-fillers", action="store_true",
                    help="strip filler tokens (um, uh) before scoring")
    args = ap.parse_args()

    run_dir = resolve_run(args.run_id)
    summary_path = run_dir / "summary.json"
    if not summary_path.exists():
        sys.exit(f"summary.json missing at {summary_path}")
    summary = json.loads(summary_path.read_text())

    # Build reference map: sample_id -> Scribe v2 text.
    ref_engine = None
    for e in summary["engines"]:
        if e["engine_key"] == REFERENCE_ENGINE_KEY:
            ref_engine = e
            break
    if ref_engine is None:
        sys.exit(f"Reference engine {REFERENCE_ENGINE_KEY} not in run — re-run sweep including elevenlabs.")

    ref_by_sample: dict[str, str] = {}
    for r in ref_engine["results"]:
        if r.get("ok") and r.get("text"):
            ref_by_sample[r["sample_id"]] = r["text"]

    # Merge in manual corrections from review.html (overrides.json).
    overrides_path = run_dir / "overrides.json"
    n_overrides = 0
    if overrides_path.exists():
        overrides = json.loads(overrides_path.read_text())
        for sid, text in overrides.items():
            if text:
                ref_by_sample[sid] = text
                n_overrides += 1

    print(f"Reference: {ref_engine['display']} — {len(ref_by_sample)}/{len(ref_engine['results'])} usable samples"
          + (f" ({n_overrides} hand-corrected)" if n_overrides else ""))

    scored_engines: list[dict] = []
    for e in summary["engines"]:
        rows = []
        per_sample_wers: list[float] = []
        total_S = total_D = total_I = total_N = 0
        for r in e["results"]:
            ref = ref_by_sample.get(r["sample_id"])
            entry = {**r}
            if ref and r.get("ok") and r.get("text") is not None:
                ref_tok = normalize(ref, args.drop_fillers)
                hyp_tok = normalize(r["text"], args.drop_fillers)
                wer_v, S, D, I, N = wer(ref_tok, hyp_tok)
                entry["wer"] = round(wer_v, 4)
                entry["wer_S"] = S
                entry["wer_D"] = D
                entry["wer_I"] = I
                entry["wer_N"] = N
                per_sample_wers.append(wer_v)
                total_S += S; total_D += D; total_I += I; total_N += N
            else:
                entry["wer"] = None
            rows.append(entry)

        avg_wer = (sum(per_sample_wers) / len(per_sample_wers)) if per_sample_wers else None
        agg_wer = ((total_S + total_D + total_I) / total_N) if total_N > 0 else None

        ok_rows = [r for r in e["results"] if r.get("ok")]
        latencies = [r["wall_ms"] for r in ok_rows if r.get("wall_ms") is not None]
        scored_engines.append({
            "engine_key": e["engine_key"],
            "display": e["display"],
            "engine": e["engine"],
            "model": e["model"],
            "provider": e["provider"],
            "n_samples": len(e["results"]),
            "n_ok": len(ok_rows),
            "avg_wer": round(avg_wer, 4) if avg_wer is not None else None,
            "agg_wer": round(agg_wer, 4) if agg_wer is not None else None,
            "p50_latency_ms": round(sorted(latencies)[len(latencies) // 2], 1) if latencies else None,
            "mean_latency_ms": round(sum(latencies) / len(latencies), 1) if latencies else None,
            "is_reference": e["engine_key"] == REFERENCE_ENGINE_KEY,
            "results": rows,
        })

    out = {
        "run_id": run_dir.name,
        "reference_engine_key": REFERENCE_ENGINE_KEY,
        "drop_fillers": args.drop_fillers,
        "samples": summary["samples"],
        "engines": scored_engines,
    }
    out_path = run_dir / "scored.json"
    out_path.write_text(json.dumps(out, indent=2) + "\n")

    # Pretty table.
    print(f"\n{'engine_key':<46} {'n_ok':>5} {'avg_wer':>8} {'p50_ms':>8}")
    print("-" * 70)
    rows_sorted = sorted(
        [e for e in scored_engines if not e["is_reference"]],
        key=lambda x: (x["avg_wer"] if x["avg_wer"] is not None else 99),
    )
    for e in rows_sorted:
        w = f"{e['avg_wer']:.3f}" if e["avg_wer"] is not None else "  -  "
        lat = f"{e['p50_latency_ms']:.0f}" if e["p50_latency_ms"] is not None else "  -  "
        print(f"{e['engine_key']:<46} {e['n_ok']:>5} {w:>8} {lat:>8}")

    print(f"\nWrote {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
