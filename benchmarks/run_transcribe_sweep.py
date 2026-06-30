#!/usr/bin/env python3
"""
HyperWhisper benchmark — transcribe sweep.

For every (sample × installed-engine) pair, POSTs /transcribe to the running
Local API server and records the resulting text + latency. Resumable: each
engine writes to its own JSON file under results/<run-id>/.

Run:
    python3 run_transcribe_sweep.py
    python3 run_transcribe_sweep.py --max-duration 60      # skip clips > 60s
    python3 run_transcribe_sweep.py --include-engines whisperlocal,parakeet
    python3 run_transcribe_sweep.py --resume <run-id>

Reads:
    ~/Library/Application Support/HyperWhisper/local-api.json   (port + token)
    benchmarks/samples.json                                     (corpus)
    GET /health, GET /models?installed_only=true               (engine inventory)

Writes:
    benchmarks/results/<run-id>/<engine-key>.json   (per-engine results)
    benchmarks/results/<run-id>/summary.json        (aggregated, written at end)
"""

import argparse
import json
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

BENCH_DIR = Path(__file__).parent
PORT_FILE = Path.home() / "Library" / "Application Support" / "HyperWhisper" / "local-api.json"
SAMPLES_FILE = BENCH_DIR / "samples.json"
RESULTS_DIR = BENCH_DIR / "results"

# Cloud provider IDs accepted by the TranscribeEndpoint (applyEngineModel switch).
CLOUD_PROVIDERS = {
    "openai", "groq", "fireworks", "deepgram", "assemblyai",
    "elevenlabs", "mistral", "soniox", "gemini", "grok",
    "hyperwhisper",  # routed via engine="cloud" path in Swift
}

# Per-request HTTP timeout — accommodates 12-min audio through large-v3.
REQUEST_TIMEOUT_S = 900


def read_port_file() -> dict:
    if not PORT_FILE.exists():
        sys.exit(f"local-api.json not found at {PORT_FILE} — is the API server toggle on?")
    return json.loads(PORT_FILE.read_text())


def api_url(port: int, path: str) -> str:
    return f"http://127.0.0.1:{port}{path}"


def http_get(port: int, token: str, path: str) -> dict:
    req = urllib.request.Request(api_url(port, path),
                                  headers={"Authorization": f"Bearer {token}"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def http_post_json(port: int, token: str, path: str, body: dict,
                   timeout: float = REQUEST_TIMEOUT_S) -> tuple[dict, float]:
    data = json.dumps(body).encode()
    req = urllib.request.Request(
        api_url(port, path),
        data=data,
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
        },
    )
    t0 = time.monotonic()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            wall_ms = (time.monotonic() - t0) * 1000
            payload = json.loads(resp.read())
            return payload, wall_ms
    except urllib.error.HTTPError as e:
        wall_ms = (time.monotonic() - t0) * 1000
        try:
            payload = json.loads(e.read())
        except Exception:
            payload = {"ok": False, "error": {"code": "HTTP_ERROR",
                                              "message": f"HTTP {e.code}"}}
        return payload, wall_ms


def derive_engines(models: list[dict]) -> list[dict]:
    """
    Walk /models response, derive (engine_key, engine, model, display) tuples.
    engine_key is a stable filename-safe identifier.
    """
    engines: list[dict] = []
    for m in models:
        if not m.get("installed", False):
            continue
        if m.get("kind") != "voice":
            continue
        mid = m["id"]
        provider = m.get("provider", "")
        display = m.get("displayName", mid)

        if mid == "apple-speech-analyzer":
            engines.append({"key": "applespeech", "engine": "applespeech",
                            "model": "apple-speech-analyzer", "display": display,
                            "provider": "apple", "model_id": mid})
        elif mid.startswith("cloud-tx-"):
            # cloud-tx-<provider>-<rest>
            rest = mid[len("cloud-tx-"):]
            if "-" not in rest:
                continue
            prov, model = rest.split("-", 1)
            if prov not in CLOUD_PROVIDERS:
                continue
            engines.append({"key": f"cloud-{prov}-{model}",
                            "engine": prov, "model": model,
                            "display": display, "provider": prov,
                            "model_id": mid})
        elif mid.startswith("whisper-"):
            size = mid[len("whisper-"):]
            engines.append({"key": f"whisperlocal-{size}",
                            "engine": "whisperlocal", "model": size,
                            "display": display, "provider": "local",
                            "model_id": mid})
        elif mid.startswith("parakeet-"):
            model = mid[len("parakeet-"):]
            engines.append({"key": f"parakeet-{model}",
                            "engine": "parakeet", "model": model,
                            "display": display, "provider": "local",
                            "model_id": mid})
        elif mid.startswith("qwen3-asr-"):
            model = mid[len("qwen3-asr-"):]
            engines.append({"key": f"qwen3asr-{model}",
                            "engine": "qwen3asr", "model": model,
                            "display": display, "provider": "local",
                            "model_id": mid})
    return engines


def safe_filename(s: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "_", s)


def transcribe_one(port: int, token: str, file_path: str,
                   engine: str, model: str) -> dict:
    body = {"file": file_path, "engine": engine, "model": model, "language": "auto"}
    payload, wall_ms = http_post_json(port, token, "/transcribe", body)
    return {
        "ok": bool(payload.get("ok", False)),
        "text": payload.get("text", ""),
        "language": payload.get("language"),
        "server_latency_ms": payload.get("latency_ms"),
        "wall_ms": round(wall_ms, 1),
        "engine_reported": payload.get("engine"),
        "model_reported": payload.get("model"),
        "error": payload.get("error"),
    }


def run_engine(engine_info: dict, samples: list[dict], port: int, token: str,
               out_path: Path, max_duration: float | None) -> dict:
    results: list[dict] = []
    for i, s in enumerate(samples, 1):
        if max_duration is not None and s["duration_s"] > max_duration:
            print(f"    [{i}/{len(samples)}] skip {s['id']} (dur {s['duration_s']:.1f}s > {max_duration}s)", flush=True)
            continue
        print(f"    [{i}/{len(samples)}] {s['id']} ({s['duration_s']:.1f}s)...", end=" ", flush=True)
        r = transcribe_one(port, token, s["path"],
                            engine_info["engine"], engine_info["model"])
        if r["ok"]:
            print(f"ok {r['wall_ms']:.0f}ms ({len(r['text'])} chars)", flush=True)
        else:
            err = (r.get("error") or {})
            print(f"FAIL {err.get('code', '?')}: {err.get('message', '')[:60]}", flush=True)
        results.append({
            "sample_id": s["id"],
            "sample_path": s["path"],
            "sample_duration_s": s["duration_s"],
            "sample_bucket": s["bucket"],
            **r,
        })

    out = {
        "engine_key": engine_info["key"],
        "engine": engine_info["engine"],
        "model": engine_info["model"],
        "display": engine_info["display"],
        "provider": engine_info["provider"],
        "model_id": engine_info["model_id"],
        "results": results,
    }
    out_path.write_text(json.dumps(out, indent=2) + "\n")
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run-id", default=datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ"),
                    help="run directory name under results/ (default: utc timestamp)")
    ap.add_argument("--resume", metavar="RUN_ID",
                    help="resume an existing run (skip engines that already have a file)")
    ap.add_argument("--max-duration", type=float,
                    help="skip samples longer than this many seconds")
    ap.add_argument("--include-engines",
                    help="comma-separated engine identifiers to include (e.g. whisperlocal,parakeet)")
    ap.add_argument("--exclude-engines",
                    help="comma-separated engine identifiers to exclude")
    ap.add_argument("--include-models", help="substring match against engine_key (model-level filter)")
    args = ap.parse_args()

    if args.resume:
        args.run_id = args.resume

    cfg = read_port_file()
    port, token = cfg["port"], cfg["token"]
    print(f"API server: 127.0.0.1:{port} (pid {cfg.get('pid')})")

    if not SAMPLES_FILE.exists():
        sys.exit(f"samples.json not found at {SAMPLES_FILE} — run select_samples.py first.")
    samples = json.loads(SAMPLES_FILE.read_text())
    print(f"Loaded {len(samples)} samples from {SAMPLES_FILE.name}")

    health = http_get(port, token, "/health")
    if not health.get("ok"):
        sys.exit("GET /health failed")

    models = http_get(port, token, "/models?kind=voice&installed_only=true")["models"]
    engines = derive_engines(models)

    include = set(args.include_engines.split(",")) if args.include_engines else None
    exclude = set(args.exclude_engines.split(",")) if args.exclude_engines else set()

    def keep(e: dict) -> bool:
        if include is not None and e["engine"] not in include:
            return False
        if e["engine"] in exclude:
            return False
        if args.include_models and args.include_models not in e["key"]:
            return False
        return True

    engines = [e for e in engines if keep(e)]
    print(f"Sweeping {len(engines)} engines:")
    for e in engines:
        print(f"  - {e['key']:<50} {e['display']}")

    # Filter cloud engines to providers that look healthy.
    healthy_providers = {p["id"] for p in health.get("providers", [])
                          if p.get("status") == "healthy"}
    healthy_providers.add("local")
    healthy_providers.add("apple")
    skipped = [e for e in engines if e["provider"] not in healthy_providers]
    engines = [e for e in engines if e["provider"] in healthy_providers]
    for e in skipped:
        print(f"  skip {e['key']} (provider {e['provider']} not healthy)")

    run_dir = RESULTS_DIR / args.run_id
    run_dir.mkdir(parents=True, exist_ok=True)
    print(f"Writing results to {run_dir}")

    summary_engines: list[dict] = []
    for idx, e in enumerate(engines, 1):
        out_path = run_dir / f"{safe_filename(e['key'])}.json"
        if out_path.exists() and args.resume:
            print(f"\n[{idx}/{len(engines)}] {e['key']} — already done, loading", flush=True)
            summary_engines.append(json.loads(out_path.read_text()))
            continue
        print(f"\n[{idx}/{len(engines)}] {e['key']} ({e['display']})", flush=True)
        result = run_engine(e, samples, port, token, out_path, args.max_duration)
        summary_engines.append(result)

    summary = {
        "run_id": args.run_id,
        "started_at": datetime.now(timezone.utc).isoformat(),
        "api_port": port,
        "samples_file": str(SAMPLES_FILE),
        "samples": samples,
        "engines": summary_engines,
    }
    (run_dir / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(f"\nWrote summary: {run_dir / 'summary.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
