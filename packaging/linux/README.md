# Linux Debian packaging

Build the amd64 `1.0.0` package from a self-contained `linux-x64` publish:

```bash
packaging/linux/scripts/build-deb.sh \
  --publish-dir /tmp/hyperwhisper-linux-publish
```

The package installs the application under `/usr/lib/hyperwhisper`, a launcher at
`/usr/bin/hyperwhisper`, its desktop entry and icon, and a udev-rule template.
`postinst` creates the system group `hyperwhisper-input` and installs the rule.
It does **not** add users automatically: an administrator must explicitly run
`sudo usermod -aG hyperwhisper-input <user>` and the user must re-login.

The rule grants the group read-only access to keyboard event devices and
read/write access to `/dev/uinput`. It grants no world access and deliberately
does not use `TAG+="uaccess"`. Maintainer scripts never inspect or write user
home directories.

Run the isolated package tests with:

```bash
packaging/linux/scripts/test-package.sh
```

The tests extract the package into a temporary root and run `postinst`/`postrm`
there, leaving the host's groups, udev rules, and devices untouched.
