# Spec 11 — Unit Test Suite (80% Coverage Gate)

**Status:** Ready to implement  
**Scope:** New `Specc.Tests/` xUnit project; coverage enforced in CI via Coverlet; updated `scripts/test.sh`

## Goal

Establish a test foundation that must pass before new features merge. The requirement: **≥ 80% line coverage** across the `Specc` project, measured by Coverlet and enforced in `test.sh`.

Unit tests target only deterministic code — passes that transform one data structure into another without I/O. External-dependency passes (EmbeddingPass, AssemblyEmitPass) are excluded from the coverage denominator via `[ExcludeFromCodeCoverage]`.

## Test project layout

```
Specc.Tests/
  Specc.Tests.csproj
  Passes/
    ParseSpecPassTests.cs
    SemanticGraphPassTests.cs
    CfgPassTests.cs
    StackIrPassTests.cs
    MsilGenerationPassTests.cs
  Graph/
    StackInstructionTests.cs
```

## Coverage strategy

| Pass | Approach | Rationale |
|------|----------|-----------|
| `ParseSpecPass` | Unit | Pure text → context field assignment |
| `SemanticGraphPass` | Unit | Deterministic graph construction |
| `CfgPass` | Unit | Fixed 11-block CFG from graph |
| `StackIrPass` | Unit | Pattern-match CFG → stack opcodes |
| `MsilGenerationPass` | Unit | Stack IR → IL text |
| `EmbeddingPass` | Excluded | Requires live Ollama |
| `AssemblyEmitPass` | Excluded | Filesystem + PE emit; covered by test.sh |
| `ArtifactWriter` | Excluded | I/O only |
| `Program.cs` | Excluded | Entry point glue |

Mark excluded classes with `[ExcludeFromCodeCoverage]` (or via `.runsettings` filter).

## Test cases per pass

### ParseSpecPass

- Parses a minimal spec (program name only) without throwing
- `RawSpec` is populated on context after execution
- Spec with all field types (loop, multiple branches, variable) parses without error
- Invalid YAML-like content throws a descriptive exception (or sets a known error state)

### SemanticGraphPass

Using the FizzBuzz spec as fixture input:

- Graph contains a `ProgramNode` with label `"FizzBuzz"`
- Graph contains exactly one `LoopNode` (from=1, to=100)
- Graph contains four `BranchNode`s (divisible_by_15/3/5 + default)
- Graph contains a `VariableNode` named `"n"`
- Every node has a non-empty `Id`
- Edges include a `Contains` edge from the program node to each branch
- Node count and edge count match expected values

### CfgPass

Using a pre-built `CompilationContext` with a FizzBuzz `SemanticGraph` (no I/O — pass context directly):

- Produces exactly 11 blocks
- Block labels are the canonical set: `entry`, `loop_test`, `fizzbuzz_check`, `fizz_check`, `buzz_check`, `print_fizzbuzz`, `print_fizz`, `print_buzz`, `print_n`, `loop_inc`, `exit`
- `entry` block has no predecessors (SuccessorTrue = `loop_test`)
- `loop_test` has two successors: one to `loop_inc`, one to `exit`
- Each `print_*` block's SuccessorTrue points to `loop_inc`
- `loop_inc` successor is `loop_test`
- `exit` block contains a `ret` instruction

### StackIrPass

Using a pre-built `CompilationContext` with the canonical 11-block CFG:

- Produces a non-empty `StackIr` list
- The IR contains at least one `LdcI4` (loop init)
- The IR contains at least one `Rem` (modulo check)
- The IR contains at least one `LdstrS` (string literal output)
- The IR contains at least one `Call` (Console.WriteLine)
- The IR contains at least one `Ret`
- The IR contains `Label` instructions matching each CFG block label
- No `null` operand on ops that require one (`LdcI4`, `LdstrS`, `Label`, `Br`, `Brfalse`, `Brtrue`)

### MsilGenerationPass

Using a pre-built `CompilationContext` with known `StackIr`:

- `MsilOutput` is non-null and non-empty after execution
- Output contains `.assembly FizzBuzz`
- Output contains `.entrypoint`
- Output contains `call void [mscorlib]System.Console::WriteLine`
- Output contains `ret`
- Each `Label` instruction in the IR appears as a label line in the IL text

## NuGet packages

```xml
<PackageReference Include="xunit"                         Version="2.9.*" />
<PackageReference Include="xunit.runner.visualstudio"     Version="2.8.*" />
<PackageReference Include="Microsoft.NET.Test.Sdk"        Version="17.12.*" />
<PackageReference Include="coverlet.collector"            Version="6.0.*" />
```

## Coverage enforcement

`scripts/test.sh` gains a unit-test + coverage step before the end-to-end pipeline check:

```bash
dotnet test Specc.Tests/Specc.Tests.csproj \
  --collect:"XPlat Code Coverage" \
  --results-directory "$REPO_ROOT/TestResults"

# Extract line coverage % from the generated coverage.cobertura.xml
coverage=$(python3 -c "
import xml.etree.ElementTree as ET, sys
tree = ET.parse(sys.argv[1])
root = tree.getroot()
rate = float(root.attrib.get('line-rate', 0)) * 100
print(f'{rate:.1f}')
" "$(find "$REPO_ROOT/TestResults" -name 'coverage.cobertura.xml' | head -1)")

check "unit test coverage ≥ 80% (got ${coverage}%)" \
  "$( python3 -c "print('ok' if float('$coverage') >= 80 else 'below threshold')" )"
```

## Test fixtures

A `Fixtures/` folder holds the FizzBuzz spec as a string constant and a factory method that builds a pre-populated `CompilationContext` at each pipeline stage — so each test file can start from the right level without repeating setup:

```csharp
static class Fixtures
{
    public const string FizzBuzzSpec = """
        program: FizzBuzz
        loop:
          from: 1
          to: 100
        branch:
          condition: divisible_by_15
          divisor: 15
          true_output: "FizzBuzz"
        ...
        """;

    public static CompilationContext AfterParse()  { ... }
    public static CompilationContext AfterGraph()  { ... }
    public static CompilationContext AfterCfg()    { ... }
    public static CompilationContext AfterStackIr() { ... }
}
```

## Commit scope

One commit: `test: add xUnit test suite with ≥80% coverage gate`

Updated files: `Specc.slnx` (add test project), `scripts/test.sh` (add coverage step).
