# Model Evaluation — 2026-07-17

## Objective

Assess which locally-available Ollama models can reliably extract a valid `.spec` from a Markdown program description. All tests ran on CPU only (no GPU). The reference program is FizzBuzz (`examples/FizzBuzz/FizzBuzz.md`, 396 chars), compiled from scratch to a native binary with acceptance verification.

FizzBuzz is the hardest extraction case in the current example set because the "divisible by both 3 and 5" condition requires the model to infer `divisor: 15` — it must reason about the *implication* of the condition, not just copy text. A model that gets this right demonstrates it can follow the `.spec` format and apply light arithmetic reasoning.

## Hardware

CPU-only. Local machine. Ollama serving on `http://localhost:11434`.

## Results

| Model | Size | LLM pass time | Spec quality | Binary output | Acceptance |
|-------|------|--------------|--------------|---------------|------------|
| **ministral-3:3b** | 3.0 GB | 240–249s | Correct format, correct `divisor: 15`, no fences | Correct | **100/100 ✓** |
| **gemma4:e2b** | 7.2 GB | 369s | Correct structure, correct branch names — but **missing `divisor: 15`** | Compiled from cached artifacts; not re-built from this spec | **100/100 (false positive)** |
| **gemma3:1b** | 815 MB | 35s | Fenced output, `divisor: 0` on all branches, duplicate `true_output` keys | N/A — pipeline crashed | **crash (÷0 in AcceptanceCriteriaPass)** |
| **qwen2.5:0.5b** | 397 MB | 20s | Markdown-fenced pseudocode; used `if: "n mod 3 = 0 && n mod 5 = 0"` syntax; completely wrong format | Compiled to a "FizzBuzz" that prints only numbers | **0/100 ✗** |

## Observations

### ministral-3:3b — baseline, correct

Produced a clean `.spec` with all four branches, correct `divisor:` values, no fencing, snake_case condition names. Authorial criteria (100 assertions) extracted correctly via the second LLM call. Full pipeline end-to-end: graph construction → CFG → StackIR → MSIL → native binary → 100/100 acceptance. This is the current production model.

### gemma4:e2b — structurally aware, reasoning gap

Understood the task structure — correctly named all four branches including `divisible_by_15`. Failed to supply `divisor: 15`, leaving the branch with no divisor field. The pipeline compiled from that spec would have produced a program with no compound branch. The 100/100 acceptance result was a false positive: downstream artifacts were cached from a prior ministral-3b run and the binary was not rebuilt from gemma4:e2b's spec.

Speed: 369s, vs 240s for ministral-3b. Larger model (7.2 GB vs 3.0 GB), slower on CPU despite the "e2b" designation.

The failure mode is interesting: the model *identified* the condition correctly but didn't follow through that "divisible by both 3 and 5" implies `divisor: 15`. This is a reasoning gap (missing an arithmetic inference step), not a format-following gap.

### gemma3:1b — format breakdown

35s (fast, small model). Produced output wrapped in a markdown fence despite the prompt instruction to return only `.spec` content. Branch conditions were expressed as code expressions (`n % 3 == 0 and n % 5 == 0`) rather than `condition:` + `divisor:` pairs. Set `divisor: 0` on all branches (copied the field name but left it at zero). Duplicate `true_output` keys per branch. The `ValidateExtracted` check should have caught this but the spec parsed enough to produce a graph. `AcceptanceCriteriaPass` crashed with `DivideByZeroException` when evaluating `i % 0`. **This is a latent pipeline bug: `divisor: 0` is not validated at graph construction time and causes a crash downstream.**

### qwen2.5:0.5b — wrong format entirely

20s (fastest). Produced fenced pseudocode with an `if:` / `else:` syntax that bears no resemblance to the `.spec` format. The `ValidateExtracted` check passed (the text parsed enough to satisfy the graph's structural check — no `ProgramNode` missing error was thrown), but the graph had no branches, so the binary only printed numbers. Authorial criteria were extracted but wrong — the LLM assigned "FizzBuzz" to most iterations.

## Pipeline Bug Identified

`divisor: 0` in a branch reaches `AcceptanceCriteriaPass.ExecuteAsync` and causes an unhandled `DivideByZeroException`. It should be rejected in `SemanticGraphPass` (or `ValidateExtracted`) with a descriptive error. See the qwen2.5:0.5b and gemma3:1b results above — both produced `divisor: 0`.

## Conclusion

**ministral-3:3b is the correct choice for local CPU compilation.** It is the only model in this set that produces a correct `.spec` reliably. The quality bar for this task is not code generation — it is structured extraction with light arithmetic reasoning. A reasoning-tuned model in the 2–3B parameter range would be the next thing to evaluate if ministral-3b's quality proves insufficient for more complex programs.

Models tested but not yet available locally that would be worth evaluating: DeepSeek-R1 distill variants (reasoning-tuned, small footprints), Phi-4-mini (Microsoft, reasoning focus).
