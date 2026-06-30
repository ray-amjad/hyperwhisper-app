#!/usr/bin/env python3
"""
Convert post-processing benchmark WER + latency into proposed 1-5 bar
Speed/Accuracy ratings for ModelLibraryManager.postProcessingRatings.

Reads results/<run-id>/scored.json. Prints a markdown table grouped by
provider, plus a Swift-paste-ready dict literal at the end.
"""

import argparse
import json
import sys
from pathlib import Path

BENCH_DIR = Path(__file__).parent

# Same latency buckets as cloud transcription — most post-processing LLM
# calls land in the same 0.5–6s wall range.
def speed_bars(p50_ms):
    if p50_ms is None: return None
    if p50_ms < 700: return 5
    if p50_ms < 2000: return 4
    if p50_ms < 3500: return 3
    if p50_ms < 5500: return 2
    return 1

# Accuracy is WER vs Claude's hyper-preset reference. Buckets are tighter
# than transcription's because post-processing outputs should be close to
# the reference token-for-token (the preset is conservative).
def accuracy_bars(wer):
    if wer is None: return None
    if wer < 0.08: return 5
    if wer < 0.15: return 4
    if wer < 0.25: return 3
    if wer < 0.40: return 2
    return 1


PROVIDER_LABELS = {
    "openai":      "OpenAI",
    "anthropic":   "Anthropic",
    "gemini":      "Gemini",
    "groq":        "Groq",
    "grok":        "Grok (xAI)",
    "cerebras":    "Cerebras",
    "local_llm":   "Local LLM",
    "hyperwhisper": "HyperWhisper (routed)",
}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("run_id")
    ap.add_argument("--preset", default="hyper",
                    help="filter to a single preset (default: hyper). post-processing "
                         "ratings shown in the model library are derived from a single "
                         "preset's run.")
    args = ap.parse_args()

    scored_path = BENCH_DIR / "results" / args.run_id / "scored.json"
    if not scored_path.exists():
        sys.exit(f"scored.json missing at {scored_path}")
    scored = json.loads(scored_path.read_text())

    rows = [e for e in scored["engines"] if e.get("preset") == args.preset]
    if not rows:
        sys.exit(f"No rows for preset '{args.preset}'. Available: "
                  + ", ".join(sorted({e['preset'] for e in scored['engines']})))

    print(f"# Post-processing ratings — run {args.run_id}, preset '{args.preset}'\n")
    print("| model | n_ok | WER vs hyper-ref | p50 ms | accuracy | speed |")
    print("|---|---|---|---|---|---|")

    by_provider: dict[str, list[dict]] = {}
    for e in rows:
        by_provider.setdefault(e["provider"], []).append(e)

    # Stable provider ordering.
    ordered_providers = [p for p in PROVIDER_LABELS if p in by_provider]
    ordered_providers += sorted(p for p in by_provider if p not in PROVIDER_LABELS)

    swift_entries: list[tuple[str, int, int, str]] = []  # (model, speed, accuracy, display)

    for provider in ordered_providers:
        models = by_provider[provider]
        models.sort(key=lambda e: (e["avg_wer"] if e["avg_wer"] is not None else 99))
        label = PROVIDER_LABELS.get(provider, provider)
        print(f"| **{label}** | | | | | |")
        for e in models:
            wer = e["avg_wer"]
            p50 = e["p50_latency_ms"]
            wer_s = f"{wer*100:.1f}%" if wer is not None else "—"
            p50_s = f"{int(p50)}" if p50 is not None else "—"
            a = accuracy_bars(wer)
            s = speed_bars(p50)
            a_s = "█"*a + "░"*(5-a) if a else "—"
            s_s = "█"*s + "░"*(5-s) if s else "—"
            print(f"| {e['display']} | {e['n_ok']} | {wer_s} | {p50_s} | {a_s} ({a}) | {s_s} ({s}) |")
            if a is not None and s is not None:
                swift_entries.append((e["model"], s, a, e["display"]))

    print()
    print("**Buckets:**")
    print("- Speed (p50): 5 <700ms · 4 700-2000ms · 3 2000-3500ms · 2 3500-5500ms · 1 >5500ms")
    print("- Accuracy (WER vs hyper-preset reference): 5 <8% · 4 8-15% · 3 15-25% · 2 25-40% · 1 >40%")

    print()
    print("## Swift dict literal — paste into `ModelLibraryManager.postProcessingRatings`")
    print()
    print("```swift")
    print("private static let postProcessingRatings: [String: (speed: Int, accuracy: Int)] = [")
    width = max((len(f'"{m}":') for m, _, _, _ in swift_entries), default=0)
    for model, speed, acc, display in swift_entries:
        pad = " " * (width - len(f'"{model}":') + 1)
        print(f'    "{model}":{pad}({speed}, {acc}),  // {display}')
    print("]")
    print("```")
    return 0


if __name__ == "__main__":
    sys.exit(main())
