#!/usr/bin/env bash
# Spec 35 — Embedding Geometry Validation
# Loads program-level embeddings from the repository and prints a pairwise
# cosine similarity matrix, ranked by most-similar pair.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)/repository"
python3 "$(dirname "$0")/geometry.py" "$REPO" "$@"
