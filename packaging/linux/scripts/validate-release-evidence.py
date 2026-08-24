#!/usr/bin/env python3
"""Fail closed unless a Linux release-evidence manifest satisfies every gate."""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import urlparse


MATRIX_IDS = {
    "ubuntu-22.04-gnome-wayland",
    "ubuntu-22.04-gnome-xorg",
    "debian-12-kde-wayland",
    "physical-gpu",
}
SHA256 = re.compile(r"[0-9a-f]{64}")
COMMIT = re.compile(r"[0-9a-f]{40}")


def fail(message: str) -> None:
    raise ValueError(message)


def require_https(value: object, field: str, host: str | None = None) -> str:
    if not isinstance(value, str):
        fail(f"{field} must be an HTTPS URL")
    parsed = urlparse(value)
    if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password:
        fail(f"{field} must be an HTTPS URL without embedded credentials")
    if host is not None and parsed.hostname != host:
        fail(f"{field} must use {host}")
    return value


def validate(path: Path, expected_version: str) -> str:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"could not read release evidence: {error}")

    if not isinstance(document, dict) or document.get("schemaVersion") != 1:
        fail("schemaVersion must be 1")
    if document.get("version") != expected_version:
        fail("evidence version does not match the release")
    tested_commit = document.get("testedCommit")
    if not isinstance(tested_commit, str) or not COMMIT.fullmatch(tested_commit):
        fail("testedCommit must be a 40-character lowercase Git commit")
    if not isinstance(document.get("reviewedBy"), str) or not document["reviewedBy"].strip():
        fail("reviewedBy is required")
    if not isinstance(document.get("reviewedAtUtc"), str) or not re.fullmatch(
        r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", document["reviewedAtUtc"]
    ):
        fail("reviewedAtUtc must be an ISO-8601 UTC timestamp")
    require_https(document.get("gnomeExtensionUrl"), "gnomeExtensionUrl", "extensions.gnome.org")

    environments = document.get("environments")
    if not isinstance(environments, list) or len(environments) != len(MATRIX_IDS):
        fail("environments must contain exactly the three desktop gates and physical-gpu")
    by_id: dict[str, dict] = {}
    for entry in environments:
        if not isinstance(entry, dict) or not isinstance(entry.get("id"), str):
            fail("every environment must be an object with an id")
        identifier = entry["id"]
        if identifier in by_id:
            fail(f"duplicate environment id: {identifier}")
        by_id[identifier] = entry
        if entry.get("result") != "PASS":
            fail(f"{identifier} is not PASS")
        require_https(entry.get("evidenceUrl"), f"{identifier}.evidenceUrl")
        digest = entry.get("sha256")
        if not isinstance(digest, str) or not SHA256.fullmatch(digest):
            fail(f"{identifier}.sha256 must be 64 lowercase hexadecimal characters")
    if set(by_id) != MATRIX_IDS:
        fail("environment ids do not match the mandatory release matrix")

    gpu = by_id["physical-gpu"]
    if gpu.get("architecture") != "x86_64":
        fail("physical-gpu architecture must be x86_64")
    if gpu.get("vulkanInference") is not True:
        fail("physical Vulkan inference must pass")
    if gpu.get("softwareRendererRejected") is not True:
        fail("the evidence must explicitly reject software Vulkan")
    vendor = gpu.get("gpuVendor")
    if not isinstance(vendor, str) or not vendor.strip():
        fail("physical-gpu.gpuVendor is required")
    cuda = gpu.get("cuda12Inference")
    if vendor.strip().lower() == "nvidia" and cuda is not True:
        fail("CUDA 12 inference must pass on an NVIDIA release host")
    if cuda not in (True, None):
        fail("cuda12Inference must be true or null when not applicable")
    if cuda is None and (not isinstance(gpu.get("cuda12NotApplicableReason"), str)
                         or not gpu["cuda12NotApplicableReason"].strip()):
        fail("a CUDA 12 not-applicable reason is required")
    return tested_commit


def verify_release_revision(tested_commit: str, release_commit: str, repository: Path) -> None:
    if not COMMIT.fullmatch(release_commit):
        fail("release commit must be a 40-character lowercase Git commit")
    ancestor = subprocess.run(
        ["git", "-C", str(repository), "merge-base", "--is-ancestor", tested_commit, release_commit],
        check=False,
    )
    if ancestor.returncode != 0:
        fail("testedCommit is not an ancestor of the release commit")
    changed = subprocess.run(
        [
            "git", "-C", str(repository), "diff", "--quiet", tested_commit, release_commit,
            "--", ".", ":(exclude)packaging/linux/release-evidence/**",
        ],
        check=False,
    )
    if changed.returncode != 0:
        fail("release code differs from the tested commit outside release-evidence manifests")


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: validate-release-evidence.py MANIFEST VERSION COMMIT", file=sys.stderr)
        return 2
    try:
        path = Path(sys.argv[1]).resolve()
        tested_commit = validate(path, sys.argv[2])
        verify_release_revision(tested_commit, sys.argv[3], Path.cwd())
    except ValueError as error:
        print(f"release evidence rejected: {error}", file=sys.stderr)
        return 1
    print("Linux Tier-3 and physical-GPU release evidence accepted.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
