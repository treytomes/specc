#!/usr/bin/env bash
# Smoke-tests the pipeline end-to-end against the FizzBuzz example.
# Checks that all six expected artifact files are produced and non-empty.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARTIFACTS="$REPO_ROOT/examples/FizzBuzz/artifacts"
SPEC="$REPO_ROOT/examples/FizzBuzz/FizzBuzz.md"

PASS=0
FAIL=0

check() {
    local desc="$1"
    local result="$2"  # "ok" or a failure message
    if [[ "$result" == "ok" ]]; then
        echo "  PASS  $desc"
        (( PASS++ )) || true
    else
        echo "  FAIL  $desc — $result"
        (( FAIL++ )) || true
    fi
}

# ── Build ────────────────────────────────────────────────────────────────────
echo "Building..."
dotnet build "$REPO_ROOT/Specc/Specc.csproj" --nologo -q
dotnet build "$REPO_ROOT/Specc.Tests/Specc.Tests.csproj" --nologo -q

# ── Unit tests + coverage ────────────────────────────────────────────────────
echo
echo "Running unit tests..."
TEST_RESULTS="$REPO_ROOT/TestResults"
dotnet test "$REPO_ROOT/Specc.Tests/Specc.Tests.csproj" \
  --no-build --nologo \
  --collect:"XPlat Code Coverage" \
  --settings "$REPO_ROOT/Specc.Tests/coverlet.runsettings" \
  --results-directory "$TEST_RESULTS"

COBERTURA=$(find "$TEST_RESULTS" -name 'coverage.cobertura.xml' | sort | tail -1)
coverage=$(python3 -c "
import xml.etree.ElementTree as ET, sys
tree = ET.parse(sys.argv[1])
root = tree.getroot()
rate = float(root.attrib.get('line-rate', 0)) * 100
print(f'{rate:.1f}')
" "$COBERTURA")

check "unit test coverage ≥ 80% (got ${coverage}%)" \
  "$(python3 -c "print('ok' if float('$coverage') >= 80 else 'below threshold')")"

# ── Run pipeline ─────────────────────────────────────────────────────────────
echo "Running pipeline..."
dotnet run \
    --project "$REPO_ROOT/Specc/Specc.csproj" \
    --no-build \
    -- compile --spec "$SPEC" --out "$ARTIFACTS"

echo
echo "Checking artifacts..."

# ── Artifact existence + non-empty ───────────────────────────────────────────
for artifact in \
    "00-extracted.spec" \
    "01-spec.json" \
    "02-semantic-graph.json" \
    "03-embeddings.json" \
    "03b-normalized-graph.json" \
    "04-cfg.json" \
    "05-stackir.json" \
    "06-program.il" \
    "07-program.dll"
do
    path="$ARTIFACTS/$artifact"
    if [[ ! -f "$path" ]]; then
        check "$artifact" "file missing"
    elif [[ ! -s "$path" ]]; then
        check "$artifact" "file is empty"
    else
        check "$artifact" "ok"
    fi
done

# ── Semantic graph sanity ─────────────────────────────────────────────────────
graph="$ARTIFACTS/02-semantic-graph.json"
if [[ -f "$graph" ]]; then
    node_count=$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(len(d['nodes']))" "$graph" 2>/dev/null || echo 0)
    edge_count=$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(len(d['edges']))" "$graph" 2>/dev/null || echo 0)
    check "semantic graph has nodes ($node_count)" "$( [[ $node_count -gt 0 ]] && echo ok || echo "0 nodes" )"
    check "semantic graph has edges ($edge_count)" "$( [[ $edge_count -gt 0 ]] && echo ok || echo "0 edges" )"
fi

# ── Embeddings sanity ────────────────────────────────────────────────────────
embeddings="$ARTIFACTS/03-embeddings.json"
if [[ -f "$embeddings" ]]; then
    embed_count=$(python3 -c "import json,sys; print(len(json.load(open(sys.argv[1]))))" "$embeddings" 2>/dev/null || echo 0)
    check "embeddings non-empty ($embed_count entries)" "$( [[ $embed_count -gt 0 ]] && echo ok || echo "0 entries" )"
fi

# ── CFG sanity ───────────────────────────────────────────────────────────────
cfg="$ARTIFACTS/04-cfg.json"
if [[ -f "$cfg" ]]; then
    block_count=$(python3 -c "import json,sys; print(len(json.load(open(sys.argv[1]))))" "$cfg" 2>/dev/null || echo 0)
    check "CFG has blocks ($block_count)" "$( [[ $block_count -gt 0 ]] && echo ok || echo "0 blocks" )"
fi

# ── MSIL sanity ───────────────────────────────────────────────────────────────
il="$ARTIFACTS/06-program.il"
if [[ -f "$il" ]]; then
    check "MSIL contains .entrypoint" "$( grep -q '.entrypoint' "$il" && echo ok || echo "missing .entrypoint" )"
    check "MSIL contains ret"         "$( grep -q '\bret\b'        "$il" && echo ok || echo "missing ret" )"
fi

# ── Launcher ─────────────────────────────────────────────────────────────────
launcher="$ARTIFACTS/FizzBuzz"
if [[ -f "$launcher" ]]; then
    check "launcher is executable" "$( [[ -x "$launcher" ]] && echo ok || echo "not executable" )"
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo
echo "Results: $PASS passed, $FAIL failed"
[[ $FAIL -eq 0 ]]
