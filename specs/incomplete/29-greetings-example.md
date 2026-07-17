# Spec 29 — Greetings Example

## Status

Incomplete — blocked on: `InputNode` (stdin), `ConcatNode` (string concatenation), string variables.

## What this example is

`examples/Greetings/Greetings.md` describes a program that:

1. Prints an initial greeting.
2. Prints a prompt asking for the user's name.
3. Reads a line from stdin into a string variable.
4. Prints a farewell that embeds the name.

The program is **interactive** and **non-deterministic** at acceptance-test time, which requires a different verification strategy than the current pipeline.

## New graph nodes required

### `InputNode`

Reads a line from stdin and stores it in a named variable.

```
variable:
  name: user_name
  type: string
  source: stdin
```

In the semantic graph:

```json
{ "kind": "Input", "Name": "user_name", "Type": "string" }
```

- Connected to the `ProgramNode` via `EdgeType.Contains`.
- Lowered in `StackIrPass` to a `Console.ReadLine()` call and a `StlocS` into the named string variable.

### `ConcatNode`

Represents a string concatenation expression used inside a `PrintNode` template.

Templates already support `{variable}` substitution (e.g. `"Hello, {user_name}!"`). The existing `PrintNode` template mechanism covers this case — no new node type is needed if the template syntax handles it.

**Conclusion:** `ConcatNode` is not needed as a graph node. `PrintNode` templates already express string interpolation. The gap is in MSIL generation, not graph structure.

## New `.spec` syntax required

```
variable:
  name: user_name
  type: string
  source: stdin
```

- `type: string` — a new scalar type (currently only `int` is supported).
- `source: stdin` — signals that the variable is populated by a `Console.ReadLine()` call at that point in the program flow.

`SemanticGraphPass.EmitVariable` must be extended to handle `type: string` and `source: stdin`.

## CFG lowering

`CfgPass.LowerFlatLoop` is designed for counted-loop programs. `Greetings` has **no loop** — it is a linear sequence of three output operations with one input in between.

A new lowering path is needed for **linear programs** (no loop, no branch, no array):

```
entry → read_name → exit
```

CFG blocks:

| Block     | Instructions                                     | True |
|-----------|--------------------------------------------------|------|
| entry     | print "Hello! What's your name?"                 | read_name |
|           | print "Please enter your name:"                  |      |
| read_name | read user_name                                   | exit |
| exit      | print "Nice to meet you, {user_name}!"           | —    |

`CfgPass` detects a linear program by the absence of `LoopNode` and `ArrayNode`. It reads `PrintNode`s and `InputNode` from the graph and orders them.

## StackIR opcodes required

| Opcode        | Description                          |
|---------------|--------------------------------------|
| `Call_ReadLine` | Calls `Console.ReadLine()`, pushes result (string) |
| `LdlocStr`    | Load string local variable           |
| `StlocStr`    | Store string local variable          |

Alternatively: the existing `LdlocS`/`StlocS` opcodes are generic enough to cover string variables if the MSIL generator emits `ldloc`/`stloc` with `string` type. The critical new opcode is `Call_ReadLine`.

## MSIL generation

String local variables require `string` type in the `.locals` declaration instead of `int32`.

`PrintNode` templates with `{variable}` need `string.Concat` or `String.Format` in MSIL instead of direct `Console.WriteLine(int)`.

The simplest implementation: use `Console.WriteLine(string)` with `String.Concat` for multi-part outputs.

## Acceptance verification

`Greetings` cannot be acceptance-tested by running the binary and diffing stdout directly — the test runner would need to provide stdin input. Two options:

**Option A — fixed test input**: pipe a known name (e.g. `"Alice\n"`) to stdin and assert the output contains `"Alice"`. `AcceptanceVerificationPass` redirects stdin.

**Option B — structural acceptance only**: verify that the three output statements appear in order (greeting, prompt, farewell), and that the farewell contains the variable reference. This is a static graph check, not a runtime check.

Option A is simpler and consistent with the existing runtime verification approach. Add a `TestInput` field to `AuthorialAssertions` (or a new `AcceptanceInput` context field) that is piped to the process stdin.

For `Greetings.md`, the expected output block cannot be literal — it contains a dynamic name. The `ParseExpectedOutputBlock` path is not applicable. `MarkdownSpecPass` should detect that the expected output contains variable references and emit a placeholder assertion set.

## Implementation order

1. **String variable support** — `SemanticGraphPass.EmitVariable` handles `type: string`, `source: stdin` → `InputNode`.
2. **Linear CFG lowering** — `CfgPass` detects absence of loop/array and emits a sequenced block list.
3. **`Call_ReadLine` opcode** — `StackIrPass` lowers `read user_name` → `Call_ReadLine`, `StlocS user_name`.
4. **String MSIL** — `MsilGenerationPass` emits `string` locals, `String.Concat` for template interpolation, `Console.ReadLine()`.
5. **Acceptance with stdin** — `AcceptanceVerificationPass` pipes `TestInput` to the process when present.

## Scope boundary

This spec covers only `Greetings`. General string/input support (arbitrary programs with string variables and stdin) is out of scope — implement the minimum to make this one example compile and pass acceptance.
