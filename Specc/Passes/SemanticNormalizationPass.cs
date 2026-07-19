using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Specc.Graph;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

// Validates and normalises each graph node against a small reference corpus of
// canonical node-type descriptions, embedded at pass startup.
// Nodes whose embedding is closest to a canonical above the similarity threshold
// have their labels rewritten to the stable "Kind:value" form.
// Nodes below threshold cause a CompilationException — they represent content
// the downstream passes cannot safely interpret.
/// <summary>Validates and re-labels graph nodes by comparing their embeddings against a canonical reference corpus.</summary>
[ExcludeFromCodeCoverage(Justification = "Requires live Ollama; covered by scripts/test.sh")]
public class SemanticNormalizationPass : ICompilerPass
{
    // Rejection threshold: nodes below this score don't match any known type.
    // Empirically calibrated for mxbai-embed-large: well-formed nodes of the same type
    // score 0.68-0.78; unrelated concepts score below 0.50. 0.60 leaves a safe margin.
    private const float Threshold = 0.60f;

    // Reclassification threshold: only reclassify a node to a different kind when the
    // best-match score clears this bar. Same-type nodes score 0.68-0.78 so 0.80 requires
    // a clear signal before changing the kind the graph builder already assigned.
    private const float ReclassifyThreshold = 0.80f;

    // Short, type-focused descriptions without instance-specific values.
    // Avoiding proper nouns and concrete numbers keeps similarity high across all instances.
    private static readonly (string Kind, string Description)[] ReferenceCorpus =
    [
        ("Program",    "A named executable program."),
        ("Loop",       "A loop that iterates over a range of integers."),
        ("Branch",     "A conditional if-then branch."),
        ("Print",      "Print a value or string to standard output."),
        ("Modulo",     "Integer modulo, the remainder after division."),
        ("Variable",   "An integer variable declaration."),
        ("Constant",   "An integer literal constant."),
        ("Comparison", "A comparison between two integer values."),
        ("Array",      "An array of integer values with a fixed size."),
        ("Index",      "Access to an array element by index position."),
        ("Swap",       "Swap two elements in an array."),
        ("NestedLoop",  "An inner loop whose upper bound depends on an outer loop variable."),
        ("Arithmetic",  "A binary arithmetic operation: multiply, add, or subtract."),
        ("Assign",      "Assign the result of an arithmetic operation to a variable."),
        ("Input",       "Read a string value from standard input into a named variable."),
    ];

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ILogger<SemanticNormalizationPass> _logger;

    /// <summary>Initialises the pass with an embedding generator and a logger.</summary>
    public SemanticNormalizationPass(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ILogger<SemanticNormalizationPass> logger)
    {
        _embedder = embedder;
        _logger   = logger;
    }

    /// <inheritdoc/>
    public string Name          => "03b-SemanticNormalization";
    /// <inheritdoc/>
    public string? ArtifactFile  => "03b-normalized-graph.json";

    /// <inheritdoc/>
    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json    = await File.ReadAllTextAsync(artifactPath);
        var opts    = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var wrapper = JsonSerializer.Deserialize<GraphDto>(json, opts)
                      ?? throw new InvalidOperationException("Could not deserialize normalized graph");
        var graph = new SemanticGraph();
        if (wrapper.Nodes != null) graph.Nodes.AddRange(wrapper.Nodes);
        if (wrapper.Edges != null) graph.Edges.AddRange(wrapper.Edges);
        context.SemanticGraph  = graph;
        context.GraphNormalized = true;
    }

    private sealed class GraphDto
    {
        public List<Node>? Nodes { get; set; }
        public List<Edge>? Edges { get; set; }
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(CompilationContext context)
    {
        var graph = context.SemanticGraph
            ?? throw new InvalidOperationException("SemanticGraph not set");

        if (context.Embeddings.Count == 0)
            throw new InvalidOperationException("Embeddings not set — run EmbeddingPass first");

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Embedding {Count} reference canonicals", ReferenceCorpus.Length);

        var refDescriptions = ReferenceCorpus.Select(r => r.Description).ToList();
        var refResults = await _embedder.GenerateAsync(refDescriptions);
        var references = ReferenceCorpus
            .Zip(refResults, (r, e) => (r.Kind, Vector: e.Vector.ToArray()))
            .ToArray();

        var embeddingMap = context.Embeddings.ToDictionary(e => e.NodeId);
        var reclassified = 0;
        var normalised   = 0;

        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];

            // AssertionNodes are metadata; ArithmeticNode/AssignNode/InputNode/RandomNode are exact-typed from the parsed spec.
            // None of these need similarity-based validation.
            if (node is AssertionNode or ArithmeticNode or AssignNode or InputNode or RandomNode) continue;

            if (!embeddingMap.TryGetValue(node.Id, out var embedding))
            {
                _logger.LogWarning("No embedding found for node '{Label}' — skipping normalization", node.Label);
                continue;
            }

            var (bestKind, bestScore) = BestMatch(embedding.Vector, references);

            if (bestScore < Threshold)
                throw new CompilationException(
                    $"Node '{node.Label}' (best similarity {bestScore:F2}) does not match any known node type. " +
                    $"Closest match: {bestKind} @ {bestScore:F2}. Threshold is {Threshold:F2}.");

            var currentKind = KindOf(node);

            if (currentKind != bestKind && bestScore >= ReclassifyThreshold)
            {
                _logger.LogWarning(
                    "Node '{Label}' reclassified from {From} to {To} (similarity {Score:F2})",
                    node.Label, currentKind, bestKind, bestScore);

                var reclassifiedNode = Reclassify(node, bestKind);
                graph.Nodes[i] = reclassifiedNode;
                node = reclassifiedNode;
                reclassified++;
            }

            var normalized = NormalizeLabel(node);
            if (normalized != node.Label)
            {
                graph.Nodes[i] = node with { Label = normalized };
                normalised++;
            }
        }

        context.GraphNormalized = true;
        _logger.LogInformation(
            "Pass {Name} completed in {ElapsedMs}ms — {Reclassified} reclassified, {Normalised} labels rewritten",
            Name, sw.ElapsedMilliseconds, reclassified, normalised);
    }

    /// <summary>Returns the canonical kind whose reference vector has the highest cosine similarity to <paramref name="vector"/>.</summary>
    public static (string Kind, float Score) BestMatch(float[] vector, (string Kind, float[] Vector)[] references)
    {
        var best = references[0];
        var bestScore = CosineSimilarity(vector, best.Vector);
        for (var i = 1; i < references.Length; i++)
        {
            var score = CosineSimilarity(vector, references[i].Vector);
            if (score > bestScore)
            {
                bestScore = score;
                best = references[i];
            }
        }
        return (best.Kind, bestScore);
    }

    /// <summary>Computes the cosine similarity between two vectors.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        var dot  = 0f;
        var magA = 0f;
        var magB = 0f;
        var len  = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom == 0f ? 0f : dot / denom;
    }

    private static string KindOf(Node node) => node switch
    {
        ProgramNode    => "Program",
        LoopNode       => "Loop",
        BranchNode     => "Branch",
        PrintNode      => "Print",
        ModuloNode     => "Modulo",
        VariableNode   => "Variable",
        ConstantNode   => "Constant",
        ComparisonNode => "Comparison",
        ArrayNode      => "Array",
        IndexNode      => "Index",
        SwapNode       => "Swap",
        NestedLoopNode => "NestedLoop",
        ArithmeticNode => "Arithmetic",
        AssignNode     => "Assign",
        InputNode      => "Input",
        _              => "Unknown",
    };

    private static Node Reclassify(Node node, string targetKind)
    {
        return targetKind switch
        {
            "Print"      => new PrintNode(node.Id, node.Label, ExtractTemplate(node.Label)),
            "Loop"       => new LoopNode(node.Id, node.Label, 1, 100),
            "Branch"     => new BranchNode(node.Id, node.Label, Slugify(node.Label)),
            "Modulo"     => new ModuloNode(node.Id, node.Label, ExtractDivisor(node.Label)),
            "Variable"   => new VariableNode(node.Id, node.Label, ExtractIdentifier(node.Label), "int"),
            "Constant"   => new ConstantNode(node.Id, node.Label, ExtractNumber(node.Label)),
            "Program"    => new ProgramNode(node.Id, node.Label, ExtractIdentifier(node.Label)),
            "Comparison" => new ComparisonNode(node.Id, node.Label, "=="),
            "Array"      => new ArrayNode(node.Id, node.Label, ExtractIdentifier(node.Label), "int", 10),
            "Index"      => new IndexNode(node.Id, node.Label, ExtractArrayName(node.Label), "0"),
            "Swap"       => new SwapNode(node.Id, node.Label, ExtractArrayName(node.Label), "j", "j+1"),
            "NestedLoop"  => new NestedLoopNode(node.Id, node.Label, ExtractIdentifier(node.Label), 0, "n-1"),
            "Arithmetic"  => new ArithmeticNode(node.Id, node.Label, "add"),
            "Assign"      => new AssignNode(node.Id, node.Label, ExtractIdentifier(node.Label), "add", "0", "0"),
            _ => throw new CompilationException(
                $"Cannot reclassify node '{node.Label}' to unknown kind '{targetKind}'"),
        };
    }

    private static string NormalizeLabel(Node node) => node switch
    {
        ProgramNode p     => $"Program:{p.Name}",
        LoopNode l        => $"Loop:{l.From}..{l.To}",
        BranchNode b      => $"Branch:{b.Condition}",
        PrintNode pr      => $"Print:{pr.Template}",
        ModuloNode m      => $"Modulo:{m.Divisor}",
        VariableNode v    => $"Var:{v.Name}",
        ConstantNode c    => $"Constant:{c.Value}",
        ComparisonNode c  => c.Value != 0 ? $"Comparison:{c.Op}:{c.Value}" : $"Comparison:{c.Op}",
        ArrayNode a       => $"Array:{a.Name}[{a.Size}]",
        IndexNode ix      => $"Index:{ix.ArrayName}[{ix.IndexExpr}]",
        SwapNode sw       => $"Swap:{sw.ArrayName}[{sw.FromExpr}↔{sw.ToExpr}]",
        NestedLoopNode nl => $"NestedLoop:{nl.Variable}<{nl.BoundExpr}",
        ArithmeticNode ar => $"Arithmetic:{ar.Op}",
        AssignNode an     => an.Op == "copy"
            ? $"Assign:{an.Target}=copy({an.Left})"
            : $"Assign:{an.Target}={an.Op}({an.Left},{an.Right})",
        InputNode inp     => $"Input:{inp.Name}",
        _                 => node.Label,
    };

    // Heuristic value extractors used by Reclassify when type information is lost.
    private static string ExtractTemplate(string label)
    {
        // "write 'Fizz' to stdout" → "Fizz"
        var quoted = System.Text.RegularExpressions.Regex.Match(label, @"[""'`]([^""'`]+)[""'`]");
        if (quoted.Success) return quoted.Groups[1].Value;
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 ? words[^1] : label;
    }

    private static int ExtractDivisor(string label)
    {
        var m = System.Text.RegularExpressions.Regex.Match(label, @"\d+");
        return m.Success && int.TryParse(m.Value, out var d) ? d : 1;
    }

    private static int ExtractNumber(string label)
    {
        var m = System.Text.RegularExpressions.Regex.Match(label, @"-?\d+");
        return m.Success && int.TryParse(m.Value, out var n) ? n : 0;
    }

    private static string ExtractIdentifier(string label)
    {
        var m = System.Text.RegularExpressions.Regex.Match(label, @"[A-Za-z_][A-Za-z0-9_]*");
        return m.Success ? m.Value : "unknown";
    }

    // Extracts the array name from labels like "swap(arr[j], arr[j+1])" or "arr[j]".
    // Looks for an identifier immediately followed by '[', falling back to ExtractIdentifier.
    private static string ExtractArrayName(string label)
    {
        var m = System.Text.RegularExpressions.Regex.Match(label, @"([A-Za-z_][A-Za-z0-9_]*)\s*\[");
        return m.Success ? m.Groups[1].Value : ExtractIdentifier(label);
    }

    private static string Slugify(string label) =>
        System.Text.RegularExpressions.Regex.Replace(label.Trim().ToLowerInvariant(), @"\s+", "_");
}
