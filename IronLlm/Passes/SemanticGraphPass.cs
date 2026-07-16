using System.Diagnostics;
using System.Text.Json;
using IronLlm.Graph;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

public class SemanticGraphPass : ICompilerPass
{
    private readonly ILogger<SemanticGraphPass> _logger;

    public SemanticGraphPass(ILogger<SemanticGraphPass> logger)
    {
        _logger = logger;
    }

    public string Name          => "02-SemanticGraph";
    public string? ArtifactFile  => "02-semantic-graph.json";

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json    = await File.ReadAllTextAsync(artifactPath);
        var opts    = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var wrapper = JsonSerializer.Deserialize<SemanticGraphDto>(json, opts)
                      ?? throw new InvalidOperationException("Could not deserialize semantic graph");

        var graph = new SemanticGraph();
        if (wrapper.Nodes != null) graph.Nodes.AddRange(wrapper.Nodes);
        if (wrapper.Edges != null) graph.Edges.AddRange(wrapper.Edges);
        context.SemanticGraph = graph;
    }

    private sealed class SemanticGraphDto
    {
        public List<Node>? Nodes { get; set; }
        public List<Edge>? Edges { get; set; }
    }

    public Task ExecuteAsync(CompilationContext context)
    {
        var sw   = Stopwatch.StartNew();
        var spec = context.RawSpec ?? throw new InvalidOperationException("RawSpec not set");
        var graph = new SemanticGraph();

        var lines = spec.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        ProgramNode? program = null;
        string? pendingBranchCondition = null;
        int? pendingBranchDivisor = null;
        BranchNode? lastBranch = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("program:"))
            {
                var name = line["program:".Length..].Trim();
                program = new ProgramNode(Guid.NewGuid(), $"Program:{name}", name);
                graph.Add(program);
            }
            else if (line.StartsWith("branch:"))
            {
                pendingBranchCondition = null;
                pendingBranchDivisor = null;
                lastBranch = null;
            }
            else if (line.StartsWith("condition:"))
            {
                pendingBranchCondition = line["condition:".Length..].Trim();
            }
            else if (line.StartsWith("divisor:"))
            {
                pendingBranchDivisor = int.Parse(line["divisor:".Length..].Trim());
            }
            else if (line.StartsWith("true_output:"))
            {
                var output = line["true_output:".Length..].Trim().Trim('"');
                var condition = pendingBranchCondition ?? "default";

                lastBranch = new BranchNode(Guid.NewGuid(), $"Branch:{condition}", condition);
                graph.Add(lastBranch);

                if (pendingBranchDivisor.HasValue)
                {
                    var modNode = new ModuloNode(Guid.NewGuid(), $"Modulo:{pendingBranchDivisor}", pendingBranchDivisor.Value);
                    graph.Add(modNode);
                    graph.Connect(lastBranch.Id, modNode.Id, EdgeType.DependsOn);
                }

                var printNode = new PrintNode(Guid.NewGuid(), $"Print:{output}", output);
                graph.Add(printNode);
                graph.Connect(lastBranch.Id, printNode.Id, EdgeType.TrueBranch);

                if (program != null)
                    graph.Connect(program.Id, lastBranch.Id, EdgeType.Contains);
            }
        }

        BuildLoopNode(lines, graph, program);
        BuildVariableNode(lines, graph, program);

        // Warn about absent structural sections
        if (!graph.Nodes.OfType<LoopNode>().Any())
            _logger.LogWarning("No loop: section found in spec — graph may be incomplete");
        if (!graph.Nodes.OfType<VariableNode>().Any())
            _logger.LogWarning("No variable: section found in spec — graph may be incomplete");

        var kindSummary = graph.Nodes
            .GroupBy(n => n.GetType().Name)
            .Select(g => $"{g.Key}×{g.Count()}");
        _logger.LogDebug("Graph: {Nodes} nodes, {Edges} edges — {Kinds}",
            graph.Nodes.Count, graph.Edges.Count, string.Join(", ", kindSummary));

        context.SemanticGraph = graph;
        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
        return Task.CompletedTask;
    }

    private static void BuildLoopNode(string[] lines, SemanticGraph graph, ProgramNode? program)
    {
        // Handle multiple loop: sections (e.g. outer + nested loop).
        // When to: is not a literal integer, emit a NestedLoopNode with the expression as BoundExpr.
        bool inLoop = false;
        int? from = null;
        string? toRaw = null;
        string? varName = null;

        void FlushLoop()
        {
            if (from == null || toRaw == null) return;
            if (int.TryParse(toRaw, out var toInt))
            {
                var loopNode = new LoopNode(Guid.NewGuid(), $"Loop:{from}..{toInt}", from.Value, toInt);
                graph.Add(loopNode);
                if (program != null) graph.Connect(program.Id, loopNode.Id, EdgeType.Contains);
            }
            else
            {
                var v = varName ?? "i";
                var node = new NestedLoopNode(Guid.NewGuid(), $"NestedLoop:{v}<{toRaw}", v, from.Value, toRaw);
                graph.Add(node);
                if (program != null) graph.Connect(program.Id, node.Id, EdgeType.Contains);
            }
            from = null; toRaw = null;
        }

        foreach (var line in lines)
        {
            if (line == "loop:")
            {
                FlushLoop();
                inLoop = true;
                continue;
            }
            if (!inLoop) continue;

            if (line.StartsWith("from:"))      { from    = int.TryParse(line["from:".Length..].Trim(), out var f) ? f : 0; continue; }
            if (line.StartsWith("to:"))        { toRaw   = line["to:".Length..].Trim(); continue; }
            if (line.StartsWith("variable:"))  { inLoop  = false; FlushLoop(); continue; }
            if (line.StartsWith("branch:"))    { inLoop  = false; FlushLoop(); continue; }
        }
        FlushLoop();

        // Capture variable name for the outer loop label from the first variable: section.
        _ = varName; // suppress unused warning; variable used inside closure above
    }

    private static void BuildVariableNode(string[] lines, SemanticGraph graph, ProgramNode? program)
    {
        bool inVar = false;
        string? name = null, type = null;
        foreach (var line in lines)
        {
            if (line == "variable:") { inVar = true; name = null; type = null; continue; }
            if (!inVar) continue;
            if (line.StartsWith("name:")) { name = line["name:".Length..].Trim(); continue; }
            if (line.StartsWith("type:")) { type = line["type:".Length..].Trim(); continue; }

            // Flush on any non-variable keyword
            if ((line.StartsWith("branch:") || line.StartsWith("loop:") || line.StartsWith("program:")) && name != null && type != null)
            {
                EmitVariable(graph, program, name, type);
                inVar = false; name = null; type = null;
            }
        }
        if (name != null && type != null)
            EmitVariable(graph, program, name, type);
    }

    private static void EmitVariable(SemanticGraph graph, ProgramNode? program, string name, string type)
    {
        // int[N] → ArrayNode; plain type → VariableNode
        var arrayMatch = System.Text.RegularExpressions.Regex.Match(type, @"^int\[(\d+)\]$");
        if (arrayMatch.Success && int.TryParse(arrayMatch.Groups[1].Value, out var size))
        {
            var arr = new ArrayNode(Guid.NewGuid(), $"Array:{name}[{size}]", name, "int", size);
            graph.Add(arr);
            if (program != null) graph.Connect(program.Id, arr.Id, EdgeType.Contains);
        }
        else
        {
            var varNode = new VariableNode(Guid.NewGuid(), $"Var:{name}", name, type);
            graph.Add(varNode);
            if (program != null) graph.Connect(program.Id, varNode.Id, EdgeType.Contains);
        }
    }
}
