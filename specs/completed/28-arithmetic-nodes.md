# Spec 28 — Arithmetic Nodes (Multiply, Add-Variables)

**Status:** Completed  
**Scope:** `Graph/Nodes.cs`, `SemanticGraphPass.cs`, `CfgPass.cs`, `StackIrPass.cs`, `MsilGenerationPass.cs`, `Graph/StackInstruction.cs`

## Motivation

Specs 24 (Multiples) and 25 (Fibonacci) both require expressing integer arithmetic in the semantic graph. The current graph type system supports modulo (`ModuloNode`) and comparison (`ComparisonNode`) but no general arithmetic. Two programs are blocked:

- **Multiples**: `product = n * 7` — multiply loop counter by a constant
- **Fibonacci**: `a = a + b` — add two variables

Both require representing an expression whose operands are variables or constants, and whose result is stored in a variable.

## New Node Types

### `ArithmeticNode`

```csharp
[JsonDerivedType(typeof(ArithmeticNode), "Arithmetic")]
public record ArithmeticNode(Guid Id, string Label, string Op) : Node(Id, Label);
// Op values: "mul", "add", "sub"
```

Represents a binary arithmetic operation. Operands are expressed as graph edges (`Reads` from variable/constant nodes to the `ArithmeticNode`). The result is consumed by an `AssignNode`.

### `AssignNode`

```csharp
[JsonDerivedType(typeof(AssignNode), "Assign")]
public record AssignNode(Guid Id, string Label, string Target) : Node(Id, Label);
// Target: name of the variable being assigned
```

Represents `target = <arithmetic expression>`. Connected to an `ArithmeticNode` via a `Reads` edge and to the target `VariableNode` via a `Writes` edge.

## Spec Format Extensions

### Multiples pattern — `product = n * constant`

```
assign:
  target: product
  op: mul
  left: {n}
  right: 7
```

`SemanticGraphPass` parses this into `AssignNode(target:"product")` + `ArithmeticNode(op:"mul")` + edges: `AssignNode →Reads→ ArithmeticNode`, `ArithmeticNode →Reads→ VariableNode(n)`, `ArithmeticNode →Reads→ ConstantNode(7)`, `AssignNode →Writes→ VariableNode(product)`.

### Fibonacci pattern — `a = a + b`

```
assign:
  target: a
  op: add
  left: {a}
  right: {b}
```

Same structure; both operands are `VariableNode`s.

## CFG Lowering

Add `assign {target} {op} {left} {right}` as a new CFG instruction string:

```
assign product mul {n} 7
assign a add {a} {b}
assign b copy {tmp}
```

`CfgPass.LowerFlatLoop` emits assign instructions inside the loop body, before the print block. Order: all assigns, then print.

## StackIR Patterns

New patterns in `StackIrPass.LowerInstruction`:

```
"assign {target} mul {left} {right}"  →  load left, load right, Mul, StlocS target
"assign {target} add {left} {right}"  →  load left, load right, Add, StlocS target
```

Where each operand is either `{varName}` (variable load) or a literal integer (constant).

New opcode: `Mul` in `StackInstruction.OpCode`.

## MSIL

`MsilGenerationPass`: add `OpCode.Mul → "    mul"`.

## Acceptance Criteria

1. `examples/Multiples`: `scripts/run.sh examples/Multiples/Multiples.md` completes, binary prints 12 lines (7, 14, 21, ... 84), 12/12 assertions pass. ✓
2. `examples/Fibonacci`: `scripts/run.sh examples/Fibonacci/Fibonacci.md` completes, binary prints 10 lines (1, 1, 2, 3, 5, 8, 13, 21, 34, 55), 10/10 assertions pass. ✓ (Achieved after adding `op: copy` per Spec 30.)
3. All existing examples still pass (FizzBuzz 100/100, BubbleSort 10/10, SelectionSort 8/8). ✓
4. 278/278 unit tests pass, including 4 new tests. ✓

## Not In Scope

- Division (`div`) or bitwise ops — add in a follow-on spec.
- Nested arithmetic expressions — one level of `op(left, right)` is sufficient for both target programs.
- Changing how BubbleSort or SelectionSort are lowered.
