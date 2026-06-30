#!/usr/bin/env python3
"""
HyperWhisper benchmark — sample picker.

Scans the two recording stores the app writes to, buckets every audio file by
duration, and picks N random samples from each bucket. SHA256-pins the picks
so the corpus is stable across re-runs even after model updates.

Run:
    python3 select_samples.py                 # default: 3 per bucket, seed=42
    python3 select_samples.py --n 5 --seed 7
    python3 select_samples.py --include-m4a   # also consider .m4a (default: wav only)

Writes benchmarks/samples.json:
    [{"id": "<sha256[:12]>", "path": "...", "format": "wav",
      "duration_s": 4.2, "size_bytes": 134510, "sha256": "...",
      "bucket": "lt5s"}, ...]
"""

import argparse
import hashlib
import json
import random
import subprocess
import sys
from pathlib import Path
from typing import Optional

RECORDING_DIRS = [
    Path.home() / "Documents" / "hyperwhisper" / "recordings",
    Path.home() / "Library" / "Application Support" / "HyperWhisper" / "recordings",
]

BUCKETS = [
    ("lt5s",      0.0,    5.0),
    ("5to20s",    5.0,   20.0),
    ("20to60s",  20.0,   60.0),
    ("gt60s",    60.0,   float("inf")),
]


def probe_duration(path: Path) -> Optional[float]:
    """Return duration in seconds via ffprobe, or None on failure."""
    try:
        out = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries", "format=duration",
             "-of", "default=noprint_wrappers=1:nokey=1", str(path)],
            capture_output=True, text=True, timeout=10,
        )
        if out.returncode != 0:
            return None
        return float(out.stdout.strip())
    except (ValueError, subprocess.TimeoutExpired, FileNotFoundError):
        return None


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def bucket_for(duration: float) -> Optional[str]:
    for name, lo, hi in BUCKETS:
        if lo <= duration < hi:
            return name
    return None


def gather_candidates(include_m4a: bool) -> list[dict]:
    exts = {".wav"}
    if include_m4a:
        exts.add(".m4a")

    candidates: list[dict] = []
    for root in RECORDING_DIRS:
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if path.suffix.lower() not in exts:
                continue
            if path.name.startswith(".incomplete_") or path.name.startswith("."):
                continue
            if not path.is_file():
                continue
            dur = probe_duration(path)
            if dur is None or dur <= 0:
                continue
            b = bucket_for(dur)
            if b is None:
                continue
            candidates.append({
                "path": str(path),
                "duration_s": round(dur, 3),
                "size_bytes": path.stat().st_size,
                "format": path.suffix.lower().lstrip("."),
                "bucket": b,
            })
    return candidates


def pick(candidates: list[dict], n_per_bucket: int, rng: random.Random) -> list[dict]:
    by_bucket: dict[str, list[dict]] = {b[0]: [] for b in BUCKETS}
    for c in candidates:
        by_bucket[c["bucket"]].append(c)

    picked: list[dict] = []
    for name, _, _ in BUCKETS:
        pool = by_bucket[name]
        if not pool:
            print(f"  [{name}] no candidates", file=sys.stderr)
            continue
        k = min(n_per_bucket, len(pool))
        chosen = rng.sample(pool, k)
        print(f"  [{name}] picked {k}/{len(pool)}", file=sys.stderr)
        picked.extend(chosen)
    return picked


def finalize(picked: list[dict]) -> list[dict]:
    out = []
    for p in picked:
        digest = sha256_file(Path(p["path"]))
        out.append({
            "id": digest[:12],
            "path": p["path"],
            "format": p["format"],
            "duration_s": p["duration_s"],
            "size_bytes": p["size_bytes"],
            "sha256": digest,
            "bucket": p["bucket"],
        })
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="Pick benchmark audio samples.")
    ap.add_argument("--n", type=int, default=3, help="samples per bucket (default: 3)")
    ap.add_argument("--seed", type=int, default=42, help="RNG seed (default: 42)")
    ap.add_argument("--include-m4a", action="store_true",
                    help="also consider .m4a files (default: wav only)")
    ap.add_argument("--out", type=Path,
                    default=Path(__file__).parent / "samples.json",
                    help="output path (default: benchmarks/samples.json)")
    args = ap.parse_args()

    print(f"Scanning recording dirs (include_m4a={args.include_m4a})...", file=sys.stderr)
    candidates = gather_candidates(args.include_m4a)
    print(f"  found {len(candidates)} candidates", file=sys.stderr)

    rng = random.Random(args.seed)
    picked = pick(candidates, args.n, rng)
    print(f"Hashing {len(picked)} picks...", file=sys.stderr)
    samples = finalize(picked)

    args.out.write_text(json.dumps(samples, indent=2) + "\n")
    print(f"\nWrote {len(samples)} samples to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
