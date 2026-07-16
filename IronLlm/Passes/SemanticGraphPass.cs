using System.Text.Json;
using IronLlm.Graph;

namespace IronLlm.Passes;

// Deterministic parser: reads the .spec file and builds the semantic graph.
// No LLM involved — this is pure structural extraction.
public class SemanticGraphPass : ICompilerPass
{
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
        var spec = context.RawSpec ?? throw new InvalidOperationException("RawSpec not set");
        var graph = new SemanticGraph();

        var lines = spec.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        ProgramNode? program = null;
        LoopNode? loop = null;
        string? pendingBranchCondition = null;
        int? pendingBranchDivisor = null;

        // Track last branch so we can attach its print node
        BranchNode? lastBranch = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("program:"))
            {
                var name = line["program:".Length..].Trim();
                program = new ProgramNode(Guid.NewGuid(), $"Program:{name}", name);
                graph.Add(program);
            }
            else if (line == "loop:")
            {
                // will be filled by from:/to: lines
            }
            else if (line.StartsWith("from:") && loop == null)
            {
                // handled together with to:
            }
            else if (line.StartsWith("to:") && loop == null)
            {
                // scan back for from — simpler: just parse them in a two-pass
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
            else if (line.StartsWith("variable:"))
            {
                // handled by name:/type: lines
            }
            else if (line.StartsWith("name:"))
            {
                // stash for variable
            }
            else if (line.StartsWith("type:"))
            {
                // stash for variable
            }
        }

        // Two-pass for loop and variable (simpler than inline state machine)
        BuildLoopNode(lines, graph, program);
        BuildVariableNode(lines, graph, program);

        context.SemanticGraph = graph;
        return Task.CompletedTask;
    }

    private static void BuildLoopNode(string[] lines, SemanticGraph graph, ProgramNode? program)
    {
        bool inLoop = false;
        int? from = null, to = null;
        foreach (var line in lines)
        {
            if (line == "loop:") { inLoop = true; continue; }
            if (inLoop && line.StartsWith("from:")) from = int.Parse(line["from:".Length..].Trim());
            if (inLoop && line.StartsWith("to:")) to = int.Parse(line["to:".Length..].Trim());
            if (inLoop && line.StartsWith("branch:")) break;
        }
        if (from.HasValue && to.HasValue)
        {
            var loopNode = new LoopNode(Guid.NewGuid(), $"Loop:{from}..{to}", from.Value, to.Value);
            graph.Add(loopNode);
            if (program != null)
                graph.Connect(program.Id, loopNode.Id, EdgeType.Contains);
        }
    }

    private static void BuildVariableNode(string[] lines, SemanticGraph graph, ProgramNode? program)
    {
        bool inVar = false;
        string? name = null, type = null;
        foreach (var line in lines)
        {
            if (line == "variable:") { inVar = true; continue; }
            if (inVar && line.StartsWith("name:")) name = line["name:".Length..].Trim();
            if (inVar && line.StartsWith("type:")) type = line["type:".Length..].Trim();
        }
        if (name != null && type != null)
        {
            var varNode = new VariableNode(Guid.NewGuid(), $"Var:{name}", name, type);
            graph.Add(varNode);
            if (program != null)
                graph.Connect(program.Id, varNode.Id, EdgeType.Contains);
        }
    }
}
