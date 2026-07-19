# Spec 08 — .env Configuration

**Status:** Ready to implement  
**Scope:** `Program.cs`, new `.env` file in repo root, `Microsoft.Extensions.Configuration` (in-box)

## Problem

Ollama endpoint, model names, and default paths are hardcoded constants in `Program.cs`:

```csharp
const string OllamaBase = "http://localhost:11434";
const string EmbedModel = "mxbai-embed-large:latest";
const string ChatModel  = "ministral-3:3b";
```

Changing any of these requires editing and rebuilding. Users running a remote Ollama instance or a different model can't override without touching source.

## Solution

Load configuration from a `.env` file at the repo root (not the project directory). Fall through to defaults if the file or a key is absent.

## `.env` File

```
IRONLLM_OLLAMA_BASE=http://localhost:11434
IRONLLM_EMBED_MODEL=mxbai-embed-large:latest
IRONLLM_CHAT_MODEL=ministral-3:3b
```

File is in the repo root at `/specc/.env`. Add `.env` to `.gitignore` — it may contain a remote endpoint or API key.

## Implementation

Use `Microsoft.Extensions.Configuration` with a simple hand-rolled `.env` parser (no extra NuGet needed — the framework package is in-box):

```csharp
static Dictionary<string, string> LoadDotEnv(string path)
{
    if (!File.Exists(path)) return [];
    return File.ReadLines(path)
        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
        .Select(l => l.Split('=', 2))
        .Where(p => p.Length == 2)
        .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
}
```

Priority order (highest to lowest):
1. Environment variable (already set in shell)
2. `.env` file value
3. Compiled default

```csharp
static string Cfg(Dictionary<string, string> env, string key, string fallback) =>
    Environment.GetEnvironmentVariable(key)
    ?? env.GetValueOrDefault(key)
    ?? fallback;
```

## `.env.example`

Commit a `.env.example` to the repo root documenting all keys and their defaults. Users `cp .env.example .env` to get started.

## `install.sh` Update

`install.sh` should check for `.env` and offer to create it from `.env.example` if missing:

```bash
if [[ ! -f "$REPO_ROOT/.env" && -f "$REPO_ROOT/.env.example" ]]; then
    echo "No .env found — copying from .env.example"
    cp "$REPO_ROOT/.env.example" "$REPO_ROOT/.env"
fi
```
