# Spec 36 — Spec Construct Router

**Status:** Not started
**Scope:** `MarkdownSpecPass.cs`; new `SpecConstructLibrary.cs`

## Motivation

The `MarkdownSpecPass` system prompt currently describes every construct in the `.spec` language to the LLM. As the language grows, this becomes a liability: large models lose focus around 30 tool/rule definitions, and ministral-3b at 3B parameters hits that wall earlier. The extraction calls for FizzBuzz already include rules for `assign:`, `print:`, `source: stdin`, and array types — constructs that are irrelevant noise for a divisibility-branch program.

The solution is a two-call router pattern (analogous to a tool router in agent systems): a cheap classifier call that identifies which construct families are needed, followed by a targeted extraction call whose system prompt includes only the relevant sections.

This keeps the `.spec` format viable as the language grows, and decouples per-call complexity from total language size. Adding a new construct means adding one classifier tag and one library section — it does not increase the size of any extraction call for programs that don't use it.

## Design

### Construct families

Each family groups one or more `.spec` keywords that tend to appear together:

| Tag | Keywords covered | Trigger words in prose |
|-----|-----------------|----------------------|
| `loop` | `loop: from: to:` | "iterate", "loop", "from 1 to", "for each", "for every" |
| `branch` | `branch: condition: divisor: true_output:` | "divisible", "multiple of", "if … print", "FizzBuzz" |
| `arithmetic` | `assign: op: left: right:` | "multiply", "add", "subtract", "compute", "fibonacci", "running total" |
| `input` | `variable: source: stdin`, `print:` (for prompts) | "read", "input", "ask the user", "stdin", "enter" |
| `array` | `variable: type: array[int]`, `initial_value:` | "array", "sort", "list", "elements", "swap" |
| `while` | `while: condition:` (Spec 34) | "until", "while", "repeat", "unbounded", "Collatz" |

The classifier is biased toward false positives — including an extra family slightly enlarges the extraction prompt but missing one breaks extraction entirely.

### Two-call flow

```
MarkdownSpecPass.ExecuteAsync(context)
│
├── Call 1: ClassifyAsync(markdown)
│     System prompt: static ~200-token classifier
│     Returns: string[] of family tags, e.g. ["loop", "branch"]
│
└── Call 2: ExtractSpecAsync(markdown, tags)
      System prompt: assembled from SpecConstructLibrary
        - preamble (always included, ~100 tokens)
        - per-family sections (only tagged families, ~80 tokens each)
        - rules block (always included, ~150 tokens)
      Returns: .spec text (unchanged from today)
```

The assembled prompt for a FizzBuzz extraction (`["loop", "branch"]`) is roughly the same size as today's static prompt. For a Greetings extraction (`["input"]`) it is smaller. For a Collatz extraction (`["loop", "arithmetic", "while"]`) it is larger than today but still within a focused window.

### SpecConstructLibrary

A static class (or record) that owns the text of each section:

```csharp
public static class SpecConstructLibrary
{
    public static string Preamble       => "...";
    public static string LoopSection    => "...";
    public static string BranchSection  => "...";
    public static string ArithSection   => "...";
    public static string InputSection   => "...";
    public static string ArraySection   => "...";
    public static string WhileSection   => "...";
    public static string Rules          => "...";

    public static string Assemble(IEnumerable<string> tags)
    {
        var sb = new StringBuilder(Preamble);
        foreach (var tag in tags)
        {
            sb.Append(tag switch
            {
                "loop"       => LoopSection,
                "branch"     => BranchSection,
                "arithmetic" => ArithSection,
                "input"      => InputSection,
                "array"      => ArraySection,
                "while"      => WhileSection,
                _            => ""
            });
        }
        sb.Append(Rules);
        return sb.ToString();
    }
}
```

### Classifier prompt

Short and static. Returns a JSON array of tag strings — constrained via `ChatOptions.ResponseFormat` with a trivial schema:

```json
{ "type": "array", "items": { "type": "string" } }
```

Classifier system prompt (~200 tokens):
```
You are a program classifier. Read the program description and return a JSON array
of construct families it requires. Choose from: "loop", "branch", "arithmetic",
"input", "array", "while". Include a tag if there is any chance the construct is
needed — it is better to include an extra tag than to miss one.
Examples:
  "FizzBuzz from 1 to 100, print Fizz/Buzz" → ["loop","branch"]
  "Fibonacci first 10 terms"                 → ["loop","arithmetic"]
  "Ask name, greet user"                     → ["input"]
  "Bubble sort an array of 10 ints"          → ["loop","array"]
  "Collatz sequence from user input"         → ["loop","arithmetic","input","while"]
Return only the JSON array.
```

### Changes to MarkdownSpecPass

- `ExtractSpecAsync` gains a `string[] tags` parameter and calls `SpecConstructLibrary.Assemble(tags)` instead of the current static `SpecSystemPrompt`.
- A new `ClassifyAsync(string markdown)` is called first, before `ExtractSpecAsync`.
- The static `SpecSystemPrompt` constant is removed (contents migrated to `SpecConstructLibrary`).
- `CriteriaSystemPrompt` is unchanged.

### Acceptance criteria

1. `scripts/test.sh` passes — all existing examples compile and all assertions pass.
2. A FizzBuzz extraction uses the `loop` + `branch` families and does NOT include the `input`, `array`, or `while` sections.
3. A Greetings extraction uses the `input` family and does NOT include `loop`, `branch`, `array`, or `while`.
4. Adding a new family section to `SpecConstructLibrary` requires no changes to `MarkdownSpecPass` beyond adding the tag to the classifier examples.

## Blockers

None — this is a pure refactor of the extraction prompt path. All downstream passes are unchanged.

## What this enables

- Spec 32 (Guesser) adds a `compare:` construct. With the router, FizzBuzz and Greetings extractions never see the `compare:` rules.
- Spec 34 (Collatz) adds `while:`. Same isolation.
- The language can grow to 10–15 construct families before any extraction call sees more than 4–5.
