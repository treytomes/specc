#!/usr/bin/env bash
# Verifies and installs dependencies required to build and run IronLlm.
# Safe to run multiple times — skips anything already present.
set -euo pipefail

PASS=0
FAIL=0
INSTALLED=0

ok()       { echo "  OK        $1"; (( PASS++      )) || true; }
missing()  { echo "  MISSING   $1 — $2"; (( FAIL++  )) || true; }
installed(){ echo "  INSTALLED $1"; (( INSTALLED++ )) || true; }

# ── .NET SDK ─────────────────────────────────────────────────────────────────
echo "Checking .NET SDK..."
if command -v dotnet &>/dev/null; then
    SDK_VER=$(dotnet --version 2>/dev/null || echo "unknown")
    ok ".NET SDK ($SDK_VER)"
else
    missing ".NET SDK" "install from https://dotnet.microsoft.com/download"
fi

# ── Ollama ────────────────────────────────────────────────────────────────────
echo "Checking Ollama..."
if command -v ollama &>/dev/null; then
    OLLAMA_VER=$(ollama --version 2>/dev/null | head -1 || echo "unknown")
    ok "Ollama ($OLLAMA_VER)"
else
    echo "  Ollama not found — installing..."
    if command -v curl &>/dev/null; then
        curl -fsSL https://ollama.com/install.sh | sh
        installed "Ollama"
    else
        missing "Ollama" "install curl first, then re-run this script"
    fi
fi

# ── Ollama running? ───────────────────────────────────────────────────────────
echo "Checking Ollama server..."
if curl -sf http://localhost:11434/api/tags &>/dev/null; then
    ok "Ollama server (listening on :11434)"
else
    echo "  Ollama server not running — starting in background..."
    ollama serve &>/dev/null &
    disown
    sleep 2
    if curl -sf http://localhost:11434/api/tags &>/dev/null; then
        installed "Ollama server"
    else
        missing "Ollama server" "failed to start — run 'ollama serve' manually"
    fi
fi

# ── Required Ollama models ────────────────────────────────────────────────────
echo "Checking Ollama models..."

check_or_pull_model() {
    local model="$1"
    if curl -sf http://localhost:11434/api/tags 2>/dev/null | grep -q "\"$model\""; then
        ok "model $model"
    else
        echo "  Pulling $model (this may take a while)..."
        ollama pull "$model"
        installed "model $model"
    fi
}

check_or_pull_model "mxbai-embed-large:latest"
check_or_pull_model "ministral-3:3b"

# ── ilasm (optional — needed only for assemble.sh) ────────────────────────────
echo "Checking ilasm (optional)..."
if command -v ilasm &>/dev/null; then
    ok "ilasm ($(ilasm --version 2>/dev/null | head -1 || echo 'found'))"
else
    echo "  ilasm not found — attempting install via mono-devel..."
    if command -v apt-get &>/dev/null; then
        if sudo apt-get install -y mono-devel &>/dev/null; then
            installed "ilasm (mono-devel)"
        else
            echo "  OPTIONAL  ilasm — install manually with: sudo apt install mono-devel"
            echo "            (required only to run ./scripts/assemble.sh)"
        fi
    else
        echo "  OPTIONAL  ilasm — install mono-devel for your distro"
        echo "            (required only to run ./scripts/assemble.sh)"
    fi
fi

# ── python3 (used by test.sh for JSON inspection) ────────────────────────────
echo "Checking python3..."
if command -v python3 &>/dev/null; then
    PY_VER=$(python3 --version 2>/dev/null || echo "unknown")
    ok "python3 ($PY_VER)"
else
    missing "python3" "used by test.sh for artifact inspection — install python3"
fi

# ── NuGet restore ─────────────────────────────────────────────────────────────
echo "Restoring NuGet packages..."
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if dotnet restore "$REPO_ROOT/IronLlm/IronLlm.csproj" -q; then
    ok "NuGet packages restored"
else
    missing "NuGet restore" "check network connectivity or NuGet feed"
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo
echo "Results: $PASS ok, $INSTALLED installed, $FAIL missing"
if [[ $FAIL -gt 0 ]]; then
    echo "Fix the items marked MISSING above, then re-run this script."
    exit 1
fi
echo "All dependencies satisfied. Run ./scripts/run.sh to compile FizzBuzz."
