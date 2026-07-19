# Spec 38 — Intrinsic Library

**Status:** Completed
**Depends on:** Spec 33 (Calculator — linear programs with assigns) ✓
**Blocks:** Spec 37 (GuessingGame — `random:` requires `RandInt` intrinsic)

## Problem

Every new runtime capability requires touching four layers simultaneously:

1. A new `OpCode` enum member in `StackInstruction.cs`
2. A new `case` in `MsilGenerationPass` (IL text string)
3. A new `case` in `AssemblyEmitPass` (reflected `MethodInfo` + `il.EmitCall`)
4. A description string in `EmbeddingPass`

`ReadLine`, `ParseInt`, and `Concat` are already method-call semantics wearing typed opcode clothes. `Random.Shared.Next` (Spec 37) would be the fourth. The pattern will keep repeating for every I/O or runtime operation the language gains: string formatting, file I/O, math functions.

This is the same cliff as the tool-use wall (Spec 36): adding capability requires expanding a hardcoded list in every consumer.

## Design

### Intrinsic descriptors

Each intrinsic is a record with everything the emit passes need:

```csharp
public record IntrinsicDescriptor(
    string Name,              // canonical name used in StackIR operand
    MethodInfo Method,        // resolved once at startup via reflection
    bool IsVirtual,           // true → callvirt, false → call
    IrType[] Inputs,          // stack types consumed (top of stack last)
    IrType Returns,           // IrType.Void | Int | String
    string IlText,            // IL assembly text for MsilGenerationPass
    string EmbeddingHint      // one-line description for EmbeddingPass
);

public enum IrType { Void, Int, String }
```

### `IntrinsicLibrary`

A static class (or singleton) that registers all known intrinsics at startup:

```csharp
public static class IntrinsicLibrary
{
    private static readonly Dictionary<string, IntrinsicDescriptor> _table = [];

    static IntrinsicLibrary() => RegisterAll();

    public static IntrinsicDescriptor Get(string name) =>
        _table.TryGetValue(name, out var d) ? d
            : throw new InvalidOperationException($"Unknown intrinsic: {name}");

    public static bool TryGet(string name, out IntrinsicDescriptor d) =>
        _table.TryGetValue(name, out d!);

    private static void RegisterAll()
    {
        Register(new(
            Name: "console.read_line",
            Method: typeof(Console).GetMethod("ReadLine", Type.EmptyTypes)!,
            IsVirtual: false,
            Inputs: [],
            Returns: IrType.String,
            IlText: "call string [mscorlib]System.Console::ReadLine()",
            EmbeddingHint: "Reads a line of text from standard input."
        ));
        Register(new(
            Name: "int.parse",
            Method: typeof(int).GetMethod("Parse", [typeof(string)])!,
            IsVirtual: false,
            Inputs: [IrType.String],
            Returns: IrType.Int,
            IlText: "call int32 [mscorlib]System.Int32::Parse(string)",
            EmbeddingHint: "Parses a string into a 32-bit integer."
        ));
        Register(new(
            Name: "string.concat",
            Method: typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!,
            IsVirtual: false,
            Inputs: [IrType.String, IrType.String],
            Returns: IrType.String,
            IlText: "call string [mscorlib]System.String::Concat(string, string)",
            EmbeddingHint: "Concatenates two strings."
        ));
        Register(new(
            Name: "console.write_line.string",
            Method: typeof(Console).GetMethod("WriteLine", [typeof(string)])!,
            IsVirtual: false,
            Inputs: [IrType.String],
            Returns: IrType.Void,
            IlText: "call void [mscorlib]System.Console::WriteLine(string)",
            EmbeddingHint: "Writes a string followed by a newline to standard output."
        ));
        Register(new(
            Name: "console.write_line.int",
            Method: typeof(Console).GetMethod("WriteLine", [typeof(int)])!,
            IsVirtual: false,
            Inputs: [IrType.Int],
            Returns: IrType.Void,
            IlText: "call void [mscorlib]System.Console::WriteLine(int32)",
            EmbeddingHint: "Writes an integer followed by a newline to standard output."
        ));
        Register(new(
            Name: "random.next",
            Method: typeof(Random).GetMethod("Next", [typeof(int), typeof(int)])!,
            IsVirtual: true,
            Inputs: [IrType.Int, IrType.Int],  // min, max (exclusive) already on stack
            Returns: IrType.Int,
            IlText: "callvirt instance int32 [mscorlib]System.Random::Next(int32, int32)",
            EmbeddingHint: "Returns a random integer in a given range."
        ));
    }

    private static void Register(IntrinsicDescriptor d) => _table[d.Name] = d;
}
```

`random.next` is a virtual instance call — it needs `Random.Shared` on the stack first. The `StackIR` pattern for `rand_int name min max` stays as today's planned design but emits two instructions: `Intrinsic "random.shared"` (static property getter, pushes the `Random` instance) then `Intrinsic "random.next"`. Alternatively, wrap both into a single `rand_int` intrinsic with a composite emit. The simpler composite approach is preferred (see §OpCode migration below).

### OpCode migration

The four method-call opcodes are retired and replaced with a single generic:

```csharp
Intrinsic,  // call named intrinsic; operand = intrinsic name from IntrinsicLibrary
```

The typed opcodes (`ReadLine`, `ParseInt`, `Concat`) become operand values of `Intrinsic`:

| Old opcode | New form |
|---|---|
| `ReadLine` | `Intrinsic "console.read_line"` |
| `ParseInt` | `Intrinsic "int.parse"` |
| `Concat`   | `Intrinsic "string.concat"` |
| `Call` (string) | `Intrinsic "console.write_line.string"` |
| `Call` (int)   | `Intrinsic "console.write_line.int"` |

The old opcodes are removed from the enum. The `Call` opcode special-case in `StackIrPass` (which currently distinguishes int/string by operand content) is replaced by two explicit intrinsic names.

`RandInt` (Spec 37) is not added to the enum at all — it is `Intrinsic "rand_int"` with a composite descriptor that wraps both the `get_Shared` call and `Next(int, int)`.

### Composite intrinsics

Some operations require multiple CLR calls in sequence (e.g. `rand_int`: get singleton, then call `Next`). Represent these as a list of emit steps:

```csharp
public record IntrinsicDescriptor(
    string Name,
    IReadOnlyList<IntrinsicStep> Steps,  // replaces single Method
    IrType[] Inputs,
    IrType Returns,
    string IlText,
    string EmbeddingHint
);

public abstract record IntrinsicStep;
public record StaticCall(MethodInfo Method) : IntrinsicStep;
public record VirtualCall(MethodInfo Method) : IntrinsicStep;
public record StaticGet(PropertyInfo Property) : IntrinsicStep;
```

For `rand_int`:
```csharp
Register(new(
    Name: "rand_int",
    Steps: [
        new StaticGet(typeof(Random).GetProperty("Shared")!),
        // min and max are already on the stack from LdcI4 instructions above
        new VirtualCall(typeof(Random).GetMethod("Next", [typeof(int), typeof(int)])!)
    ],
    Inputs: [IrType.Int, IrType.Int],
    Returns: IrType.Int,
    IlText: "call class [mscorlib]System.Random [mscorlib]System.Random::get_Shared()\n    callvirt instance int32 [mscorlib]System.Random::Next(int32, int32)",
    EmbeddingHint: "Returns a random integer between min (inclusive) and max (inclusive)."
));
```

Simple (single-step) intrinsics use `Steps` of length 1, which covers `ReadLine`, `ParseInt`, `Concat`, and `WriteLine`.

### Changes to emit passes

**`AssemblyEmitPass`** — the switch over method-call opcodes is replaced:

```csharp
case IrOp.Intrinsic:
    var descriptor = IntrinsicLibrary.Get(instr.Operand!);
    foreach (var step in descriptor.Steps)
    {
        switch (step)
        {
            case StaticCall sc:
                il.EmitCall(OpCodes.Call, sc.Method, null);
                break;
            case VirtualCall vc:
                il.EmitCall(OpCodes.Callvirt, vc.Method, null);
                break;
            case StaticGet sg:
                il.EmitCall(OpCodes.Call, sg.Property.GetGetMethod()!, null);
                break;
        }
    }
    break;
```

**`MsilGenerationPass`** — the string-format switch arms are replaced:

```csharp
case OpCode.Intrinsic:
    var d = IntrinsicLibrary.Get(instr.Operand!);
    yield return $"    {d.IlText}";
    break;
```

**`StackIrPass`** — the patterns that emit `ReadLine`, `ParseInt`, `Concat` are updated to emit `Intrinsic` with the appropriate name. The `Call` opcode emission path is removed. No new patterns are needed beyond what Spec 37 adds.

**`EmbeddingPass`** — the hardcoded description strings that currently name `ReadLine`/`ParseInt` are removed; the library's `EmbeddingHint` fields carry those descriptions. When a node lowers to an `Intrinsic`, the hint is available by looking up the intrinsic name.

### Scope

This spec covers:
- `IntrinsicDescriptor` and `IntrinsicStep` types in `Specc/Graph/`
- `IntrinsicLibrary` static class in `Specc/Passes/` (or a new `Specc/Intrinsics/` folder)
- Migration of `ReadLine`, `ParseInt`, `Concat`, and `Call` opcodes to `Intrinsic`
- Removal of the four old opcode enum members
- `rand_int` intrinsic descriptor (unblocks Spec 37 without any further emit-pass changes)

Out of scope: adding new intrinsics beyond those needed for Spec 37. The library is intentionally small; new entries require only `Register(new(...))` in `RegisterAll()`.

## Files changed

| File | Change |
|---|---|
| `Specc/Graph/StackInstruction.cs` | Add `Intrinsic` opcode; remove `ReadLine`, `ParseInt`, `Concat` |
| `Specc/Graph/IntrinsicLibrary.cs` | New file: descriptors, steps, registration |
| `Specc/Passes/AssemblyEmitPass.cs` | Replace 4 switch arms with single `Intrinsic` case |
| `Specc/Passes/MsilGenerationPass.cs` | Replace 4 string-format arms with single `Intrinsic` case |
| `Specc/Passes/StackIrPass.cs` | Update `read`, `read_int`, `print`, concat patterns to emit `Intrinsic` |
| `Specc/Passes/EmbeddingPass.cs` | Remove hardcoded method-call descriptions |

## Acceptance

All existing tests (284) continue to pass after the migration. No new graph constructs, no new spec syntax, no new examples required — this is a pure compiler-internal refactor.

The `rand_int` descriptor is registered but not exercised until Spec 37 adds the `random:` construct.
