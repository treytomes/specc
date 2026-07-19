# Spec 43 — WASM Backend

**Status:** Not started  
**Depends on:** Stack IR (pass 05) is the stable input boundary  
**Unlocks:** Running compiled programs in browsers and WASM runtimes without a .NET dependency

## Motivation

The pipeline currently terminates at MSIL (`06-program.il`) and a .NET PE (`07-program.dll` + apphost launcher). The stack IR in `05-stackir.json` is the last target-neutral artifact — everything downstream is .NET-specific. A WASM backend replaces passes 06 and 07 with a new pass that emits a `.wat` (WebAssembly Text Format) file and optionally assembles it to a `.wasm` binary.

This is a clean target because:
- The stack IR is already a stack machine model, which maps directly to WASM's stack machine semantics.
- All opcodes except `Intrinsic` and `RandInt` have direct WASM equivalents.
- The CFG has already linearised control flow into labelled blocks with explicit branches — WASM's structured control flow requires some restructuring, but the CFG shape is known and finite.

The WASM backend does not replace the IL backend. Both coexist; a `--target` flag selects which runs.

---

## What the stack IR covers and how it maps

| Opcode | WASM equivalent |
|--------|----------------|
| `LdcI4 n` | `i32.const n` |
| `LdlocS name` | `local.get $name` |
| `StlocS name` | `local.set $name` |
| `Add` | `i32.add` |
| `Sub` | `i32.sub` |
| `Mul` | `i32.mul` |
| `Div` | `i32.div_s` |
| `Rem` | `i32.rem_s` |
| `Ceq` | `i32.eq` |
| `Cgt` | `i32.gt_s` |
| `Clt` | `i32.lt_s` |
| `Label lbl` | Anchor for block/loop/br restructuring |
| `Br lbl` | `br` to enclosing block |
| `Brfalse lbl` | `i32.eqz` + `br_if` |
| `Brtrue lbl` | `br_if` |
| `Ret` | `return` |

### Opcodes requiring special handling

**`LdstrS` / `StlocStr` / `LdlocStr`** — WASM has no native string type. Strings are stored in linear memory as null-terminated UTF-8. The backend pre-populates a `data` segment with all string literals at compile time and represents string variables as `i32` memory offsets. This covers `true_output:` strings and `print:` templates.

**`Intrinsic` (console I/O)** — `console.write_line.string`, `console.write_line.int`, `console.read_line`, `int.parse`, `string.concat` have no WASM equivalent. They are implemented as WASM imports: the host environment (browser JS, WASI runtime) provides these as imported functions. The `.wat` declares them as `(import "env" "println_str" ...)` etc. A companion JS shim or WASI adapter provides the implementations.

**`RandInt`** — implemented as an imported function `(import "env" "rand_int" (func (param i32 i32) (result i32)))`. The host provides the random number generator.

**Arrays** (`Newarr`, `LdlocA`, `StlocA`, `LdelemI4`, `StelemI4`) — arrays are heap-allocated in linear memory. The backend emits a simple bump allocator in the `start` function. Array locals hold `i32` base pointers. Element access is `i32.load` / `i32.store` at `(base + index * 4)`.

### Control flow restructuring

WASM requires structured control flow: `block`/`loop`/`if` with `br`/`br_if` that target enclosing constructs by depth, not arbitrary labels. The CFG's flat label graph must be converted to nested WASM blocks.

The known CFG shapes (from `CfgPass`) map to WASM as follows:

**Loop programs** (`loop_test` → `loop_body` → `loop_test` back-edge):
```wat
(block $exit
  (loop $loop_top
    ;; loop_test: compare i <= max
    local.get $i
    i32.const <max>
    i32.gt_s
    br_if $exit        ;; exit when i > max
    ;; loop_body instructions
    ...
    ;; increment
    local.get $i
    i32.const 1
    i32.add
    local.set $i
    br $loop_top       ;; back edge
  )
)
```

**While loops** (`while_test` → body → `while_test` back-edge or `exit`):
```wat
(block $exit
  (loop $while_top
    ;; while_test: check condition
    ...
    i32.eqz
    br_if $exit        ;; condition false → exit
    ;; body
    ...
    br $while_top      ;; back edge
  )
)
```

**Branch chains** (`check_X` → `print_X` / fallthrough):
```wat
(block $end_branch
  (block $not_X
    ;; condition for X
    i32.eqz
    br_if $not_X
    ;; true path
    ...
    br $end_branch
  )
  ;; false path (next branch or default)
  ...
)
```

The restructuring algorithm walks the flat CFG block list in order and emits nested WASM blocks guided by the known CFG shapes. Since the CFG shape space is finite and enumerable (flat loop, while loop, interactive while, comparison branch, sort), the restructurer can dispatch on shape rather than doing a general loop nest analysis.

---

## New pass: `WatGenerationPass`

```
Pass number: 06w (runs after 05-StackIR, parallel slot to MsilGenerationPass)
Artifact:    06w-program.wat
Input:       context.StackIr, context.SemanticGraph, context.Locals (inferred from IR)
Output:      06w-program.wat (WebAssembly Text Format)
```

Active when `--target wasm` is passed on the command line. `MsilGenerationPass` and `AssemblyEmitPass` are skipped in this mode.

### Output structure

```wat
(module
  ;; Imports — host-provided I/O and RNG
  (import "env" "println_str"  (func $println_str  (param i32) (result)))
  (import "env" "println_i32"  (func $println_i32  (param i32) (result)))
  (import "env" "readln_i32"   (func $readln_i32   (result i32)))
  (import "env" "rand_int"     (func $rand_int     (param i32 i32) (result i32)))

  ;; Linear memory
  (memory 1)

  ;; String data segment — all string literals packed at fixed offsets
  (data (i32.const 0) "Guess a number between 1 and 100:\00")
  (data (i32.const 36) "Too low!\00")
  ...

  ;; Locals declaration
  ;; (emitted inside the main function)

  ;; Main function
  (func $main (export "main")
    (local $n i32)
    (local $target i32)
    ...
    ;; restructured control flow
  )
)
```

### String layout

All string literals from `LdstrS` opcodes and `true_output:` values are collected at pass start, assigned contiguous offsets in the data segment (null-terminated), and stored in a `Dictionary<string, int>` used by the emitter. String variables (`LdlocStr`/`StlocStr`) hold `i32` offsets into this segment.

---

## Optional: `WasmAssemblyPass`

If `wat2wasm` (from the WABT toolchain) is available on `PATH`, a second pass assembles the `.wat` to a `.wasm` binary:

```
Pass number: 07w
Artifact:    07w-program.wasm
```

This pass is skipped silently if `wat2wasm` is not found — the `.wat` file is still useful without it (Node.js, Deno, and browser devtools can load `.wat` directly).

---

## CLI addition

```
--target msil | wasm    Output target. Default: msil.
```

`Program.cs` selects the pass set based on `--target`:
- `msil` (default): existing `MsilGenerationPass` → `AssemblyEmitPass` chain
- `wasm`: `WatGenerationPass` → `WasmAssemblyPass` (if `wat2wasm` available)

Passes 00–05 are identical for both targets.

---

## Scope boundaries

**In scope:**
- All existing example programs (FizzBuzz, Collatz, Fibonacci, Guesser, GuessingGame, Calculator, Greetings) compile to `.wat` and produce correct output when run under a WASI runtime (e.g. `wasmtime`, `wasmer`) with the companion shim.
- A minimal JS shim (`shim.js`) is generated alongside the `.wat` for browser execution, providing the `env` imports.
- Sort programs (BubbleSort, SelectionSort) are explicitly out of scope for the initial pass — array lowering in WASM linear memory adds significant complexity that deserves its own spec.

**Not in scope:**
- General loop nest analysis (the restructurer dispatches on known CFG shapes only)
- GC or heap beyond the bump allocator needed for arrays
- WASI standard I/O (a future spec could replace the `env` import shim with proper WASI fd_write calls)
- Debugging or source maps

---

## Acceptance criteria

1. `FizzBuzz.md` compiled with `--target wasm` produces a `06w-program.wat` that, when run with `wasmtime --invoke main 06w-program.wasm` and the companion shim, outputs the correct 100 FizzBuzz lines.
2. `Collatz.md` compiled with `--target wasm` produces correct output for input 6: `6 3 10 5 16 8 4 2 1`.
3. `GuessingGame.spec` compiled with `--target wasm` produces a binary where the loop, comparison branches, and random number generation all function correctly.
4. `--target msil` (default) is unchanged — all existing 306 tests pass.
5. If `wat2wasm` is absent, `WasmAssemblyPass` logs a warning and skips; compilation does not fail.

---

## Files changed

| File | Change |
|------|--------|
| `Specc/Passes/WatGenerationPass.cs` | New pass: stack IR → `.wat` |
| `Specc/Passes/WasmAssemblyPass.cs` | New pass: `.wat` → `.wasm` via `wat2wasm` |
| `Specc/Program.cs` | `--target` option; conditional pass registration |
| `Specc/Passes/CompilationContext.cs` | `Target` property (`"msil"` \| `"wasm"`) |

No changes to passes 00–05 or the graph layer.
