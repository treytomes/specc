using System.Text.Json;
using IronLlm.Graph;

namespace IronLlm.Tests.Passes;

/// <summary>
/// Verifies that the four new node kind discriminators round-trip cleanly
/// through System.Text.Json polymorphic serialization.
/// </summary>
public class NodeSerializationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private static T RoundTrip<T>(T node) where T : Node
    {
        var json        = JsonSerializer.Serialize<Node>(node, JsonOpts);
        var deserialized = JsonSerializer.Deserialize<Node>(json, JsonOpts)
            ?? throw new InvalidOperationException("Deserialized null");
        return (T)deserialized;
    }

    [Fact]
    public void ArrayNode_RoundTrips_WithCorrectKind()
    {
        var original = new ArrayNode(Guid.NewGuid(), "Array:arr[10]", "arr", "int", 10);
        var json     = JsonSerializer.Serialize<Node>(original, JsonOpts);

        Assert.Contains("\"kind\": \"Array\"", json);

        var deserialized = JsonSerializer.Deserialize<Node>(json, JsonOpts);
        Assert.IsType<ArrayNode>(deserialized);
    }

    [Fact]
    public void ArrayNode_RoundTrips_PreservesProperties()
    {
        var original     = new ArrayNode(Guid.NewGuid(), "Array:arr[10]", "arr", "int", 10);
        var deserialized = RoundTrip(original);

        Assert.Equal(original.Id,          deserialized.Id);
        Assert.Equal(original.Label,       deserialized.Label);
        Assert.Equal(original.Name,        deserialized.Name);
        Assert.Equal(original.ElementType, deserialized.ElementType);
        Assert.Equal(original.Size,        deserialized.Size);
    }

    [Fact]
    public void IndexNode_RoundTrips_WithCorrectKind()
    {
        var original = new IndexNode(Guid.NewGuid(), "Index:arr[i]", "arr", "i");
        var json     = JsonSerializer.Serialize<Node>(original, JsonOpts);

        Assert.Contains("\"kind\": \"Index\"", json);

        var deserialized = JsonSerializer.Deserialize<Node>(json, JsonOpts);
        Assert.IsType<IndexNode>(deserialized);
    }

    [Fact]
    public void IndexNode_RoundTrips_PreservesProperties()
    {
        var original     = new IndexNode(Guid.NewGuid(), "Index:arr[i]", "arr", "i");
        var deserialized = RoundTrip(original);

        Assert.Equal(original.Id,        deserialized.Id);
        Assert.Equal(original.Label,     deserialized.Label);
        Assert.Equal(original.ArrayName, deserialized.ArrayName);
        Assert.Equal(original.IndexExpr, deserialized.IndexExpr);
    }

    [Fact]
    public void SwapNode_RoundTrips_WithCorrectKind()
    {
        var original = new SwapNode(Guid.NewGuid(), "Swap:arr[j↔j+1]", "arr", "j", "j+1");
        var json     = JsonSerializer.Serialize<Node>(original, JsonOpts);

        Assert.Contains("\"kind\": \"Swap\"", json);

        var deserialized = JsonSerializer.Deserialize<Node>(json, JsonOpts);
        Assert.IsType<SwapNode>(deserialized);
    }

    [Fact]
    public void SwapNode_RoundTrips_PreservesProperties()
    {
        var original     = new SwapNode(Guid.NewGuid(), "Swap:arr[j↔j+1]", "arr", "j", "j+1");
        var deserialized = RoundTrip(original);

        Assert.Equal(original.Id,        deserialized.Id);
        Assert.Equal(original.Label,     deserialized.Label);
        Assert.Equal(original.ArrayName, deserialized.ArrayName);
        Assert.Equal(original.FromExpr,  deserialized.FromExpr);
        Assert.Equal(original.ToExpr,    deserialized.ToExpr);
    }

    [Fact]
    public void NestedLoopNode_RoundTrips_WithCorrectKind()
    {
        var original = new NestedLoopNode(Guid.NewGuid(), "NestedLoop:j<n-1", "j", 0, "n-1");
        var json     = JsonSerializer.Serialize<Node>(original, JsonOpts);

        Assert.Contains("\"kind\": \"NestedLoop\"", json);

        var deserialized = JsonSerializer.Deserialize<Node>(json, JsonOpts);
        Assert.IsType<NestedLoopNode>(deserialized);
    }

    [Fact]
    public void NestedLoopNode_RoundTrips_PreservesProperties()
    {
        var original     = new NestedLoopNode(Guid.NewGuid(), "NestedLoop:j<n-1", "j", 0, "n-1");
        var deserialized = RoundTrip(original);

        Assert.Equal(original.Id,        deserialized.Id);
        Assert.Equal(original.Label,     deserialized.Label);
        Assert.Equal(original.Variable,  deserialized.Variable);
        Assert.Equal(original.From,      deserialized.From);
        Assert.Equal(original.BoundExpr, deserialized.BoundExpr);
    }

    [Fact]
    public void GraphJson_WithAllFourNewNodeKinds_DeserializesWithoutError()
    {
        var nodes = new List<Node>
        {
            new ArrayNode(Guid.NewGuid(),      "Array:arr[5]",      "arr", "int", 5),
            new IndexNode(Guid.NewGuid(),      "Index:arr[0]",      "arr", "0"),
            new SwapNode(Guid.NewGuid(),       "Swap:arr[j↔j+1]",  "arr", "j", "j+1"),
            new NestedLoopNode(Guid.NewGuid(), "NestedLoop:j<n-1",  "j",   0,   "n-1"),
        };

        var json        = JsonSerializer.Serialize(nodes, JsonOpts);
        var deserialized = JsonSerializer.Deserialize<List<Node>>(json, JsonOpts);

        Assert.NotNull(deserialized);
        Assert.Equal(4, deserialized!.Count);
        Assert.IsType<ArrayNode>(deserialized[0]);
        Assert.IsType<IndexNode>(deserialized[1]);
        Assert.IsType<SwapNode>(deserialized[2]);
        Assert.IsType<NestedLoopNode>(deserialized[3]);
    }
}
