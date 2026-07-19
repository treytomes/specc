using System.Text.Json.Serialization;

namespace Specc.Graph;

/// <summary>A basic block in the control flow graph.</summary>
/// <param name="Label">Unique block identifier used as a branch target.</param>
/// <param name="Instructions">Ordered list of CFG instruction strings for this block.</param>
/// <param name="SuccessorTrue">Label of the fall-through successor, or null for exit blocks.</param>
/// <param name="SuccessorFalse">Label of the branch-taken successor when the block ends with a conditional test.</param>
public record CfgBlock(
    [property: JsonPropertyName("label")]        string Label,
    [property: JsonPropertyName("instructions")] List<string> Instructions,
    [property: JsonPropertyName("successorTrue")]  string? SuccessorTrue,
    [property: JsonPropertyName("successorFalse")] string? SuccessorFalse);
