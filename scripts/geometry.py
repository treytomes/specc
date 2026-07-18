#!/usr/bin/env python3
"""
Spec 35 — Embedding Geometry Validation

For each program in the repository, computes the mean embedding across all
non-assertion nodes and reports pairwise cosine similarity.

Usage:
    python3 scripts/geometry.py [repository_path] [--json]

Flags:
    --json   Write geometry.json to the repository root instead of printing.
"""

import json
import math
import os
import sys
from collections import defaultdict


def cosine(a, b):
    dot   = sum(x * y for x, y in zip(a, b))
    na    = math.sqrt(sum(x * x for x in a))
    nb    = math.sqrt(sum(y * y for y in b))
    if na == 0 or nb == 0:
        return 0.0
    return dot / (na * nb)


def mean_vector(vectors):
    if not vectors:
        return []
    dim = len(vectors[0])
    result = [0.0] * dim
    for v in vectors:
        for i, x in enumerate(v):
            result[i] += x
    n = len(vectors)
    return [x / n for x in result]


def load_program_embeddings(repo_path):
    index_path = os.path.join(repo_path, "index.json")
    if not os.path.exists(index_path):
        print(f"No index.json found at {repo_path}", file=sys.stderr)
        sys.exit(1)

    with open(index_path) as f:
        index = json.load(f)

    # Keep only the most-recent compilation per program name.
    latest = {}
    for unit in index.get("Units", []):
        name = unit.get("ProgramName", "unknown")
        compiled_at = unit.get("CompiledAt", "")
        if name not in latest or compiled_at > latest[name]["CompiledAt"]:
            latest[name] = unit

    programs = {}
    for name, unit in sorted(latest.items()):
        emb_path = unit.get("EmbeddingsPath", "")
        if not emb_path or not os.path.exists(emb_path):
            print(f"  [skip] {name}: embeddings not found at {emb_path}", file=sys.stderr)
            continue

        with open(emb_path) as f:
            nodes = json.load(f)

        # Exclude AssertionNodes — they're metadata, not semantic program structure.
        vectors = [
            n["vector"]
            for n in nodes
            if "Assert:" not in n.get("label", "") and n.get("vector")
        ]

        if not vectors:
            print(f"  [skip] {name}: no usable embeddings", file=sys.stderr)
            continue

        programs[name] = mean_vector(vectors)

    return programs


def compute_pairs(programs):
    names = sorted(programs.keys())
    pairs = []
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            a, b = names[i], names[j]
            sim = cosine(programs[a], programs[b])
            pairs.append((sim, a, b))
    pairs.sort(reverse=True)
    return pairs


def main():
    args = sys.argv[1:]
    emit_json = "--json" in args
    args = [a for a in args if a != "--json"]

    repo_path = args[0] if args else os.path.join(os.path.dirname(__file__), "..", "repository")
    repo_path = os.path.abspath(repo_path)

    print(f"Repository: {repo_path}")
    programs = load_program_embeddings(repo_path)

    if len(programs) < 2:
        print("Need at least 2 programs to compute similarity. Run the pipeline on more examples first.")
        sys.exit(1)

    print(f"\nLoaded {len(programs)} programs: {', '.join(sorted(programs))}\n")

    pairs = compute_pairs(programs)

    col_w = max(len(a) + len(b) + 4 for _, a, b in pairs)
    print(f"{'Pair':<{col_w}}  Similarity")
    print("-" * (col_w + 12))
    for sim, a, b in pairs:
        bar = "█" * int(sim * 20)
        print(f"{a} ↔ {b:<{col_w - len(a) - 3}}  {sim:.4f}  {bar}")

    # Report on expected clusters.
    print("\n── Expected cluster checks ──")
    name_set = set(programs)

    def check(label, a, b, baseline_a, baseline_b):
        if a not in name_set or b not in name_set:
            print(f"  [skip] {label}: one or both programs not in repository")
            return
        sim_ab    = cosine(programs[a], programs[b])
        baselines = []
        for ba, bb in [(baseline_a, baseline_b)]:
            if ba in name_set and bb in name_set:
                baselines.append(cosine(programs[ba], programs[bb]))
        if not baselines:
            print(f"  [skip] {label}: baseline programs not in repository")
            return
        baseline = min(baselines)
        result = "PASS ✓" if sim_ab > baseline else "FAIL ✗"
        print(f"  {result}  {a} ↔ {b} ({sim_ab:.4f}) > {baseline_a} ↔ {baseline_b} ({baseline:.4f})")

    check("FizzBuzz family closer than FizzBuzz↔BubbleSort",
          "FizzBuzz", "fizzbuzzhundred",
          "FizzBuzz", "BubbleSort")
    check("Sorting programs closer than BubbleSort↔FizzBuzz",
          "BubbleSort", "SelectionSort",
          "BubbleSort", "FizzBuzz")
    check("Number sequences closer than Fibonacci↔BubbleSort",
          "Fibonacci", "Collatz",
          "Fibonacci", "BubbleSort")

    if emit_json:
        out = {
            "programs": sorted(programs.keys()),
            "pairs": [{"a": a, "b": b, "similarity": round(sim, 6)} for sim, a, b in pairs],
        }
        out_path = os.path.join(repo_path, "geometry.json")
        with open(out_path, "w") as f:
            json.dump(out, f, indent=2)
        print(f"\nWrote {out_path}")


if __name__ == "__main__":
    main()
