#!/usr/bin/env python3
"""
HyperWhisper benchmark — score post-processing sweep results.

Pairs every (engine, preset) output against the gold-standard reference
in pp_references.json for that input and computes WER. Writes scored.json
with per-(engine, preset) summary stats.

The reference is the cleaned text Claude produces by applying the
`hyper` preset prompt itself — same role hand-corrected Scribe v2 plays
for /transcribe scoring.

Run:
    python3 score_pp_results.py <run-id>
    python3 score_pp_results.py latest

The CLEANED/END wrappers from the hyper preset (if any model leaks
them into its output) are stripped before scoring, so an "honest"
strict-format model isn't penalised.
"""

import argparse
import json
import re
import sys
from pathlib import Path

BENCH_DIR = Path(__file__).parent
RESULTS_DIR = BENCH_DIR / "results"
REFERENCES_FILE = BENCH_DIR / "pp_references.json"

CLEANED_RE = re.compile(r"<<CLEANED>>\s*(.*?)\s*<<END>>", re.DOTALL)


def strip_wrappers(text: str) -> str:
    """If the model dutifully wrapped its output in <<CLEANED>>...<<END>>,
    pull the body out. Otherwise return the text unchanged."""
    m = CLEANED_RE.search(text)
    return m.group(1) if m else text


def normalize(text: str) -> list[str]:
    s = strip_wrappers(text).lower()
    s = re.sub(r"[^a-z0-9'\s]", " ", s)
    return [t for t in s.split() if t]


def wer(ref_tokens: list[str], hyp_tokens: list[str]) -> tuple[float, int, int, int, int]:
    """Word error rate via Levenshtein DP. Returns (wer, S, D, I, N)."""
    n, m = len(ref_tokens), len(hyp_tokens)
    if n == 0:
        return (0.0 if m == 0 else 1.0, 0, 0, m, 0)

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
        runs = sorted([p for p in RESULTS_DIR.iterdir()
                        if p.is_dir() and p.name.startswith("pp-")],
                      key=lambda p: p.stat().st_mtime, reverse=True)
        if not runs:
            sys.exit("No pp-* runs in results/")
        return runs[0]
    p = RESULTS_DIR / run_id
    if not p.is_dir():
        sys.exit(f"Run not found: {p}")
    return p


def load_references() -> dict[str, str]:
    if not REFERENCES_FILE.exists():
        sys.exit(f"pp_references.json missing at {REFERENCES_FILE}")
    payload = json.loads(REFERENCES_FILE.read_text())
    refs = payload.get("references") or {}
    if not refs:
        sys.exit("pp_references.json contains no references")
    return refs


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("run_id", help="run directory under results/, or 'latest'")
    args = ap.parse_args()

    run_dir = resolve_run(args.run_id)
    summary_path = run_dir / "summary.json"
    if not summary_path.exists():
        sys.exit(f"summary.json missing at {summary_path}")
    summary = json.loads(summary_path.read_text())
    refs = load_references()

    ref_tokens_by_id: dict[str, list[str]] = {
        sid: normalize(text) for sid, text in refs.items()
    }

    print(f"Reference: pp_references.json — {len(refs)} inputs")

    scored_engines: list[dict] = []
    for e in summary["engines"]:
        rows = []
        per_input_wers: list[float] = []
        total_S = total_D = total_I = total_N = 0
        for r in e["results"]:
            ref_tok = ref_tokens_by_id.get(r["input_id"])
            entry = {**r}
            if ref_tok is not None and r.get("ok") and r.get("text") is not None:
                hyp_tok = normalize(r["text"])
                wer_v, S, D, I, N = wer(ref_tok, hyp_tok)
                entry["wer"] = round(wer_v, 4)
                entry["wer_S"] = S
                entry["wer_D"] = D
                entry["wer_I"] = I
                entry["wer_N"] = N
                per_input_wers.append(wer_v)
                total_S += S; total_D += D; total_I += I; total_N += N
            else:
                entry["wer"] = None
            rows.append(entry)

        avg_wer = (sum(per_input_wers) / len(per_input_wers)) if per_input_wers else None
        agg_wer = ((total_S + total_D + total_I) / total_N) if total_N > 0 else None

        ok_rows = [r for r in e["results"] if r.get("ok")]
        latencies = [r["wall_ms"] for r in ok_rows if r.get("wall_ms") is not None]
        scored_engines.append({
            "engine_key": e["engine_key"],
            "display": e["display"],
            "provider": e["provider"],
            "model": e["model"],
            "preset": e["preset"],
            "library_id": e.get("library_id"),
            "n_inputs": len(e["results"]),
            "n_ok": len(ok_rows),
            "avg_wer": round(avg_wer, 4) if avg_wer is not None else None,
            "agg_wer": round(agg_wer, 4) if agg_wer is not None else None,
            "p50_latency_ms": round(sorted(latencies)[len(latencies) // 2], 1) if latencies else None,
            "mean_latency_ms": round(sum(latencies) / len(latencies), 1) if latencies else None,
            "results": rows,
        })

    out = {
        "run_id": run_dir.name,
        "reference_source": "pp_references.json",
        "presets": summary.get("presets"),
        "inputs": summary["inputs"],
        "engines": scored_engines,
    }
    out_path = run_dir / "scored.json"
    out_path.write_text(json.dumps(out, indent=2) + "\n")

    # Pretty table.
    print(f"\n{'engine_key':<46} {'preset':<8} {'n_ok':>5} {'avg_wer':>8} {'p50_ms':>8}")
    print("-" * 80)
    rows_sorted = sorted(scored_engines,
                          key=lambda x: (x["avg_wer"] if x["avg_wer"] is not None else 99))
    for e in rows_sorted:
        w = f"{e['avg_wer']:.3f}" if e["avg_wer"] is not None else "  -  "
        lat = f"{e['p50_latency_ms']:.0f}" if e["p50_latency_ms"] is not None else "  -  "
        print(f"{e['engine_key']:<46} {e['preset']:<8} {e['n_ok']:>5} {w:>8} {lat:>8}")

    print(f"\nWrote {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
