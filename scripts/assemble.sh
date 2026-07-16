#!/usr/bin/env bash
# Assembles a .il file produced by the MSIL generation pass using ilasm.
# ilasm ships with the Mono toolchain: sudo apt install mono-devel
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

IL_FILE="${1:-$REPO_ROOT/examples/FizzBuzz/artifacts/06-program.il}"

if [[ ! -f "$IL_FILE" ]]; then
    echo "Error: .il file not found: $IL_FILE" >&2
    echo "Run ./scripts/run.sh first to generate it." >&2
    exit 1
fi

if ! command -v ilasm &>/dev/null; then
    echo "ilasm not found. Install with:" >&2
    echo "  sudo apt install mono-devel" >&2
    exit 1
fi

OUT_DIR="$(dirname "$IL_FILE")"
OUT_EXE="$OUT_DIR/program.exe"

echo "Assembling $IL_FILE..."
ilasm "$IL_FILE" /output:"$OUT_EXE"
echo "Output: $OUT_EXE"
echo
echo "Run with: mono $OUT_EXE"
