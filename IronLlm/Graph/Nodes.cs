using System.Text.Json.Serialization;

namespace IronLlm.Graph;

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
public abstract record Node(Guid Id, string Label);

public record ProgramNode(Guid Id, string Label, string Name) : Node(Id, Label);
public record LoopNode(Guid Id, string Label, int From, int To) : Node(Id, Label);
public record BranchNode(Guid Id, string Label, string Condition) : Node(Id, Label);
public record ConstantNode(Guid Id, string Label, int Value) : Node(Id, Label);
public record PrintNode(Guid Id, string Label, string Template) : Node(Id, Label);
public record ModuloNode(Guid Id, string Label, int Divisor) : Node(Id, Label);
public record ComparisonNode(Guid Id, string Label, string Op, int Value = 0) : Node(Id, Label);
public record VariableNode(Guid Id, string Label, string Name, string Type, int? InitialValue = null) : Node(Id, Label);
public record AssertionNode(Guid Id, string Label, int Iteration, string Expected) : Node(Id, Label);

public record ArrayNode(Guid Id, string Label, string Name, string ElementType, int Size, int[]? Values = null)
    : Node(Id, Label);

public record IndexNode(Guid Id, string Label, string ArrayName, string IndexExpr)
    : Node(Id, Label);

public record SwapNode(Guid Id, string Label, string ArrayName, string FromExpr, string ToExpr)
    : Node(Id, Label);

public record NestedLoopNode(Guid Id, string Label, string Variable, int From, string BoundExpr)
    : Node(Id, Label);

// op: "mul" | "add" | "sub"
public record ArithmeticNode(Guid Id, string Label, string Op) : Node(Id, Label);

// left/right: variable name wrapped in {}, or a bare integer literal; right is null for op: copy
public record AssignNode(Guid Id, string Label, string Target, string Op, string Left, string? Right = null)
    : Node(Id, Label);

// Reads a line from stdin into a named variable. Type is "string" or "int".
public record InputNode(Guid Id, string Label, string Name, string Type = "string") : Node(Id, Label);

// Loop that runs while Variable Op Value is true (e.g. n != 1).
// Body nodes are connected via EdgeType.Contains edges from this node.
public record WhileLoopNode(Guid Id, string Label, string Variable, string Op, int Value)
    : Node(Id, Label);
