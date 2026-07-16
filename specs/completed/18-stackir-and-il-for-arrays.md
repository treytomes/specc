# Spec 18 — StackIR and IL Emission for Arrays

**Status:** Ready to implement (after Spec 17)  
**Scope:** `StackIrPass.cs`, `MsilGenerationPass.cs`, `AssemblyEmitPass.cs`

## Motivation

The current StackIR and IL passes assume a single `int` local variable. BubbleSort requires an `int[]` local, element load/store, and three loop counter locals (`i`, `j`, `k`). This spec extends `LowerInstruction`, `MsilGenerationPass`, and `AssemblyEmitPass` to handle the new instruction forms introduced by Spec 17.

## New StackIR Opcodes

Add to `OpCode` enum in `StackInstruction.cs`:

| OpCode | Description |
|--------|-------------|
| `Newarr` | Allocate a new integer array. Operand: size as string. |
| `LdelemI4` | Load element from array at index. |
| `StelemI4` | Store value into array element at index. |
| `LdlocA` | Load array local (by name). Separate from `LdlocS` (int local) for clarity. |
| `StlocA` | Store array local (by name). |

Existing opcodes `LdlocS`, `StlocS`, `LdcI4`, `Cgt`, `Brfalse`, `Brtrue`, `Br`, `Call`, `Ret`, `Label` are unchanged.

## New StackIR Patterns

### Array init: `arr[0] = 64`

```
ldloc.a arr
ldc.i4  0        ; index
ldc.i4  64       ; value
stelem.i4
```

### Dynamic loop bound: `if j > (8 - i) goto outer_loop_inc`

```
ldloc.s j
ldc.i4  8
ldloc.s i
sub              ; 8 - i
cgt              ; j > (8 - i)
brtrue  outer_loop_inc
```

Add `Sub` to the `OpCode` enum (`Rem` already exists; `Sub` is the mirror for subtraction).

### Array comparison: `if arr[j] > arr[j+1]`

```
ldloc.a arr
ldloc.s j
ldelem.i4        ; arr[j]
ldloc.a arr
ldloc.s j
ldc.i4  1
add              ; j+1
ldelem.i4        ; arr[j+1]
cgt
```

### Array swap: `swap arr[j] arr[j+1]`

```
; temp = arr[j]
ldloc.a arr
ldloc.s j
ldelem.i4
stloc.s temp

; arr[j] = arr[j+1]
ldloc.a arr
ldloc.s j
ldloc.a arr
ldloc.s j
ldc.i4  1
add
ldelem.i4
stelem.i4

; arr[j+1] = temp
ldloc.a arr
ldloc.s j
ldc.i4  1
add
ldloc.s temp
stelem.i4
```

This requires a `temp` local (int). `StackIrPass` must declare it.

### Array print: `print arr[k]`

```
ldloc.a arr
ldloc.s k
ldelem.i4
call Console.WriteLine(int)
```

## MsilGenerationPass

Extend the text IL output to include the new opcodes:

```il
newarr  [mscorlib]System.Int32
ldelem.i4
stelem.i4
sub
```

Multiple locals require declaring them in sequence:

```il
.locals init (
    int32 V_0,    // i
    int32 V_1,    // j
    int32 V_2,    // k
    int32 V_3,    // temp
    int32[] V_4   // arr
)
```

`MsilGenerationPass` must collect all local variable declarations from the StackIR (by scanning for `StlocS`, `StlocA` operands) and emit a single `.locals init` block.

## AssemblyEmitPass

Extend the IL generator dispatch:

- `Newarr`: `il.Emit(OpCodes.Newarr, typeof(int))`
- `LdelemI4`: `il.Emit(OpCodes.Ldelem_I4)`
- `StelemI4`: `il.Emit(OpCodes.Stelem_I4)`
- `Sub`: `il.Emit(OpCodes.Sub)`
- `LdlocA` / `StlocA`: resolve to the array local index (same local table as int locals, just a different type)

`DeclareLocal` calls must be emitted in declaration order before any IL. AssemblyEmitPass already calls `il.DeclareLocal(typeof(int))` once; this becomes a loop over all unique local names found in the StackIR, with type `int[]` for array locals and `int` for scalar locals.

## SemanticValidationPass

Add Stack IR invariants:
- Every `LdlocA` / `StlocA` operand matches a declared array local.
- Every `LdelemI4` is preceded by a `LdlocA` and an index on the stack (static check: the instruction before `LdelemI4` must push an int — `LdcI4`, `LdlocS`, or `Add`).

## Tests

- `LowerInstruction` correctly lowers each new instruction form to the expected opcode sequence.
- `MsilGenerationPass` emits a `.locals init` block with all locals.
- `MsilGenerationPass` emits `newarr`, `ldelem.i4`, `stelem.i4`, `sub` for the corresponding opcodes.
- `SemanticValidationPass` passes for a valid BubbleSort StackIR.
