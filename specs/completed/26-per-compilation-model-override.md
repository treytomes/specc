# Spec 26 — Per-Compilation Chat Model Override

**Status:** Incomplete  
**Scope:** `Program.cs`, `scripts/run.sh`; no pass changes

## Motivation

The chat model is currently fixed at compile time via `IRONLLM_CHAT_MODEL` (env var / `.env`). Switching models to test a different extraction quality — for example, comparing `ministral-3:3b` vs `gemma4:e2b` on the same `.md` file — requires editing `.env`, re-running, restoring `.env`. That workflow is awkward and can't be done from a single shell command.

A `--chat-model` CLI flag lets the caller override the chat model for one compilation without touching `.env`. This is the right granularity: per-compilation, not per-pass (all LLM calls within one compilation use the same model).

## What Changes

### CLI option

Add a new option to the `compile` command:

```csharp
var chatModelOpt = new Option<string?>("--chat-model")
{
    Description = "Override the chat model for this compilation (default: IRONLLM_CHAT_MODEL env / ministral-3:3b)",
};
```

Add it to `compileCommand` alongside the existing options.

### Resolution order

Inside `compileCommand.SetAction`:

```csharp
var chatModelOverride = result.GetValue(chatModelOpt);
var effectiveChatModel = chatModelOverride ?? chatModel;  // chatModel = env/dotenv/default
```

Pass `effectiveChatModel` to `OllamaChatClient` instead of `chatModel`:

```csharp
.AddSingleton<IChatClient>(_ =>
    new OllamaChatClient(new Uri(ollamaBase), effectiveChatModel))
```

The override applies only within the current process. `.env` and `IRONLLM_CHAT_MODEL` are unaffected.

### scripts/run.sh

The script passes `"$@"` through to the binary, so `--chat-model` works automatically:

```bash
scripts/run.sh examples/FizzBuzz --chat-model gemma4:e2b
```

No changes to the script are needed.

### Incremental behavior

Model selection is not part of the artifact key. If `00-extracted.spec` already exists, `MarkdownSpecPass` is skipped regardless of `--chat-model`. This is correct: the artifact represents the output, not how it was produced. To force a re-extraction with a different model, combine with `--force 00`:

```bash
scripts/run.sh examples/FizzBuzz --chat-model gemma4:e2b --force 00
```

This is the intended workflow for model comparison: force the LLM-dependent passes, override the model, observe the difference in artifacts.

### Log line

The pipeline should log the effective chat model at startup so the model in use is always visible in the artifact log:

```csharp
_logger.LogInformation("Chat model: {Model}", effectiveChatModel);
```

This can be a log line emitted before the pipeline runs, from the `compileCommand.SetAction` handler.

## Acceptance Criteria

1. `scripts/run.sh examples/FizzBuzz --chat-model ministral-3:3b` produces the same result as the current default run.
2. `scripts/run.sh examples/FizzBuzz --chat-model gemma4:e2b --force 00` deletes `00-extracted.spec` and re-extracts using `gemma4:e2b`. (Requires Ollama has the model pulled; Ollama returns an error if not, which is acceptable — the spec doesn't require auto-pull.)
3. Running without `--chat-model` behaves exactly as before.
4. All unit tests pass.
5. `--help` output includes the `--chat-model` option with its description.

## Not In Scope

- Per-pass model selection — all LLM calls use the same model per compilation.
- An `--embed-model` override — the embedding model is less often swapped and can be added in a follow-on spec if needed.
- Auto-pulling the model via Ollama if it is not present.
- Changing the `.env` format.
