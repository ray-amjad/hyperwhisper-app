#!/usr/bin/env python3
"""Fail closed when production HyperWhisper line coverage is below a floor."""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path


def is_production_package(name: str) -> bool:
    return (
        (name == "HyperWhisper" or name.startswith("HyperWhisper."))
        and not name.endswith(".Tests")
        and ".Tests." not in name
        and not name.endswith(".SmokeTests")
    )


def covered_lines(report: Path) -> tuple[int, int, dict[str, tuple[int, int]]]:
    try:
        root = ET.parse(report).getroot()
    except (OSError, ET.ParseError) as error:
        raise ValueError(f"coverage report is unreadable: {error}") from error

    packages: dict[str, tuple[int, int]] = {}
    total_covered = 0
    total_valid = 0
    for package in root.findall("./packages/package"):
        name = package.get("name", "")
        if not is_production_package(name):
            continue
        # A source line can appear in more than one method. Count each physical
        # package/file/line once and consider it covered if any record has hits.
        lines: dict[tuple[str, int], bool] = defaultdict(bool)
        for class_node in package.findall("./classes/class"):
            filename = class_node.get("filename", "")
            for line in class_node.findall("./lines/line"):
                try:
                    number = int(line.get("number", ""))
                    hits = int(line.get("hits", "0"))
                except ValueError as error:
                    raise ValueError(f"invalid line record in package {name}") from error
                lines[(filename, number)] |= hits > 0
        valid = len(lines)
        covered = sum(lines.values())
        if valid:
            packages[name] = (covered, valid)
            total_covered += covered
            total_valid += valid

    if total_valid == 0:
        raise ValueError("coverage report contains no production HyperWhisper lines")
    return total_covered, total_valid, packages


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    parser.add_argument("--minimum", type=float, default=60.0)
    args = parser.parse_args()
    if not 0.0 <= args.minimum <= 100.0:
        parser.error("--minimum must be between 0 and 100")

    try:
        covered, valid, packages = covered_lines(args.report)
    except ValueError as error:
        print(f"COVERAGE ERROR: {error}", file=sys.stderr)
        return 2

    for name, (package_covered, package_valid) in sorted(packages.items()):
        rate = package_covered * 100.0 / package_valid
        print(f"{name}: {rate:.2f}% ({package_covered}/{package_valid} lines)")
    rate = covered * 100.0 / valid
    print(f"Production .NET line coverage: {rate:.2f}% ({covered}/{valid} lines)")
    if rate + 1e-9 < args.minimum:
        print(f"COVERAGE FAIL: {rate:.2f}% is below {args.minimum:.2f}%", file=sys.stderr)
        return 1
    print(f"COVERAGE PASS: minimum {args.minimum:.2f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
