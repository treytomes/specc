using System.Text.Json;
using System.Text.Json.Serialization;
using Specc.Graph;

namespace Specc.Learning;

// Owns one NodeMlp per node kind. Loads from / saves to repository/node-mlp-weights.json.
// On first run (no weights file), initialises all MLPs with Xavier uniform random weights.
/// <summary>Manages one <see cref="NodeMlp"/> per node kind; persists weights to the repository directory.</summary>
public class NodeMlpRegistry
{
    private static readonly string[] KnownKinds =
    [
        "Program", "Loop", "Branch", "Print", "Modulo", "Variable",
        "Assign", "Input", "WhileLoop", "Comparison", "Array",
        "Index", "Swap", "NestedLoop", "Arithmetic",
    ];

    private readonly Dictionary<string, NodeMlp> _mlps;

    private NodeMlpRegistry(Dictionary<string, NodeMlp> mlps) => _mlps = mlps;

    /// <summary>Loads weights from <c>node-mlp-weights.json</c> in the repository directory, or creates random weights on first run.</summary>
    public static NodeMlpRegistry LoadOrCreate(string repositoryPath)
    {
        var weightsPath = WeightsPath(repositoryPath);
        if (File.Exists(weightsPath))
        {
            var json = File.ReadAllText(weightsPath);
            var dto  = JsonSerializer.Deserialize<RegistryDto>(json, JsonOpts)
                       ?? throw new InvalidOperationException("Failed to deserialize node-mlp-weights.json");
            var mlps = new Dictionary<string, NodeMlp>(dto.Weights.Count);
            foreach (var (kind, w) in dto.Weights)
                mlps[kind] = new NodeMlp(w.W1, w.B1, w.W2, w.B2);
            return new NodeMlpRegistry(mlps);
        }

        return CreateRandom();
    }

    /// <summary>Persists the current weights to <c>node-mlp-weights.json</c> in the repository directory.</summary>
    public void Save(string repositoryPath)
    {
        Directory.CreateDirectory(repositoryPath);
        var dto = new RegistryDto
        {
            Weights = _mlps.ToDictionary(
                kv => kv.Key,
                kv => new MlpDto { W1 = kv.Value.W1, B1 = kv.Value.B1, W2 = kv.Value.W2, B2 = kv.Value.B2 })
        };
        File.WriteAllText(WeightsPath(repositoryPath), JsonSerializer.Serialize(dto, JsonOpts));
    }

    /// <summary>Applies the MLP for this node's kind and returns the refined embedding; returns the raw embedding unchanged when the kind is unknown.</summary>
    public float[] Refine(Node node, float[] rawEmbedding, float[] neighborMean)
    {
        var kind = KindOf(node);
        return _mlps.TryGetValue(kind, out var mlp)
            ? mlp.Forward(rawEmbedding, neighborMean)
            : rawEmbedding;
    }

    private static NodeMlpRegistry CreateRandom()
    {
        var rng  = new Random(42); // deterministic seed for reproducibility
        var mlps = KnownKinds.ToDictionary(k => k, _ => NodeMlp.CreateRandom(rng));
        return new NodeMlpRegistry(mlps);
    }

    private static string WeightsPath(string repositoryPath) =>
        Path.Combine(repositoryPath, "node-mlp-weights.json");

    private static string KindOf(Node node) => node switch
    {
        ProgramNode    => "Program",
        LoopNode       => "Loop",
        BranchNode     => "Branch",
        PrintNode      => "Print",
        ModuloNode     => "Modulo",
        VariableNode   => "Variable",
        AssignNode     => "Assign",
        InputNode      => "Input",
        WhileLoopNode  => "WhileLoop",
        ComparisonNode => "Comparison",
        ArrayNode      => "Array",
        IndexNode      => "Index",
        SwapNode       => "Swap",
        NestedLoopNode => "NestedLoop",
        ArithmeticNode => "Arithmetic",
        _              => "Unknown",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class RegistryDto
    {
        public Dictionary<string, MlpDto> Weights { get; set; } = [];
    }

    private sealed class MlpDto
    {
        public float[][] W1 { get; set; } = [];
        public float[]   B1 { get; set; } = [];
        public float[][] W2 { get; set; } = [];
        public float[]   B2 { get; set; } = [];
    }
}
