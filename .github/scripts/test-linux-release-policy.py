#!/usr/bin/env python3
"""Regression checks for fail-closed Linux release workflow wiring."""

from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]
CI = (ROOT / ".github/workflows/linux-ci.yml").read_text(encoding="utf-8")
RELEASE = (ROOT / ".github/workflows/linux-release.yml").read_text(encoding="utf-8")
CI_DOCUMENT = yaml.safe_load(CI)
RELEASE_DOCUMENT = yaml.safe_load(RELEASE)


def require(document: str, fragment: str, explanation: str) -> None:
    if fragment not in document:
        raise SystemExit(f"FAIL: {explanation}: missing {fragment!r}")


def main() -> None:
    # PyYAML implements YAML 1.1 and parses the plain key `on` as True.
    ci_triggers = CI_DOCUMENT.get("on", CI_DOCUMENT.get(True, {}))
    if "workflow_call" not in ci_triggers:
        raise SystemExit("FAIL: Linux CI must expose workflow_call")
    ci_job = CI_DOCUMENT["jobs"]["build-and-test"]
    if ci_job.get("outputs", {}).get("verified_sha") != "${{ steps.verified.outputs.sha }}":
        raise SystemExit("FAIL: Linux CI must export the exact verified step SHA")

    release_jobs = RELEASE_DOCUMENT["jobs"]
    release_triggers = RELEASE_DOCUMENT.get("on", RELEASE_DOCUMENT.get(True, {}))
    if set(release_triggers) != {"workflow_dispatch"}:
        raise SystemExit("FAIL: Linux releases must be manual-only")
    if release_jobs.get("quality-gates", {}).get("uses") != "./.github/workflows/linux-ci.yml":
        raise SystemExit("FAIL: release quality-gates must call Linux CI directly")
    release_job = release_jobs["build-package-release"]
    needs = release_job.get("needs", [])
    if isinstance(needs, str):
        needs = [needs]
    if "quality-gates" not in needs:
        raise SystemExit("FAIL: release build must require quality-gates")

    require(CI, "verified_sha:", "Linux CI must report its tested commit")
    require(CI, "cargo test --locked", "shared Rust tests are mandatory")
    require(CI, "cargo clippy --locked --all-targets", "Rust lint is mandatory")
    require(CI, "find app/shared-dotnet", "all portable .NET tests are discovered")
    require(CI, "for project in \"${test_projects[@]}\"", "all discovered test harnesses execute")
    require(CI, "run-virtual-mic-e2e.sh", "live virtual microphone transcription is mandatory")
    require(CI, "run-ui-smoke.sh", "Xvfb UI smoke is mandatory")
    require(CI, "run-ui-smoke-xwayland.sh", "Weston/XWayland UI smoke is mandatory")
    require(CI, "test-package.sh", "Debian package verification is mandatory")

    require(
        RELEASE,
        "uses: ./.github/workflows/linux-ci.yml",
        "release must invoke the reusable Linux CI gate",
    )
    require(RELEASE, "needs: quality-gates", "release build must depend on quality gates")
    require(
        RELEASE,
        'test "$VERIFIED_SHA" = "$GITHUB_SHA"',
        "release must read back the exact tested SHA",
    )
    require(
        RELEASE,
        "validate-release-evidence.py",
        "publishing must require reviewed physical evidence",
    )
    require(
        RELEASE,
        'git fetch -q --no-tags origin "$GITHUB_SHA"',
        "release checkout must retain evidence ancestry",
    )
    if "--depth=1" in RELEASE:
        raise SystemExit("FAIL: release checkout must not be shallow")
    require(
        RELEASE,
        "'Linux Release Dry Run'",
        "dry runs must not enter the Production environment",
    )
    require(
        RELEASE,
        "actions/upload-artifact@v4",
        "dry-run packages must be retrievable for physical testing",
    )
    require(RELEASE, "retention-days: 14", "dry-run artifacts need bounded retention")
    require(
        RELEASE,
        'if [[ "$publish" == "true" && "$require_apt_signature" != "true" ]]',
        "publication must require signed APT metadata",
    )
    require(RELEASE, 'GITHUB_REF" != "refs/heads/main', "publishing must run from main")
    require(RELEASE, "Remote tag already exists", "publishing must reject tag collisions")
    require(RELEASE, "Non-empty release notes", "published releases require notes")
    require(
        RELEASE,
        "working-directory: shared-core-rs",
        "release Rust builds must honor the pinned toolchain",
    )
    require(RELEASE, "GitHub Release assets and exact tag target verified", "publication needs readback")
    require(RELEASE, "if: always()", "release runs must always emit a summary")
    require(RELEASE, "if: steps.release.outputs.publish == 'true'", "publication is explicit")
    require(RELEASE, "if: steps.release.outputs.deploy_apt == 'true'", "APT deployment is explicit")
    require(RELEASE, "https://", "APT deployment requires an HTTPS public origin")
    require(RELEASE, "LINUX_APT_SSH_KNOWN_HOSTS", "SSH host identity is pinned")
    require(RELEASE, "mv -Tf", "APT current symlink is switched atomically")

    print("PASS Linux release cannot bypass required CI or publication policy")


if __name__ == "__main__":
    main()
