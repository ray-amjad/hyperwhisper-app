#!/usr/bin/env python3
"""Dependency-free regression tests for the release-evidence gate."""

from __future__ import annotations

import importlib.util
import json
import subprocess
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


def git(repository: Path, *arguments: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repository), *arguments],
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


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

with tempfile.TemporaryDirectory() as directory:
    repository = Path(directory)
    git(repository, "init", "--quiet")
    git(repository, "config", "user.name", "Release Evidence Test")
    git(repository, "config", "user.email", "release-evidence@example.test")
    (repository / "code.txt").write_text("tested\n", encoding="utf-8")
    git(repository, "add", "code.txt")
    git(repository, "commit", "--quiet", "-m", "tested code")
    tested_commit = git(repository, "rev-parse", "HEAD")

    evidence_directory = repository / "packaging" / "linux" / "release-evidence"
    evidence_directory.mkdir(parents=True)
    (evidence_directory / f"{VERSION}.json").write_text(
        json.dumps(manifest()), encoding="utf-8"
    )
    git(repository, "add", "packaging/linux/release-evidence")
    git(repository, "commit", "--quiet", "-m", "add reviewed evidence")
    evidence_only_commit = git(repository, "rev-parse", "HEAD")
    MODULE.verify_release_revision(tested_commit, evidence_only_commit, repository)

    (repository / "code.txt").write_text("changed after testing\n", encoding="utf-8")
    git(repository, "add", "code.txt")
    git(repository, "commit", "--quiet", "-m", "change release code")
    changed_code_commit = git(repository, "rev-parse", "HEAD")
    try:
        MODULE.verify_release_revision(tested_commit, changed_code_commit, repository)
    except ValueError:
        pass
    else:
        raise AssertionError("a release code change after physical testing was accepted")

print("Linux release evidence gate tests passed.")
