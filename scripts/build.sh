#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

echo "Building Specc..."
dotnet build "$REPO_ROOT/Specc/Specc.csproj" "$@"
