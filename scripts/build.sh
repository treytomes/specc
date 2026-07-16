#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

echo "Building IronLlm..."
dotnet build "$REPO_ROOT/IronLlm/IronLlm.csproj" "$@"
