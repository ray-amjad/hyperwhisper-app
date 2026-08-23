#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/../../.." && pwd)"
MINIMUM="${HW_DOTNET_COVERAGE_MINIMUM:-60}"
REPORT="${HW_DOTNET_COVERAGE_REPORT:-$REPO_ROOT/artifacts/coverage/linux.cobertura.xml}"

mapfile -t TEST_PROJECTS < <(
    find "$REPO_ROOT/app/shared-dotnet" "$REPO_ROOT/app/linux" \
        -mindepth 2 -type f -path '*Tests/*.csproj' -print | sort
)
if (( ${#TEST_PROJECTS[@]} == 0 )); then
    echo "No portable or Linux test harnesses were found." >&2
    exit 2
fi

if [[ "${1:-}" == "--execute-harnesses" ]]; then
    for project in "${TEST_PROJECTS[@]}"; do
        project_dir="$(dirname -- "$project")"
        assembly="$(basename -- "$project" .csproj).dll"
        dll="$(find "$project_dir/bin/Release" -mindepth 2 -maxdepth 2 -type f -name "$assembly" -print -quit)"
        if [[ -z "$dll" ]]; then
            echo "Built harness was not found for $project" >&2
            exit 2
        fi
        echo "COVERAGE $project"
        dotnet "$dll"
    done
    exit 0
fi

cd -- "$REPO_ROOT"
dotnet tool restore
for project in "${TEST_PROJECTS[@]}"; do
    dotnet build "$project" --configuration Release -p:TreatWarningsAsErrors=true -v:q
done

mkdir -p -- "$(dirname -- "$REPORT")"
native_core="$REPO_ROOT/shared-core-rs/target/x86_64-unknown-linux-gnu/release"
export LD_LIBRARY_PATH="$native_core${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export DOTNET_COVERAGE_TELEMETRY_OPTOUT=1
export DOTNET_COVERAGE_NOLOGO=1

dotnet tool run dotnet-coverage collect \
    "bash $SCRIPT_DIR/run-dotnet-coverage.sh --execute-harnesses" \
    --output "$REPORT" \
    --output-format cobertura
python3 "$REPO_ROOT/.github/scripts/check-dotnet-coverage.py" \
    "$REPORT" --minimum "$MINIMUM"
