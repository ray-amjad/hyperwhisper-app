#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)

dbus-run-session -- xvfb-run -a -s "-screen 0 1280x800x24" \
  dotnet run \
    --project "$repo_root/app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj" \
    --configuration Release \
    --no-build \
    -- \
    --smoke-test
