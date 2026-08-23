#!/usr/bin/env python3
"""Dependency-free regression tests for the release-evidence gate."""

from __future__ import annotations

import importlib.util
import json
import tempfile
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate-release-evidence.py")
SPEC = importlib.util.spec_from_file_location("release_evidence", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

VERSION = "1.0.7"
COMMIT = "a" * 40
DIGEST = "b" * 64


def manifest() -> dict:
    common = {"result": "PASS", "evidenceUrl": "https://evidence.example/run.tar.gz", "sha256": DIGEST}
    return {
        "schemaVersion": 1,
        "version": VERSION,
        "testedCommit": COMMIT,
        "reviewedBy": "Ray",
        "reviewedAtUtc": "2026-08-23T12:34:56Z",
        "gnomeExtensionUrl": "https://extensions.gnome.org/extension/9999/hyperwhisper-companion/",
        "environments": [
            {"id": "ubuntu-22.04-gnome-wayland", **common},
            {"id": "ubuntu-22.04-gnome-xorg", **common},
            {"id": "debian-12-kde-wayland", **common},
            {
                "id": "physical-gpu",
                **common,
                "architecture": "x86_64",
                "gpuVendor": "AMD",
                "vulkanInference": True,
                "softwareRendererRejected": True,
                "cuda12Inference": None,
                "cuda12NotApplicableReason": "The release host is not NVIDIA hardware.",
            },
        ],
    }


def expect_rejected(value: dict, message: str) -> None:
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "evidence.json"
        path.write_text(json.dumps(value), encoding="utf-8")
        try:
            MODULE.validate(path, VERSION)
        except ValueError:
            return
    raise AssertionError(message)


with tempfile.TemporaryDirectory() as directory:
    path = Path(directory) / "evidence.json"
    path.write_text(json.dumps(manifest()), encoding="utf-8")
    assert MODULE.validate(path, VERSION) == COMMIT

failed_desktop = manifest()
failed_desktop["environments"][0]["result"] = "BLOCKED"
expect_rejected(failed_desktop, "a blocked desktop gate was accepted")

software_gpu = manifest()
software_gpu["environments"][3]["softwareRendererRejected"] = False
expect_rejected(software_gpu, "software Vulkan evidence was accepted")

nvidia_without_cuda = manifest()
nvidia_without_cuda["environments"][3]["gpuVendor"] = "NVIDIA"
expect_rejected(nvidia_without_cuda, "NVIDIA evidence without CUDA 12 was accepted")

invalid_commit = manifest()
invalid_commit["testedCommit"] = "not-a-commit"
expect_rejected(invalid_commit, "an invalid tested commit was accepted")

credential_url = manifest()
credential_url["environments"][0]["evidenceUrl"] = "https://token:secret@evidence.example/run.tar.gz"
expect_rejected(credential_url, "an evidence URL with credentials was accepted")

print("Linux release evidence gate tests passed.")
