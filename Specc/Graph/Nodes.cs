using System.Text.Json.Serialization;

namespace Specc.Graph;

/// <summary>Base type for all semantic graph nodes.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Human-readable label used for diagnostics and embeddings.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ProgramNode),    "Program")]
[JsonDerivedType(typeof(LoopNode),       "Loop")]
[JsonDerivedType(typeof(BranchNode),     "Branch")]
[JsonDerivedType(typeof(ConstantNode),   "Constant")]
[JsonDerivedType(typeof(PrintNode),      "Print")]
[JsonDerivedType(typeof(ModuloNode),     "Modulo")]
[JsonDerivedType(typeof(ComparisonNode), "Comparison")]
[JsonDerivedType(typeof(VariableNode),   "Variable")]
[JsonDerivedType(typeof(AssertionNode),  "Assertion")]
[JsonDerivedType(typeof(ArrayNode),      "Array")]
[JsonDerivedType(typeof(IndexNode),      "Index")]
[JsonDerivedType(typeof(SwapNode),       "Swap")]
[JsonDerivedType(typeof(NestedLoopNode), "NestedLoop")]
[JsonDerivedType(typeof(ArithmeticNode), "Arithmetic")]
[JsonDerivedType(typeof(AssignNode),     "Assign")]
[JsonDerivedType(typeof(InputNode),      "Input")]
[JsonDerivedType(typeof(WhileLoopNode),  "WhileLoop")]
[JsonDerivedType(typeof(RandomNode),     "Random")]
public abstract record Node(Guid Id, string Label);

/// <summary>Root node representing the program being compiled.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Name">Program name as declared in the spec.</param>
public record ProgramNode(Guid Id, string Label, string Name) : Node(Id, Label);

/// <summary>A counted loop iterating over a fixed integer range.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="From">Inclusive loop start value.</param>
/// <param name="To">Inclusive loop end value.</param>
public record LoopNode(Guid Id, string Label, int From, int To) : Node(Id, Label);

/// <summary>A conditional branch with a named condition.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Condition">Snake-case condition name (e.g. <c>fizz</c>, <c>default</c>).</param>
public record BranchNode(Guid Id, string Label, string Condition) : Node(Id, Label);

/// <summary>An integer literal constant.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Value">The constant integer value.</param>
public record ConstantNode(Guid Id, string Label, int Value) : Node(Id, Label);

/// <summary>A print-to-stdout operation with a template string.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Template">Output template; may contain <c>{variable}</c> placeholders.</param>
public record PrintNode(Guid Id, string Label, string Template) : Node(Id, Label);

/// <summary>A modulo (remainder) check against a fixed divisor.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Divisor">The divisor used in the modulo test.</param>
public record ModuloNode(Guid Id, string Label, int Divisor) : Node(Id, Label);

/// <summary>A comparison between a variable and an integer or another variable.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Op">Comparison operator: <c>lt</c>, <c>gt</c>, or <c>eq</c>.</param>
/// <param name="Value">Integer right-hand side when comparing against a constant.</param>
/// <param name="RhsVar">Variable name right-hand side when comparing two variables.</param>
public record ComparisonNode(Guid Id, string Label, string Op, int Value = 0, string? RhsVar = null) : Node(Id, Label);

/// <summary>A named variable declaration with optional initial value.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Name">Variable identifier.</param>
/// <param name="Type">Variable type, e.g. <c>int</c> or <c>string</c>.</param>
/// <param name="InitialValue">Optional initial value set before the loop begins.</param>
public record VariableNode(Guid Id, string Label, string Name, string Type, int? InitialValue = null) : Node(Id, Label);

/// <summary>Metadata node recording an expected output line for acceptance verification.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Iteration">Loop iteration index this assertion applies to.</param>
/// <param name="Expected">Expected output string for that iteration.</param>
public record AssertionNode(Guid Id, string Label, int Iteration, string Expected) : Node(Id, Label);

/// <summary>A fixed-size integer array with optional initial element values.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Name">Array variable name.</param>
/// <param name="ElementType">Element type, typically <c>int</c>.</param>
/// <param name="Size">Number of elements in the array.</param>
/// <param name="Values">Initial element values, or null if not specified.</param>
public record ArrayNode(Guid Id, string Label, string Name, string ElementType, int Size, int[]? Values = null)
    : Node(Id, Label);

/// <summary>An indexed access into a named array.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="ArrayName">Name of the array being accessed.</param>
/// <param name="IndexExpr">Index expression, e.g. a variable name or literal.</param>
public record IndexNode(Guid Id, string Label, string ArrayName, string IndexExpr)
    : Node(Id, Label);

/// <summary>A swap of two elements within a named array.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="ArrayName">Name of the array containing the elements to swap.</param>
/// <param name="FromExpr">Index expression of the first element.</param>
/// <param name="ToExpr">Index expression of the second element.</param>
public record SwapNode(Guid Id, string Label, string ArrayName, string FromExpr, string ToExpr)
    : Node(Id, Label);

/// <summary>An inner loop whose upper bound is determined by an outer loop variable.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Variable">Loop counter variable name.</param>
/// <param name="From">Inclusive start value for the inner loop.</param>
/// <param name="BoundExpr">Expression string giving the exclusive upper bound.</param>
public record NestedLoopNode(Guid Id, string Label, string Variable, int From, string BoundExpr)
    : Node(Id, Label);

/// <summary>A binary arithmetic operation node (mul, add, or sub).</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Op">Operation: <c>mul</c>, <c>add</c>, or <c>sub</c>.</param>
public record ArithmeticNode(Guid Id, string Label, string Op) : Node(Id, Label);

/// <summary>An assignment of an arithmetic result (or copy) to a variable.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Target">Name of the variable being assigned.</param>
/// <param name="Op">Operation: <c>mul</c>, <c>add</c>, <c>sub</c>, <c>div</c>, or <c>copy</c>.</param>
/// <param name="Left">Left operand: a <c>{variable}</c> reference or integer literal.</param>
/// <param name="Right">Right operand, or null when <paramref name="Op"/> is <c>copy</c>.</param>
public record AssignNode(Guid Id, string Label, string Target, string Op, string Left, string? Right = null)
    : Node(Id, Label);

/// <summary>Reads a line from stdin into a named variable.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Name">Variable name to store the input into.</param>
/// <param name="Type">Variable type: <c>string</c> or <c>int</c>.</param>
public record InputNode(Guid Id, string Label, string Name, string Type = "string") : Node(Id, Label);

/// <summary>A while loop that repeats while a variable condition holds.</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Variable">Name of the variable tested each iteration.</param>
/// <param name="Op">Comparison operator: <c>ne</c>, <c>eq</c>, <c>lt</c>, or <c>gt</c>.</param>
/// <param name="Value">Integer right-hand side when comparing against a constant.</param>
/// <param name="RhsVar">Variable right-hand side when comparing two variables.</param>
public record WhileLoopNode(Guid Id, string Label, string Variable, string Op, int Value = 0, string? RhsVar = null)
    : Node(Id, Label);

/// <summary>Declares a variable whose value is a random integer in [Min, Max].</summary>
/// <param name="Id">Unique node identifier.</param>
/// <param name="Label">Node label.</param>
/// <param name="Name">Variable name for the generated random value.</param>
/// <param name="Min">Inclusive minimum of the random range.</param>
/// <param name="Max">Inclusive maximum of the random range.</param>
public record RandomNode(Guid Id, string Label, string Name, int Min, int Max) : Node(Id, Label);
