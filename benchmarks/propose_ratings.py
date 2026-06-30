#!/usr/bin/env python3
"""
Convert benchmark WER + latency into proposed 1-5 bar Speed/Accuracy ratings.
Reads results/<run-id>/scored.json. Prints a markdown table by family.
"""
import json
import sys
from pathlib import Path

run_id = sys.argv[1] if len(sys.argv) > 1 else "sweep-01"
scored = json.loads((Path(__file__).parent / "results" / run_id / "scored.json").read_text())

# Buckets — calibrated against the actual distribution in this run.
def speed_bars(p50_ms):
    if p50_ms is None: return None
    if p50_ms < 700: return 5
    if p50_ms < 2000: return 4
    if p50_ms < 3500: return 3
    if p50_ms < 5500: return 2
    return 1

def accuracy_bars(wer):
    if wer is None: return None
    if wer < 0.05: return 5
    if wer < 0.08: return 4
    if wer < 0.12: return 3
    if wer < 0.18: return 2
    return 1

# Group by family for the table.
FAMILIES = {
    "Cloud — OpenAI":        lambda e: e["provider"] == "openai",
    "Cloud — Groq":          lambda e: e["provider"] == "groq",
    "Cloud — Fireworks":     lambda e: e["provider"] == "fireworks",
    "Cloud — Deepgram":      lambda e: e["provider"] == "deepgram",
    "Cloud — AssemblyAI":    lambda e: e["provider"] == "assemblyai",
    "Cloud — ElevenLabs":    lambda e: e["provider"] == "elevenlabs",
    "Cloud — Mistral":       lambda e: e["provider"] == "mistral",
    "Cloud — Soniox":        lambda e: e["provider"] == "soniox",
    "Cloud — Gemini":        lambda e: e["provider"] == "gemini",
    "Apple":                 lambda e: e["provider"] == "apple",
    "Local — Whisper":       lambda e: e["engine"] == "whisperlocal",
    "Local — Parakeet":      lambda e: e["engine"] == "parakeet",
    "Local — Qwen3 ASR":     lambda e: e["engine"] == "qwen3asr",
}

print("| model | n_ok | WER | p50 ms | accuracy | speed |")
print("|---|---|---|---|---|---|")
for fam, pred in FAMILIES.items():
    rows = [e for e in scored["engines"] if pred(e)]
    rows.sort(key=lambda e: (e["avg_wer"] if e["avg_wer"] is not None else 99))
    if not rows: continue
    print(f"| **{fam}** | | | | | |")
    for e in rows:
        wer = e["avg_wer"]
        p50 = e["p50_latency_ms"]
        is_ref = " ← reference" if e.get("is_reference") else ""
        wer_s = f"{wer*100:.1f}%" if wer is not None else "—"
        p50_s = f"{int(p50)}" if p50 is not None else "—"
        a = accuracy_bars(wer)
        s = speed_bars(p50)
        if e.get("is_reference"):
            a = 5  # reference is 5 by definition
        a_s = "█"*a + "░"*(5-a) if a else "—"
        s_s = "█"*s + "░"*(5-s) if s else "—"
        print(f"| {e['display']}{is_ref} | {e['n_ok']} | {wer_s} | {p50_s} | {a_s} ({a}) | {s_s} ({s}) |")

print()
print("**Buckets:**")
print("- Speed (p50): 5 <700ms · 4 700-2000ms · 3 2000-3500ms · 2 3500-5500ms · 1 >5500ms")
print("- Accuracy (WER vs hand-corrected Scribe v2 reference): 5 <5% · 4 5-8% · 3 8-12% · 2 12-18% · 1 >18%")
