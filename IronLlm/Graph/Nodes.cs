using System.Text.Json.Serialization;

namespace IronLlm.Graph;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ProgramNode), "Program")]
[JsonDerivedType(typeof(LoopNode), "Loop")]
[JsonDerivedType(typeof(BranchNode), "Branch")]
[JsonDerivedType(typeof(ConstantNode), "Constant")]
[JsonDerivedType(typeof(PrintNode), "Print")]
[JsonDerivedType(typeof(ModuloNode), "Modulo")]
[JsonDerivedType(typeof(ComparisonNode), "Comparison")]
[JsonDerivedType(typeof(VariableNode), "Variable")]
public abstract record Node(Guid Id, string Label);

public record ProgramNode(Guid Id, string Label, string Name) : Node(Id, Label);
public record LoopNode(Guid Id, string Label, int From, int To) : Node(Id, Label);
public record BranchNode(Guid Id, string Label, string Condition) : Node(Id, Label);
public record ConstantNode(Guid Id, string Label, int Value) : Node(Id, Label);
public record PrintNode(Guid Id, string Label, string Template) : Node(Id, Label);
public record ModuloNode(Guid Id, string Label, int Divisor) : Node(Id, Label);
public record ComparisonNode(Guid Id, string Label, string Op) : Node(Id, Label);
public record VariableNode(Guid Id, string Label, string Name, string Type) : Node(Id, Label);
