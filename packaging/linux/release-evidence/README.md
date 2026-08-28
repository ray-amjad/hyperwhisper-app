# Linux release evidence manifests

A reviewed manifest named `VERSION.json` in this directory is **optional**. It
is no longer a release gate: a release publishes without one. When a manifest
is present the release workflow still validates it with the same fail-closed
rules below, and attaches it to the GitHub Release. A manifest that does not
pass still fails the run, so a broken attestation is never published.

Releases and retrievable dry-run packages are built only by manually approved
`workflow_dispatch` runs; dry runs never publish or receive Production
signing/deployment secrets.

The manifest binds all evidence to the exact release version and 40-character
`testedCommit`. That commit must be an ancestor of the release commit, and the
workflow rejects every intervening change outside this evidence directory.
This permits adding the reviewed manifest without creating an impossible
self-referential commit hash. It must contain `PASS` evidence for Ubuntu 22.04 GNOME Wayland, Ubuntu
22.04 GNOME Xorg, Debian 12 KDE Wayland, and physical x86_64 Vulkan inference.
Each evidence URL must be HTTPS and accompanied by the SHA-256 digest of its
redacted bundle. An NVIDIA host must also pass CUDA 12 inference; another GPU
vendor records `null` plus a reason. `gnomeExtensionUrl` must be the published
extensions.gnome.org listing, not the bundled fallback.

Run the same fail-closed validation used by the release workflow:

```bash
packaging/linux/scripts/validate-release-evidence.py \
  packaging/linux/release-evidence/1.0.0.json \
  1.0.0 \
  "$(git rev-parse HEAD)"
```

Evidence files must not contain credentials, API tokens, dictated personal
content, or unrelated raw keystrokes. A manifest is an attestation index, not a
replacement for the complete checklist in `packaging/linux/VERIFICATION.md`.
