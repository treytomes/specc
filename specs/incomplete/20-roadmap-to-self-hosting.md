# Spec 20 — Roadmap to Self-Hosting

**Status:** Design / Placeholder  
**Scope:** No implementation — milestone map for the constructs required to compile IronLlm itself

## The Goal

A compiler that can compile itself is the canonical completeness test. IronLlm's long-term target is to accept a Markdown description of a C#-like program — classes, methods, recursion, collections, file I/O — and lower it through the semantic graph pipeline to a running native binary. Once it can describe and compile its own passes, the feedback loop closes: each compilation adds to the repository, and future compilations draw on prior ones as grounding.

BubbleSort (Specs 15–19) establishes the first constructs beyond the FizzBuzz family: arrays, nested loops, comparison, swap. The gap between BubbleSort and self-hosting is large but enumerable.

## Required Milestones (not yet spec'd)

Each item below warrants its own numbered spec when the time comes. They are listed in rough dependency order — later items build on earlier ones.

### 21 — Functions and Call Stack

- `FunctionNode`, `CallNode`, `ReturnNode` in the graph.
- CFG: function prologue/epilogue blocks, `call`/`ret` patterns.
- StackIR: `call` with named target, argument push/pop.
- IL: `callvirt` or `call` to a defined method in the same assembly, separate `MethodBuilder` per function.
- Example: a program with a helper function (e.g. `IsEven(n) -> bool`).

### 22 — Recursion

- Depends on Spec 21.
- `RecursiveCallNode` or a `CallNode` whose target is the containing function.
- CFG: base case detection, recursive branch.
- Example: factorial or Fibonacci.

### 23 — Strings and String Operations

- `StringNode` (literal), `ConcatNode`, `FormatNode`.
- IL: `ldstr`, `string.Concat`, `string.Format`.
- Acceptance: string equality comparison in `AcceptanceCriteriaPass`.
- Example: a program that builds and prints a formatted message.

### 24 — Classes and Fields

- `ClassNode`, `FieldNode`, `ConstructorNode`.
- Requires generating multiple `TypeBuilder`s in `AssemblyEmitPass`.
- Needs a two-pass graph: class definitions resolved before method bodies.
- Example: a simple `Point` class with X/Y fields and a `ToString()` method.

### 25 — Collections (List, Dictionary)

- `CollectionNode`, `AddNode`, `LookupNode`.
- IL: `newobj` for generic types, `callvirt` for Add/TryGetValue.
- Requires generic type construction in `AssemblyEmitPass`.
- Example: a program that builds and iterates a `List<int>`.

### 26 — File I/O

- `FileReadNode`, `FileWriteNode`.
- IL: `System.IO.File.ReadAllText`, `File.WriteAllText`.
- Example: a program that reads a `.spec` file and writes a summary.

### 27 — Exception Handling

- `TryNode`, `CatchNode`, `ThrowNode`.
- CFG: exception regions, `leave`/`endfinally` opcodes.
- IL: `BeginExceptionBlock`, `BeginCatchBlock`, `EndExceptionBlock` in `ILGenerator`.

### 28 — Self-Hosting Bootstrap

- Depends on Specs 21–27.
- A Markdown description of `SemanticGraphPass` (or a simpler pass like `ParseSpecPass`) as the target program.
- The compiled binary should accept a `.spec` string and produce a `SemanticGraph` JSON.
- Acceptance: the compiled `ParseSpecPass` binary produces output identical to the C# implementation for a set of test inputs.

## Repository Value at Each Milestone

Each milestone adds a new family of graph patterns to the repository:
- After Spec 21: function call graphs join the corpus alongside loop graphs.
- After Spec 22: recursive call patterns — identifiable by a back-edge from `CallNode` to its containing `FunctionNode`.
- After Spec 24: class/field graphs — structurally distinct from procedural graphs; useful as priors when compiling other class-based programs.

By Spec 28, the repository contains enough prior compilations that a new spec describing an unfamiliar program can retrieve structurally similar subgraphs as grounding before generating any new IR. The "differentiable semantic graph" experiment becomes testable: do programs that solve similar problems land near each other in embedding space across the whole corpus?

## Notes

- The `.spec` file format will need to be extended or replaced for programs more complex than BubbleSort. A richer AST-like format (or direct Markdown → graph without an intermediate `.spec`) is likely needed by Spec 21.
- `AssemblyEmitPass` may need to be split as complexity grows: a `MethodILPass` that handles per-method instruction emission, and an `AssemblyLayoutPass` that handles type/method/field declarations.
- The semantic graph may need a second `ProgramNode` concept — a `ModuleNode` that contains multiple `ClassNode`s — before Spec 24.
