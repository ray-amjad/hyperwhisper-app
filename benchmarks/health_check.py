#!/usr/bin/env python3
"""
HyperWhisper benchmark — API key + local model health check.

Reads every provider's API key from the macOS Keychain (the same storage the
HyperWhisper macOS app uses), probes a $0 endpoint per provider to verify the
key is live, and scans the app's Application Support directory for installed
local models.

Run:
    python3 health_check.py

First run will prompt for Keychain access for each entry — click "Always Allow".
Writes a summary table to stdout and a machine-readable JSON to health.json.
"""

import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Optional

# ---------------------------------------------------------------------------
# Provider list — mirrors KeychainManager.APIKeyType in the macOS app.
# ---------------------------------------------------------------------------
PROVIDERS = [
    ("openai", "OpenAI"),
    ("groq", "Groq"),
    ("fireworks", "Fireworks"),
    ("anthropic", "Anthropic"),
    ("gemini", "Gemini"),
    ("deepgram", "Deepgram"),
    ("assemblyai", "AssemblyAI"),
    ("elevenlabs", "ElevenLabs"),
    ("mistral", "Mistral"),
    ("soniox", "Soniox"),
    ("cerebras", "Cerebras"),
    ("grok", "Grok"),
]

# Keychain service prefixes used by KeychainManager.swift.
# Release builds → com.hyperwhisper.apikeys.<provider>
# DEBUG builds   → com.hyperwhisper.apikeys.dev.<provider>
# Dev base is listed first so the benchmark runs against the same keys
# the dev-built running app uses (release entries can hold older keys
# from previous builds and probe stale).
KEYCHAIN_BASES = [
    "com.hyperwhisper.apikeys.dev",
    "com.hyperwhisper.apikeys",
]

APP_SUPPORT = Path.home() / "Library" / "Application Support" / "HyperWhisper"


# ---------------------------------------------------------------------------
# Keychain access
# ---------------------------------------------------------------------------
def read_key(provider: str) -> tuple[Optional[str], str]:
    """Return (key, source) for a provider, or (None, reason) if not found."""
    for base in KEYCHAIN_BASES:
        service = f"{base}.{provider}"
        try:
            proc = subprocess.run(
                ["security", "find-generic-password",
                 "-s", service, "-a", provider, "-w"],
                capture_output=True, text=True, check=False, timeout=10,
            )
        except FileNotFoundError:
            return None, "`security` CLI missing"
        except subprocess.TimeoutExpired:
            return None, "keychain prompt timeout"

        if proc.returncode == 0:
            key = proc.stdout.strip()
            if key:
                return key, service
        # rc=44 == errSecItemNotFound; try next base. Other errors: surface them.
        if proc.returncode not in (0, 44):
            return None, f"security rc={proc.returncode}: {proc.stderr.strip()}"
    return None, "not in keychain"


# ---------------------------------------------------------------------------
# HTTP probe
# ---------------------------------------------------------------------------
def http_get(url: str, headers: dict, timeout: float = 10.0):
    req = urllib.request.Request(url, headers=headers, method="GET")
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            elapsed_ms = (time.perf_counter() - t0) * 1000
            return resp.status, elapsed_ms, ""
    except urllib.error.HTTPError as e:
        elapsed_ms = (time.perf_counter() - t0) * 1000
        body = ""
        try:
            body = e.read(300).decode("utf-8", errors="replace")
        except Exception:
            pass
        return e.code, elapsed_ms, body[:200]
    except Exception as e:
        elapsed_ms = (time.perf_counter() - t0) * 1000
        return 0, elapsed_ms, f"{type(e).__name__}: {e}"


def _tiny_silence_wav() -> bytes:
    """Return a valid 0.1s 16-bit/16kHz mono PCM WAV (~3.2KB)."""
    import struct
    sample_rate = 16000
    num_samples = 1600
    num_channels = 1
    bits = 16
    byte_rate = sample_rate * num_channels * bits // 8
    block_align = num_channels * bits // 8
    data_size = num_samples * block_align
    riff_size = 36 + data_size
    header = (
        b"RIFF" + struct.pack("<I", riff_size) + b"WAVE"
        + b"fmt " + struct.pack("<IHHIIHH", 16, 1, num_channels,
                                 sample_rate, byte_rate, block_align, bits)
        + b"data" + struct.pack("<I", data_size)
    )
    return header + b"\x00" * data_size


def http_post_multipart(url: str, headers: dict, fields: dict,
                         files: dict, timeout: float = 15.0):
    """POST multipart/form-data. fields: {name: str}, files: {name: (filename, mime, bytes)}."""
    import uuid
    boundary = f"Boundary-{uuid.uuid4().hex}"
    parts: list[bytes] = []
    for name, value in fields.items():
        parts.append(
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode()
        )
    for name, (filename, mime, data) in files.items():
        parts.append(
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{filename}\"\r\nContent-Type: {mime}\r\n\r\n".encode()
            + data + b"\r\n"
        )
    parts.append(f"--{boundary}--\r\n".encode())
    body = b"".join(parts)
    hdrs = dict(headers)
    hdrs["Content-Type"] = f"multipart/form-data; boundary={boundary}"
    req = urllib.request.Request(url, data=body, headers=hdrs, method="POST")
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            ms = (time.perf_counter() - t0) * 1000
            return resp.status, ms, ""
    except urllib.error.HTTPError as e:
        ms = (time.perf_counter() - t0) * 1000
        try:
            txt = e.read(300).decode("utf-8", errors="replace")
        except Exception:
            txt = ""
        return e.code, ms, txt[:200]
    except Exception as e:
        ms = (time.perf_counter() - t0) * 1000
        return 0, ms, f"{type(e).__name__}: {e}"


# Each probe returns (ok: bool, detail: str, latency_ms: float).
# Endpoints chosen to be free / GET-only / listing-style where possible.
def probe(provider: str, key: str) -> tuple[bool, str, float]:
    if provider == "openai":
        s, ms, body = http_get(
            "https://api.openai.com/v1/models",
            {"Authorization": f"Bearer {key}"})
        return s == 200, f"HTTP {s}", ms
    if provider == "groq":
        s, ms, body = http_get(
            "https://api.groq.com/openai/v1/models",
            {"Authorization": f"Bearer {key}"})
        return s == 200, f"HTTP {s}", ms
    if provider == "fireworks":
        s, ms, body = http_get(
            "https://api.fireworks.ai/inference/v1/models",
            {"Authorization": f"Bearer {key}"})
        # Fireworks returns 200 with auth, 401 without.
        return s == 200, f"HTTP {s}", ms
    if provider == "anthropic":
        s, ms, body = http_get(
            "https://api.anthropic.com/v1/models",
            {"x-api-key": key, "anthropic-version": "2023-06-01"})
        return s == 200, f"HTTP {s}", ms
    if provider == "gemini":
        s, ms, body = http_get(
            f"https://generativelanguage.googleapis.com/v1beta/models?key={key}",
            {})
        return s == 200, f"HTTP {s}", ms
    if provider == "deepgram":
        s, ms, body = http_get(
            "https://api.deepgram.com/v1/projects",
            {"Authorization": f"Token {key}"})
        return s == 200, f"HTTP {s}", ms
    if provider == "assemblyai":
        s, ms, body = http_get(
            "https://api.assemblyai.com/v2/transcript?limit=1",
            {"authorization": key})
        return s == 200, f"HTTP {s}", ms
    if provider == "elevenlabs":
        # Use STT endpoint with a 0.1s silent WAV so restricted (STT-only)
        # scoped keys probe as healthy. The old GET /v1/user / /v1/models
        # paths require user_read / models_read scopes that HyperWhisper
        # doesn't ask the user to grant. 402/429 still count as healthy
        # (auth worked, quota/billing is a separate concern).
        s, ms, body = http_post_multipart(
            "https://api.elevenlabs.io/v1/speech-to-text",
            {"xi-api-key": key, "Accept": "application/json"},
            fields={"model_id": "scribe_v1"},
            files={"file": ("silence.wav", "audio/wav", _tiny_silence_wav())},
        )
        ok = (200 <= s < 300) or s in (402, 429)
        return ok, f"HTTP {s}", ms
    if provider == "mistral":
        s, ms, body = http_get(
            "https://api.mistral.ai/v1/models",
            {"Authorization": f"Bearer {key}"})
        return s == 200, f"HTTP {s}", ms
    if provider == "soniox":
        # Soniox has no /models endpoint; /v1/transcriptions?limit=1 returns
        # 200 with valid auth and 401 otherwise. Empty list is fine.
        s, ms, body = http_get(
            "https://api.soniox.com/v1/transcriptions?limit=1",
            {"Authorization": f"Bearer {key}"})
        return s == 200, f"HTTP {s}", ms
    if provider == "cerebras":
        s, ms, body = http_get(
            "https://api.cerebras.ai/v1/models",
            {"Authorization": f"Bearer {key}"})
        return s == 200, f"HTTP {s}", ms
    if provider == "grok":
        s, ms, body = http_get(
            "https://api.x.ai/v1/models",
            {"Authorization": f"Bearer {key}"})
        return s == 200, f"HTTP {s}", ms
    return False, "unknown provider", 0.0


# ---------------------------------------------------------------------------
# Local model scan
# ---------------------------------------------------------------------------
def scan_local_models() -> dict:
    out = {
        "app_support_dir": str(APP_SUPPORT),
        "exists": APP_SUPPORT.exists(),
        "whisper": [],
        "parakeet": [],
        "qwen3_asr": [],
        "local_llm": [],
        "other": [],
    }
    if not APP_SUPPORT.exists():
        return out

    # Whisper ggml-*.bin (typically directly under models/)
    models_dir = APP_SUPPORT / "models"
    if models_dir.exists():
        for p in sorted(models_dir.rglob("*")):
            if not p.is_file():
                continue
            name = p.name
            size_mb = round(p.stat().st_size / (1024 * 1024), 1)
            entry = {"name": name, "path": str(p), "size_mb": size_mb}
            low = name.lower()
            if low.startswith("ggml-") or "whisper" in low:
                out["whisper"].append(entry)
            elif "parakeet" in low:
                out["parakeet"].append(entry)
            elif "qwen" in low and ("asr" in low or "audio" in low):
                out["qwen3_asr"].append(entry)
            elif low.endswith(".gguf"):
                out["local_llm"].append(entry)
            else:
                out["other"].append(entry)

    # Sibling dirs the app uses for engines that need multi-file layouts.
    for sibling, bucket in (
        ("parakeet", "parakeet"),
        ("qwen", "qwen3_asr"),
        ("gemma", "local_llm"),
    ):
        sib = APP_SUPPORT / sibling
        if sib.exists():
            files = [p for p in sib.rglob("*") if p.is_file()]
            if files:
                total_mb = round(sum(p.stat().st_size for p in files) / (1024 * 1024), 1)
                out[bucket].append({
                    "name": f"<{sibling}/> ({len(files)} files)",
                    "path": str(sib),
                    "size_mb": total_mb,
                })
    return out


# ---------------------------------------------------------------------------
# Pretty printer
# ---------------------------------------------------------------------------
def print_table(rows: list[dict]):
    cols = [
        ("Provider", "provider", 12),
        ("Key", "key_present", 6),
        ("Probe", "ok", 7),
        ("Detail", "detail", 18),
        ("Latency", "latency", 10),
        ("Source", "source", 38),
    ]
    header = " | ".join(f"{name:<{w}}" for name, _, w in cols)
    print(header)
    print("-" * len(header))
    for r in rows:
        cells = []
        for _, k, w in cols:
            v = r.get(k, "")
            if isinstance(v, bool):
                v = "✓" if v else "✗"
            v = str(v)
            if len(v) > w:
                v = v[: w - 1] + "…"
            cells.append(f"{v:<{w}}")
        print(" | ".join(cells))


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
def main():
    print("HyperWhisper benchmark — health check\n")
    print("Reading API keys from macOS Keychain. If this is the first run,")
    print("a system dialog will pop up asking permission for each entry —")
    print("click 'Always Allow' so subsequent runs don't prompt.\n")

    rows = []
    for provider, display in PROVIDERS:
        key, source = read_key(provider)
        if not key:
            rows.append({
                "provider": display,
                "provider_key": provider,
                "key_present": False,
                "ok": False,
                "detail": source,
                "latency": "—",
                "source": "",
            })
            continue
        ok, detail, latency_ms = probe(provider, key)
        rows.append({
            "provider": display,
            "provider_key": provider,
            "key_present": True,
            "ok": ok,
            "detail": detail,
            "latency": f"{latency_ms:.0f} ms",
            "source": source,
        })

    print_table(rows)

    print("\nLocal models on disk:")
    locals_ = scan_local_models()
    if not locals_["exists"]:
        print(f"  ✗ Application Support not found: {locals_['app_support_dir']}")
    else:
        for engine in ("whisper", "parakeet", "qwen3_asr", "local_llm"):
            entries = locals_[engine]
            if not entries:
                print(f"  • {engine:<12} — none installed")
                continue
            for e in entries:
                print(f"  • {engine:<12} {e['name']}  ({e['size_mb']} MB)")

    # Summary line
    cloud_ok = sum(1 for r in rows if r["ok"])
    cloud_total = len(rows)
    local_ok = sum(1 for engine in ("whisper", "parakeet", "qwen3_asr", "local_llm")
                   if locals_[engine])
    print(f"\nCloud providers reachable: {cloud_ok}/{cloud_total}")
    print(f"Local engines with models: {local_ok}/4")

    # Write machine-readable summary for downstream tools.
    health_path = Path(__file__).parent / "health.json"
    with health_path.open("w") as f:
        json.dump({"cloud": rows, "local": locals_}, f, indent=2)
    print(f"\nWrote {health_path}")

    # Exit nonzero if everything failed (caller's signal to abort).
    return 0 if cloud_ok + local_ok > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
