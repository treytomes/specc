#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

SPEC="${1:-$REPO_ROOT/examples/FizzBuzz/FizzBuzz.spec}"
ARTIFACTS="${2:-$REPO_ROOT/examples/FizzBuzz/artifacts}"

if [[ ! -f "$SPEC" ]]; then
    echo "Error: spec file not found: $SPEC" >&2
    echo "Usage: $0 [path/to/spec] [path/to/artifacts/]" >&2
    exit 1
fi

dotnet run \
    --project "$REPO_ROOT/IronLlm/IronLlm.csproj" \
    -- "$SPEC" "$ARTIFACTS"
