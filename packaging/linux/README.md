# Linux Debian packaging

Build the amd64 `1.0.0` package from a self-contained `linux-x64` publish:

```bash
packaging/linux/scripts/build-deb.sh \
  --publish-dir /tmp/hyperwhisper-linux-publish
```

The package installs the application under `/usr/lib/hyperwhisper`, launchers at
`/usr/bin/hyperwhisper` and `/usr/bin/hyperwhisper-companionctl`, its desktop
entry and icon, optional GNOME/KDE companion assets, and a udev-rule template.
`postinst` creates the system group `hyperwhisper-input` and installs the rule.
It does **not** add users automatically: an administrator must explicitly run
`sudo usermod -aG hyperwhisper-input <user>` and the user must re-login.

The rule grants the group read-only access to keyboard event devices and
read/write access to `/dev/uinput`. It grants no world access and deliberately
does not use `TAG+="uaccess"`. Maintainer scripts never inspect or write user
home directories.

Desktop companions are never enabled by package maintainer scripts. The signed-in
user may explicitly run `hyperwhisper-companionctl install-gnome` on GNOME 42+
or `hyperwhisper-companionctl install-kde` on Plasma 5 or 6. Matching `remove-*`
commands undo only that user's companion files.

Run the isolated package tests with:

```bash
packaging/linux/scripts/test-package.sh
```

The tests extract the package into a temporary root and run `postinst`/`postrm`
there, leaving the host's groups, udev rules, and devices untouched.

## Static APT repository

Generate deterministic unsigned repository metadata from a built package:

```bash
SOURCE_DATE_EPOCH=0 packaging/linux/scripts/generate-apt-repository.sh \
  --deb artifacts/linux/hyperwhisper_1.0.0_amd64.deb \
  --output-dir artifacts/apt-repository
```

The output contains `pool/`, `Packages`, `Packages.gz`, and a checksum-complete
`Release` file suitable for static hosting. The output directory must be new or
empty; the generator refuses broad or symlink destinations and never deletes an
existing repository.

Unsigned metadata is the default. Signing occurs only when an operator supplies
an existing private key file with `--signing-key-file`; the key is imported into
a temporary `GNUPGHOME`, is never copied into repository output, and is deleted
on exit. Signed output includes `InRelease`, `Release.gpg`, and the exported
public `hyperwhisper-archive-keyring.asc` needed by repository consumers. CI
obtains the private key from the optional `LINUX_APT_SIGNING_KEY` Production
environment secret. No private key or passphrase belongs in this repository.

Run the deterministic unsigned generator tests with:

```bash
packaging/linux/scripts/test-apt-repository.sh
```
