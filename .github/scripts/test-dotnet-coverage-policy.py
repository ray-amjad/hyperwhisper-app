#!/usr/bin/env python3
"""Regression checks for the fail-closed production coverage parser."""

from __future__ import annotations

import subprocess
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CHECKER = ROOT / ".github/scripts/check-dotnet-coverage.py"


def run(xml: str, minimum: int = 60) -> subprocess.CompletedProcess[str]:
    with tempfile.TemporaryDirectory(prefix="hyperwhisper-coverage-policy-") as directory:
        report = Path(directory) / "coverage.xml"
        report.write_text(xml, encoding="utf-8")
        return subprocess.run(
            ["python3", str(CHECKER), str(report), "--minimum", str(minimum)],
            check=False,
            text=True,
            capture_output=True,
        )


def report(product_hits: list[int], test_hits: list[int] | None = None) -> str:
    def package(name: str, hits: list[int]) -> str:
        lines = "".join(
            f'<line number="{index + 1}" hits="{hit}" />'
            for index, hit in enumerate(hits)
        )
        return (
            f'<package name="{name}"><classes><class filename="source.cs">'
            f"<lines>{lines}</lines></class></classes></package>"
        )

    tests = package("HyperWhisper.Example.Tests", test_hits or [])
    return f'<coverage><packages>{package("HyperWhisper.Example", product_hits)}{tests}</packages></coverage>'


assert run(report([1, 1, 1, 0, 0])).returncode == 0, "60% boundary did not pass"
assert run(report([1, 1, 0, 0, 0])).returncode == 1, "below-floor report did not fail"
assert run(report([0, 0], [1] * 100)).returncode == 1, "test assembly inflated coverage"
assert run("<coverage><packages /></coverage>").returncode == 2, "empty report did not fail closed"
assert run("not xml").returncode == 2, "malformed report did not fail closed"
print("PASS production .NET coverage policy is fail-closed at 60%")
