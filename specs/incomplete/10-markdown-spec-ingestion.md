# Spec 10 — Markdown Spec Ingestion

**Status:** Ready to implement  
**Scope:** New `Passes/MarkdownSpecPass.cs`, changes to `Program.cs` and `ParseSpecPass`

## Problem

The compiler currently accepts a `.spec` file — a structured key-value DSL written by hand:

```
program: FizzBuzz

loop:
  from: 1
  to: 100

branch:
  condition: divisible_by_15
  ...
```

The specification documents in `./specs/` are written in Markdown: free-form prose, motivation, code blocks, tables, acceptance criteria. A human author writing a new program description should be able to write a Markdown document in that style and feed it directly to the compiler without hand-authoring a `.spec` file.

## Solution

Add a `MarkdownSpecPass` that runs before `ParseSpecPass`. When the input file has a `.md` extension, this pass:

1. Reads the Markdown document.
2. Sends it to the LLM (ministral-3:3b) with a prompt that instructs it to extract the program specification as a `.spec` file.
3. Validates the output against the `.spec` grammar.
4. Writes it as `00-extracted.spec` in the artifacts directory.
5. Updates `context.SpecPath` to point at the extracted file.

`ParseSpecPass` then runs on `00-extracted.spec` as normal — nothing downstream changes.

## Pipeline Position

```
MarkdownSpecPass   ← new, runs only when input is .md
ParseSpecPass      ← now reads from 00-extracted.spec if MarkdownSpecPass ran
SemanticGraphPass
...
```

`MarkdownSpecPass.ArtifactFile` → `"00-extracted.spec"`

When the input is already a `.spec` file, `MarkdownSpecPass` is a no-op (it sets its artifact path to null and is skipped by the pipeline).

## LLM Prompt

This is the right job for the LLM: understanding intent from prose and translating it into a structured format — not constructing control flow, which is mechanical.

### System prompt

```
You are a compiler front-end. Your job is to read a program specification written
in Markdown and extract it as a structured .spec file.

The .spec format is:

  program: <name>

  loop:
    from: <int>
    to: <int>

  branch:
    condition: <snake_case_name>
    divisor: <int>          # omit if no modulo check
    true_output: "<string>" # quoted string or {variable}

  variable:
    name: <identifier>
    type: <type>

Rules:
1. Output ONLY the .spec content — no explanation, no markdown fences.
2. Use snake_case for all condition names.
3. Include a "default" branch (no divisor) for the fallback output.
4. The variable block must name the loop counter.
5. If the document describes a program that cannot be expressed in this format,
   output a single line: ERROR: <reason>
```

### Validation after extraction

After the LLM responds, run the same parser logic as `ParseSpecPass` against the output string. If parsing succeeds and produces a non-empty semantic graph, the extraction is accepted. If parsing fails or the LLM returned `ERROR:`, throw a `CompilationException` with the LLM's reason.

## Example Input

A Markdown document like `./specs/05-second-spec-bubble-sort.md` contains a "Spec File Draft" section with an embedded code block. The LLM should also be able to handle documents where the spec is described only in prose — e.g.:

> Write a program that iterates from 1 to 100. For multiples of 3 print "Fizz",
> for multiples of 5 print "Buzz", for multiples of both print "FizzBuzz",
> otherwise print the number.

Both should produce the same `00-extracted.spec` output.

## `Program.cs` Change

Detect the input file extension and insert `MarkdownSpecPass` at position 0 when it is `.md`:

```csharp
var passes = new List<ICompilerPass>();
if (specPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    passes.Add(new MarkdownSpecPass());
passes.AddRange([
    new ParseSpecPass(),
    new SemanticGraphPass(),
    ...
]);
```

## Artifact

`00-extracted.spec` is written to the artifacts directory immediately after the pass succeeds, before `ParseSpecPass` runs. It is a plain text file (not JSON) — it is the canonical intermediate between a Markdown description and the structured compiler input.

On an incremental re-run, if `00-extracted.spec` already exists it is loaded as-is and the LLM is not called again. Deleting it forces re-extraction.

## Example Markdown Input File

Create `examples/FizzBuzz/FizzBuzz.md` as a companion to `FizzBuzz.spec` — a human-readable description of the same program that, when passed to the compiler, produces an equivalent `00-extracted.spec`.
