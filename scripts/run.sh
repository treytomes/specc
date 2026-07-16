#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Accept --spec / --out passthrough, or positional legacy form
if [[ "${1:-}" == --* ]]; then
    dotnet run \
        --project "$REPO_ROOT/IronLlm/IronLlm.csproj" \
        -- compile "$@"
else
    SPEC="${1:-$REPO_ROOT/examples/FizzBuzz/FizzBuzz.spec}"
    ARTIFACTS="${2:-$(dirname "$SPEC")/artifacts}"

    if [[ ! -f "$SPEC" ]]; then
        echo "Error: spec file not found: $SPEC" >&2
        echo "Usage: $0 [--spec <path>] [--out <dir>] [--force <pass>] [--verbose]" >&2
        echo "       $0 [path/to/spec] [path/to/artifacts/]" >&2
        exit 1
    fi

    dotnet run \
        --project "$REPO_ROOT/IronLlm/IronLlm.csproj" \
        -- compile --spec "$SPEC" --out "$ARTIFACTS"
fi
