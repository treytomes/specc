# Spec 42 — User-Defined Intrinsics

**Status:** Not started
**Depends on:** Spec 38 ✓ (intrinsic library and `Intrinsic` opcode in place)
**Blocks:** Any example that requires a third-party .NET library (OpenTK, Terminal.Gui, etc.)

## Motivation

The current `IntrinsicLibrary` is hardcoded in `IntrinsicLibrary.cs`. Adding OpenTK or Terminal.Gui
support today requires editing the compiler source and rebuilding. That is a reasonable constraint
for language builtins (`Console.ReadLine`, `int.Parse`) — they are part of the language definition.
It is an unreasonable constraint for application-domain libraries: a user writing an OpenTK program
should not need to fork the compiler.

This spec adds two things:

1. **`intrinsics.json`** — a user-supplied file, placed alongside the `.md` spec, that registers
   additional intrinsics from arbitrary .NET assemblies. No recompile required.
2. **`call:` spec construct** — a new `.spec` block type that emits an `Intrinsic` directly,
   with explicit arguments. This is the user-facing handle for invoking user-defined intrinsics
   from within a program spec.

The hardcoded table in `IntrinsicLibrary.cs` is not touched. User entries are loaded into the same
table at startup and are indistinguishable from builtins at the emit layer.

---

## Part 1 — `intrinsics.json`

### File location

The compiler searches for `intrinsics.json` in the following order, stopping at the first match:

1. Same directory as the input `.md` or `.spec` file.
2. The path supplied via `--intrinsics <path>` on the command line.

If neither is found, no user intrinsics are loaded (not an error).

### Calling convention contract

The intrinsic mechanism supports exactly two calling patterns:

1. **Static call** — `call T::Method(args)`. Arguments are pushed in declaration order, then the method is called. No receiver.
2. **Virtual call with local receiver** — `callvirt` on a method where the receiver is a local variable already on the stack (i.e. already pushed by prior CFG instructions before the `call:` block).

**Not supported:** patterns where a static property getter must be called *before* pushing arguments — for example `Random.Shared.Next(min, max)`, which requires `call Random::get_Shared()` first, then the arguments, then `callvirt`. This stack ordering cannot be expressed in the descriptor format. Such methods need a dedicated opcode (e.g. `RandInt`) with custom emit logic in `MsilGenerationPass` and `AssemblyEmitPass`.

If a user-supplied `intrinsics.json` entry requires this pattern, the compiler will not detect the mismatch at load time — the descriptor will appear valid but the emitted IL will have incorrect stack order. The constraint is documented here; enforcement is the caller's responsibility.

### Format

A JSON array of intrinsic descriptors. Each descriptor describes a **single** static or virtual method call:

```json
[
  {
    "name": "openTK.gl.clear",
    "assembly": "OpenTK.Graphics.dll",
    "type": "OpenTK.Graphics.OpenGL.GL",
    "method": "Clear",
    "parameters": ["System.Int32"],
    "isVirtual": false,
    "returns": "void",
    "inputs": ["int"],
    "ilText": "call void [OpenTK.Graphics]OpenTK.Graphics.OpenGL.GL::Clear(int32)",
    "embeddingHint": "Clears the OpenGL framebuffer with the given buffer mask."
  }
]
```

### Field reference

| Field | Required | Description |
|---|---|---|
| `name` | ✓ | Canonical name used in `call: intrinsic:` and in the `Intrinsic` opcode operand |
| `assembly` | ✓ | Filename or absolute path. Relative paths are resolved from the `intrinsics.json` directory |
| `type` | ✓ | Fully-qualified CLR type name (e.g. `OpenTK.Graphics.OpenGL.GL`) |
| `method` | ✓ | Method name (unambiguous if `parameters` is provided) |
| `parameters` | ✓ | Array of CLR type names for overload resolution (e.g. `["System.Int32", "System.String"]`) |
| `isVirtual` | | `true` → `callvirt`, `false` → `call`. Default `false` |
| `returns` | ✓ | `"void"` \| `"int"` \| `"string"` |
| `inputs` | ✓ | Array of `"int"` \| `"string"` \| `"void"`. Stack types consumed left-to-right (leftmost pushed first) |
| `ilText` | ✓ | IL assembly text emitted by `MsilGenerationPass` |
| `embeddingHint` | ✓ | One-line description used by `EmbeddingPass` |

### Assembly resolution

`assembly` is resolved in this order:

1. Absolute path — used as-is.
2. Relative to `intrinsics.json`'s own directory.
3. Relative to the working directory.

`Assembly.LoadFrom(resolvedPath)` is called once per unique assembly path at load time.
The resolved `MethodInfo`/`PropertyInfo` is cached in the descriptor's `Steps` list exactly
as hardcoded intrinsics are, so emit is identical.

### Loading

`IntrinsicLibrary` gains a new public static method:

```csharp
public static void LoadExtensions(string intrinsicsJsonPath)
```

Called from `Program.cs` before the pipeline starts, after finding `intrinsics.json` in one of
the search locations. Deserializes the array, resolves assemblies and methods via reflection,
constructs `IntrinsicDescriptor` objects with fully-resolved `Steps`, and calls `Register` for
each. Throws `CompilationException` with a clear message if an assembly cannot be found, a type
cannot be resolved, or a method signature doesn't match.

Name collisions with hardcoded intrinsics: last write wins (user can override a builtin by
reusing its name — unusual but permitted).

### CLI addition

```
--intrinsics <path>   Path to intrinsics.json (searched automatically if omitted)
```

Added to `Program.cs` alongside the existing `--spec`, `--out`, `--force` options.

---

## Part 2 — `call:` spec construct

### Syntax

```
call:
  intrinsic: <name>
  args:
    - <int_literal>
    - "<string_literal>"
    - {variable_name}
```

`args` may be omitted entirely for zero-argument intrinsics.

Each arg is one of:
- A bare integer literal (e.g. `16384`)
- A double-quoted string literal (e.g. `"Hello"`)
- A `{variable}` reference — the variable must be declared earlier in the spec

### Semantic graph node

```csharp
public record CallNode(Guid Id, string Label, string IntrinsicName, string[] Args) : Node(Id, Label);
```

`Label` is `"Call:{IntrinsicName}"`. `Args` stores the raw arg strings as they appear in the spec
(e.g. `"16384"`, `"\"Hello\""`, `"{n}"`).

`CallNode` is added to `Nodes.cs` with a JSON discriminator `"Call"`. It is skipped by
`EmbeddingPass` (like `AssertionNode`) — the intrinsic's `EmbeddingHint` is the semantic
description, not the call site.

### `SemanticGraphPass` parsing

A `call:` block is parsed into a `CallNode` and connected to its container (program or while loop)
via `EdgeType.Contains`. Multiple `call:` blocks are permitted; they are executed in declaration
order.

### `CfgPass` lowering

`CallNode` lowers to a sequence of CFG instruction strings in the current block:

```
ldarg {arg}          # one per arg: integer constant, string literal, or variable reference
call {intrinsicName}
```

Where `ldarg {arg}` is a new internal CFG instruction meaning "push this value" — rendered
as one of:
- `ldarg_int {n}` — push integer constant n
- `ldarg_str "{s}"` — push string literal s
- `ldarg_var {v}` — push variable v (int or string, resolved by type)

These mirror the existing `LdcI4` / `LdstrS` / `LdlocS` stack ops but are scoped to the call
lowering path so they don't conflict with loop variable instructions.

### `StackIrPass` patterns

```
ldarg_int {n}         → LdcI4 {n}
ldarg_str "{s}"       → LdstrS {s}
ldarg_var {v}         → LdlocS {v}  (or LdlocStr {v} for string variables)
call {intrinsicName}  → Intrinsic {intrinsicName}
```

The existing `Intrinsic` opcode handles emit in `MsilGenerationPass` and `AssemblyEmitPass`
unchanged — no new emit-layer code is needed.

### `SpecConstructLibrary.CallSection`

Added to `SpecConstructLibrary` and returned for the `"call"` tag in `Assemble`:

```
For programs that invoke a named API method directly, use call: blocks:

  call:
    intrinsic: <name>
    args:
      - <int_literal>
      - "<string_literal>"
      - {variable_name}

  args: may be omitted for zero-argument calls.
  The intrinsic name must match an entry in intrinsics.json alongside this spec.

  Example — clear an OpenGL framebuffer:
    call:
      intrinsic: openTK.gl.clear
      args:
        - 16384
```

### Classifier addition

`"call"` is added as a valid tag with the meaning:
> Direct invocation of a named API method; requires `intrinsics.json` alongside the spec.

Classifier prompt addition:
```
"call" — program directly invokes a named library method (e.g. OpenTK, Terminal.Gui)
```

---

## Known risk — `PersistedAssemblyBuilder` and cross-assembly `EmitCall`

`AssemblyEmitPass` uses `PersistedAssemblyBuilder` (in-process PE generation). When
`il.EmitCall(OpCodes.Call, methodInfo, null)` is called with a `MethodInfo` from an externally
loaded assembly (via `Assembly.LoadFrom`), the runtime must embed a metadata reference to that
assembly in the produced PE. This works correctly for assemblies in the standard TPA (Trusted
Platform Assemblies) list. For assemblies outside the TPA — which is the typical case for
third-party libraries — the behaviour depends on the runtime version and context.

If `EmitCall` fails or produces an unloadable PE for external assemblies, the fallback is to:

1. Emit the IL text via `MsilGenerationPass` to `06-program.il`.
2. Invoke `ilasm` (if available) to assemble it instead of using `AssemblyEmitPass`.

`AssemblyEmitPass` can detect this condition by catching the `InvalidOperationException` or
`BadImageFormatException` on write and retrying via `ilasm`. This fallback path is
**out of scope for this spec** — Spec 42 documents the risk and leaves the fallback to a
follow-on spec if the primary approach fails in practice.

---

## Files changed

| File | Change |
|---|---|
| `Specc/Graph/IntrinsicLibrary.cs` | Add `LoadExtensions(path)` and JSON deserialization for `intrinsics.json` |
| `Specc/Graph/Nodes.cs` | Add `CallNode` with JSON discriminator `"Call"` |
| `Specc/Passes/SemanticGraphPass.cs` | Parse `call:` blocks into `CallNode` |
| `Specc/Passes/CfgPass.cs` | Lower `CallNode` → `ldarg_*` + `call {name}` CFG instructions |
| `Specc/Passes/StackIrPass.cs` | Patterns for `ldarg_int`, `ldarg_str`, `ldarg_var`, `call {name}` |
| `Specc/Passes/SpecConstructLibrary.cs` | Add `CallSection`, update `Assemble` for `"call"` tag |
| `Specc/Passes/MarkdownSpecPass.cs` | Add `"call"` to classifier prompt |
| `Specc/Passes/EmbeddingPass.cs` | Skip `CallNode` (like `AssertionNode`) |
| `Specc/Program.cs` | Add `--intrinsics` option; call `IntrinsicLibrary.LoadExtensions` at startup |

---

## Acceptance criteria

1. An `intrinsics.json` alongside a `.md` file is loaded silently; the registered intrinsics
   appear in `IntrinsicLibrary.Table` before the pipeline starts.
2. A `.spec` with a `call:` block compiles to a binary that calls the named method at runtime.
3. A missing assembly path produces a `CompilationException` with the path in the message —
   not a `NullReferenceException` or a silent no-op.
4. A `call:` with no matching intrinsic name produces a `CompilationException` naming the
   unknown intrinsic.
5. All existing 306 tests pass — the `call:` path and `LoadExtensions` are purely additive.
6. A minimal OpenTK or Terminal.Gui example compiles and runs (smoke test only; not part of
   automated CI since it requires the library to be present).

---

## Example — Terminal.Gui "Hello" program

Each intrinsic descriptor covers exactly one method call. Sequencing multiple calls is done with
multiple `call:` blocks in the spec, not multiple steps in a single descriptor.

`examples/TerminalHello/intrinsics.json`:
```json
[
  {
    "name": "tgui.init",
    "assembly": "/path/to/Terminal.Gui.dll",
    "type": "Terminal.Gui.Application",
    "method": "Init",
    "parameters": [],
    "isVirtual": false,
    "returns": "void",
    "inputs": [],
    "ilText": "call void [Terminal.Gui]Terminal.Gui.Application::Init()",
    "embeddingHint": "Initializes the Terminal.Gui runtime."
  },
  {
    "name": "tgui.run",
    "assembly": "/path/to/Terminal.Gui.dll",
    "type": "Terminal.Gui.Application",
    "method": "Run",
    "parameters": [],
    "isVirtual": false,
    "returns": "void",
    "inputs": [],
    "ilText": "call void [Terminal.Gui]Terminal.Gui.Application::Run()",
    "embeddingHint": "Runs the Terminal.Gui event loop."
  }
]
```

`examples/TerminalHello/TerminalHello.spec`:
```
program: TerminalHello

call:
  intrinsic: tgui.init

call:
  intrinsic: tgui.run
```

The resulting binary initializes the Terminal.Gui runtime and enters its event loop.
