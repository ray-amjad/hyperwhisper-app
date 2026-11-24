#!/usr/bin/env python3
"""Regression checks for fail-closed Linux release workflow wiring."""

import os
import shutil
import subprocess
import tempfile
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
        "a checked-in evidence manifest must still be validated",
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
        # Any pin: workflows pin actions by commit SHA (#2006).
        "uses: actions/upload-artifact@",
        "dry-run packages must be retrievable for physical testing",
    )
    require(RELEASE, "retention-days: 14", "dry-run artifacts need bounded retention")
    # Publishing no longer requires a signature, but an unsigned run must not
    # hand anyone an unsigned APT repository. Prove it is withheld everywhere.
    require(
        RELEASE,
        'if [[ "$REQUIRE_APT_SIGNATURE" == "true" ]]; then\n'
        '            assets+=("hyperwhisper-apt-${VERSION}.tar.gz")',
        "an unsigned run must withhold the APT archive from the GitHub Release",
    )
    require(
        RELEASE,
        'if [[ "$REQUIRE_APT_SIGNATURE" == "true" ]]; then\n'
        '            mirrored+=("hyperwhisper-apt-${VERSION}.tar.gz")',
        "an unsigned run must withhold the APT archive from the R2 mirror",
    )
    require(
        RELEASE,
        '"$publish" != "true" || "$require_apt_signature" != "true"',
        "APT deployment must require both publication and a signature",
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

    check_preflight_keeps_signing_key_out_of_default_keyring()

    print("PASS Linux release cannot bypass required CI or publication policy")


def release_step_script(name: str) -> str:
    for job in RELEASE_DOCUMENT["jobs"].values():
        for step in job.get("steps", []):
            if step.get("name") == name:
                return step["run"]
    raise SystemExit(f"FAIL: release workflow has no step named {name!r}")


def check_preflight_keeps_signing_key_out_of_default_keyring() -> None:
    """Run the APT credential preflight for real with a throwaway key.

    The step imports LINUX_APT_SIGNING_KEY to prove it is a usable GPG key. It
    must do so in a private GNUPGHOME that is gone when the step ends, so the
    private key is never left in the runner's default ~/.gnupg keyring.
    """
    if shutil.which("gpg") is None or shutil.which("gpgconf") is None:
        raise SystemExit("FAIL: gpg and gpgconf are required to check the APT key preflight")

    script = release_step_script("Verify APT deployment credentials")
    with tempfile.TemporaryDirectory(prefix="hyperwhisper-apt-preflight-") as scratch:
        root = Path(scratch)
        home = root / "home"
        runner_temp = root / "runner"
        stub_bin = root / "bin"
        key_home = root / "keygen"
        for directory in (home, runner_temp, stub_bin, key_home):
            directory.mkdir(mode=0o700)
        # The SSH reachability probe is out of scope here; let it succeed.
        ssh_stub = stub_bin / "ssh"
        ssh_stub.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        ssh_stub.chmod(0o755)

        # A throwaway key made at run time; nothing secret is checked in.
        subprocess.run(
            ["gpg", "--batch", "--quiet", "--homedir", str(key_home), "--passphrase", "",
             "--quick-gen-key", "APT preflight test <apt-preflight@example.invalid>",
             "ed25519", "sign", "never"],
            check=True,
        )
        armored = subprocess.run(
            ["gpg", "--batch", "--quiet", "--homedir", str(key_home), "--armor",
             "--export-secret-keys"],
            check=True, capture_output=True, text=True,
        ).stdout
        subprocess.run(["gpgconf", "--homedir", str(key_home), "--kill", "all"], check=False)
        if "PRIVATE KEY BLOCK" not in armored:
            raise SystemExit("FAIL: could not create a throwaway APT signing key")

        env = {
            "PATH": f"{stub_bin}{os.pathsep}{os.environ.get('PATH', '/usr/bin:/bin')}",
            "HOME": str(home),
            "RUNNER_TEMP": str(runner_temp),
            "APT_DEPLOY_HOST": "apt.example.invalid",
            "APT_DEPLOY_USER": "deploy",
            "APT_DEPLOY_PATH": "/srv/apt",
            "APT_SSH_PRIVATE_KEY": "unused",
            "APT_SSH_KNOWN_HOSTS": "unused",
            "APT_SIGNING_KEY": armored,
        }
        result = subprocess.run(
            ["bash", "-e", "-c", script], env=env, capture_output=True, text=True,
        )
        if result.returncode != 0:
            raise SystemExit(
                "FAIL: APT credential preflight rejected a valid signing key:\n"
                + result.stdout + result.stderr
            )

        default_secret_keys = subprocess.run(
            ["gpg", "--batch", "--with-colons", "--list-secret-keys"],
            env={**env, "PATH": os.environ.get("PATH", "/usr/bin:/bin")},
            capture_output=True, text=True,
        ).stdout
        # Stop any agent the check itself (or a regression) started on HOME.
        subprocess.run(
            ["gpgconf", "--homedir", str(home / ".gnupg"), "--kill", "all"],
            check=False, capture_output=True,
        )
        if any(line.startswith("sec:") for line in default_secret_keys.splitlines()):
            raise SystemExit(
                "FAIL: APT credential preflight left the signing key in the default GPG keyring"
            )
        leftovers = sorted(p.name for p in runner_temp.iterdir() if "gnupg" in p.name)
        if leftovers:
            raise SystemExit(
                f"FAIL: APT credential preflight left its private GNUPGHOME behind: {leftovers}"
            )
        if any(runner_temp.glob("*.asc")):
            raise SystemExit("FAIL: APT credential preflight left the signing key file behind")


if __name__ == "__main__":
    main()
