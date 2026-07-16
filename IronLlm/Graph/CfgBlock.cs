using System.Text.Json.Serialization;

namespace IronLlm.Graph;

public record CfgBlock(
    [property: JsonPropertyName("label")]        string Label,
    [property: JsonPropertyName("instructions")] List<string> Instructions,
    [property: JsonPropertyName("successorTrue")]  string? SuccessorTrue,
    [property: JsonPropertyName("successorFalse")] string? SuccessorFalse);
