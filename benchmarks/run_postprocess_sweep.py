#!/usr/bin/env python3
"""
HyperWhisper benchmark — post-processing sweep.

For every (input × installed text model × preset) triple, POSTs
/post-process to the running Local API server and records the resulting
text + latency. Resumable: each (engine, preset) pair writes to its own
JSON file under results/<run-id>/.

Run:
    python3 run_postprocess_sweep.py --run-id pp-01
    python3 run_postprocess_sweep.py --resume pp-01
    python3 run_postprocess_sweep.py --presets hyper,note,code
    python3 run_postprocess_sweep.py --include-providers openai,anthropic
    python3 run_postprocess_sweep.py --max-input-chars 800   # skip long inputs (helpful for slow local LLMs)

Reads:
    ~/Library/Application Support/HyperWhisper/local-api.json   (port + token)
    benchmarks/pp_inputs.json                                   (corpus)
    GET /health, GET /models?kind=text&installed_only=true     (engine inventory)

Writes:
    benchmarks/results/<run-id>/<provider>-<model>-<preset>.json
    benchmarks/results/<run-id>/summary.json
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
INPUTS_FILE = BENCH_DIR / "pp_inputs.json"
RESULTS_DIR = BENCH_DIR / "results"

REQUEST_TIMEOUT_S = 600

# `hyperwhisper-cloud` routes through HyperWhisper's own platform layer
# rather than a specific provider/model. Benchmarking it here would
# conflate the routing layer with the underlying model. Skip by default;
# pass --include-hyperwhisper-cloud to opt in.
DEFAULT_SKIP_MODEL_IDS = {"hyperwhisper-cloud"}


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
            return json.loads(resp.read()), wall_ms
    except urllib.error.HTTPError as e:
        wall_ms = (time.monotonic() - t0) * 1000
        try:
            payload = json.loads(e.read())
        except Exception:
            payload = {"ok": False, "error": {"code": "HTTP_ERROR",
                                              "message": f"HTTP {e.code}"}}
        return payload, wall_ms


def derive_engines(models: list[dict], skip_model_ids: set[str]) -> list[dict]:
    """
    Walk /models?kind=text response, derive (key, provider, model_id, display).
    Cloud post-processing models use id 'pp-<provider>-<modelId>'.
    Local LLMs use 'local-llm-<filename>' and report provider='local_llm'.
    """
    engines: list[dict] = []
    for m in models:
        if m.get("kind") != "text":
            continue
        if not m.get("installed", True):
            continue
        mid = m["id"]
        if mid.startswith("pp-"):
            rest = mid[len("pp-"):]
            if "-" not in rest:
                continue
            provider, model_id = rest.split("-", 1)
        elif mid.startswith("local-llm-"):
            provider = m.get("provider", "local_llm")
            model_id = mid[len("local-llm-"):]
        else:
            continue
        if model_id in skip_model_ids:
            continue
        engines.append({
            "key": f"{provider}-{model_id}",
            "provider": provider,
            "model": model_id,
            "display": m.get("displayName", model_id),
            "library_id": mid,
        })
    return engines


def safe_filename(s: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "_", s)


def post_process_one(port: int, token: str, text: str,
                     provider: str, model: str, preset: str) -> dict:
    body = {
        "text": text,
        "preset": preset,
        "provider": provider,
        "model": model,
    }
    payload, wall_ms = http_post_json(port, token, "/post-process", body)
    return {
        "ok": bool(payload.get("ok", False)),
        "text": payload.get("text", ""),
        "server_latency_ms": payload.get("latency_ms"),
        "wall_ms": round(wall_ms, 1),
        "provider_reported": payload.get("provider"),
        "model_reported": payload.get("model"),
        "preset_reported": payload.get("preset"),
        "error": payload.get("error"),
    }


def run_engine_preset(engine: dict, preset: str, inputs: list[dict],
                       port: int, token: str, out_path: Path,
                       max_input_chars: int | None) -> dict:
    results: list[dict] = []
    for i, inp in enumerate(inputs, 1):
        text = inp["text"]
        if max_input_chars is not None and len(text) > max_input_chars:
            print(f"    [{i}/{len(inputs)}] skip {inp['id']} ({len(text)} chars > {max_input_chars})", flush=True)
            continue
        print(f"    [{i}/{len(inputs)}] {inp['id']} ({len(text)} chars)...", end=" ", flush=True)
        r = post_process_one(port, token, text, engine["provider"],
                              engine["model"], preset)
        if r["ok"]:
            print(f"ok {r['wall_ms']:.0f}ms ({len(r['text'])} chars)", flush=True)
        else:
            err = (r.get("error") or {})
            print(f"FAIL {err.get('code', '?')}: {err.get('message', '')[:60]}", flush=True)
        results.append({
            "input_id": inp["id"],
            "input_bucket": inp.get("bucket"),
            "input_source": inp.get("source"),
            "input_chars": len(text),
            **r,
        })

    out = {
        "engine_key": engine["key"],
        "provider": engine["provider"],
        "model": engine["model"],
        "preset": preset,
        "display": engine["display"],
        "library_id": engine["library_id"],
        "results": results,
    }
    out_path.write_text(json.dumps(out, indent=2) + "\n")
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run-id",
                    default=datetime.now(timezone.utc).strftime("pp-%Y%m%dT%H%M%SZ"),
                    help="run directory name under results/ (default: pp-<utc timestamp>)")
    ap.add_argument("--resume", metavar="RUN_ID",
                    help="resume an existing run (skip engine/preset pairs already on disk)")
    ap.add_argument("--presets", default="hyper",
                    help="comma-separated presets to run (default: hyper). e.g. 'hyper,note,code'")
    ap.add_argument("--include-providers",
                    help="comma-separated post-processing providers to include")
    ap.add_argument("--exclude-providers",
                    help="comma-separated post-processing providers to exclude")
    ap.add_argument("--include-models",
                    help="substring match against <provider>-<model_id>")
    ap.add_argument("--max-input-chars", type=int,
                    help="skip inputs longer than this many characters (useful for slow local LLMs)")
    ap.add_argument("--include-hyperwhisper-cloud", action="store_true",
                    help="include the hyperwhisper-cloud routing entry (skipped by default)")
    args = ap.parse_args()

    if args.resume:
        args.run_id = args.resume

    cfg = read_port_file()
    port, token = cfg["port"], cfg["token"]
    print(f"API server: 127.0.0.1:{port} (pid {cfg.get('pid')})")

    if not INPUTS_FILE.exists():
        sys.exit(f"pp_inputs.json not found at {INPUTS_FILE} — run pp_pick_inputs.py first.")
    inputs = json.loads(INPUTS_FILE.read_text())
    print(f"Loaded {len(inputs)} inputs from {INPUTS_FILE.name}")

    presets = [p.strip() for p in args.presets.split(",") if p.strip()]
    print(f"Presets: {', '.join(presets)}")

    health = http_get(port, token, "/health")
    if not health.get("ok"):
        sys.exit("GET /health failed")

    skip_model_ids = set() if args.include_hyperwhisper_cloud else set(DEFAULT_SKIP_MODEL_IDS)
    models = http_get(port, token, "/models?kind=text&installed_only=true")["models"]
    engines = derive_engines(models, skip_model_ids)

    include = set(args.include_providers.split(",")) if args.include_providers else None
    exclude = set(args.exclude_providers.split(",")) if args.exclude_providers else set()

    def keep(e: dict) -> bool:
        if include is not None and e["provider"] not in include:
            return False
        if e["provider"] in exclude:
            return False
        if args.include_models and args.include_models not in e["key"]:
            return False
        return True

    engines = [e for e in engines if keep(e)]
    print(f"Sweeping {len(engines)} engines × {len(presets)} preset(s) = {len(engines) * len(presets)} runs:")
    for e in engines:
        print(f"  - {e['key']:<50} {e['display']}")

    # Filter to providers that look healthy. ModelsEndpoint returns only
    # `text` models, so the relevant health pool is
    # health.post_processing_providers (mirrors how the transcribe sweep
    # uses health.providers). We accept both shapes for forward-compat.
    pp_health = health.get("post_processing_providers") or health.get("postProcessingProviders") or []
    healthy_providers = {p["id"] for p in pp_health if p.get("status") == "healthy"}
    # `local_llm` and `hyperwhisper` (routing layer) don't show up in the
    # cloud-health list but ship as part of the app — treat them healthy.
    healthy_providers.update({"local_llm", "hyperwhisper"})
    skipped = [e for e in engines if e["provider"] not in healthy_providers]
    engines = [e for e in engines if e["provider"] in healthy_providers]
    for e in skipped:
        print(f"  skip {e['key']} (provider {e['provider']} not healthy)")

    run_dir = RESULTS_DIR / args.run_id
    run_dir.mkdir(parents=True, exist_ok=True)
    print(f"Writing results to {run_dir}")

    summary_runs: list[dict] = []
    total = len(engines) * len(presets)
    idx = 0
    for e in engines:
        for preset in presets:
            idx += 1
            key = f"{e['key']}-{preset}"
            out_path = run_dir / f"{safe_filename(key)}.json"
            if out_path.exists() and args.resume:
                print(f"\n[{idx}/{total}] {key} — already done, loading", flush=True)
                summary_runs.append(json.loads(out_path.read_text()))
                continue
            print(f"\n[{idx}/{total}] {key} ({e['display']})", flush=True)
            result = run_engine_preset(e, preset, inputs, port, token,
                                        out_path, args.max_input_chars)
            summary_runs.append(result)

    summary = {
        "run_id": args.run_id,
        "started_at": datetime.now(timezone.utc).isoformat(),
        "api_port": port,
        "inputs_file": str(INPUTS_FILE),
        "inputs": inputs,
        "presets": presets,
        "engines": summary_runs,
    }
    (run_dir / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(f"\nWrote summary: {run_dir / 'summary.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
