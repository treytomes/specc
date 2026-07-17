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

        // Inline stdin-variable state — parsed and connected in spec order so that
        // Contains edges on the ProgramNode reflect the authorial sequence.
        bool inInlineVar = false;
        string? inlineVarName = null, inlineVarType = null, inlineVarSource = null;

        void FlushInlineVar()
        {
            if (inlineVarName == null || inlineVarSource == null) return;
            if (inlineVarSource.Equals("stdin", StringComparison.OrdinalIgnoreCase))
            {
                var inp = new InputNode(Guid.NewGuid(), $"Input:{inlineVarName}", inlineVarName);
                graph.Add(inp);
                if (program != null) graph.Connect(program.Id, inp.Id, EdgeType.Contains);
            }
            inlineVarName = null; inlineVarType = null; inlineVarSource = null;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("program:"))
            {
                FlushInlineVar(); inInlineVar = false;
                var name = line["program:".Length..].Trim();
                program = new ProgramNode(Guid.NewGuid(), $"Program:{name}", name);
                graph.Add(program);
            }
            else if (line.StartsWith("branch:"))
            {
                FlushInlineVar(); inInlineVar = false;
                pendingBranchCondition = null;
                pendingBranchDivisor = null;
                lastBranch = null;
            }
            else if (line == "variable:")
            {
                FlushInlineVar();
                inInlineVar = true;
            }
            else if (inInlineVar && line.StartsWith("name:"))
            {
                inlineVarName = line["name:".Length..].Trim();
            }
            else if (inInlineVar && line.StartsWith("type:"))
            {
                inlineVarType = line["type:".Length..].Trim();
            }
            else if (inInlineVar && line.StartsWith("source:"))
            {
                inlineVarSource = line["source:".Length..].Trim();
            }
            else if (inInlineVar && (line.StartsWith("print:") || line.StartsWith("assign:") || line.StartsWith("loop:")))
            {
                FlushInlineVar(); inInlineVar = false;
                // fall through to handle the line below
            }
            else if (line.StartsWith("condition:"))
            {
                pendingBranchCondition = line["condition:".Length..].Trim();
            }
            else if (line.StartsWith("divisor:"))
            {
                if (int.TryParse(line["divisor:".Length..].Trim(), out var d) && d > 0)
                    pendingBranchDivisor = d;
                // divisor: 0 or non-integer — LLM placeholder for array programs; ignore.
            }

            if (line.StartsWith("print:") && !line.StartsWith("program:"))
            {
                var template = line["print:".Length..].Trim().Trim('"');
                var printNode = new PrintNode(Guid.NewGuid(), $"Print:{template}", template);
                graph.Add(printNode);
                if (program != null) graph.Connect(program.Id, printNode.Id, EdgeType.Contains);
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

        FlushInlineVar(); // flush trailing stdin variable if at end of spec

        BuildLoopNode(lines, graph, program);
        BuildVariableNode(lines, graph, program);
        BuildAssignNodes(lines, graph, program);

        // When the LLM couldn't express the program (ERROR: in spec) and graph has min_index
        // but no ArrayNode, try to recover the array from the original Markdown source.
        if (spec.Contains("\nERROR:") || spec.EndsWith("ERROR:", StringComparison.Ordinal))
        {
            var hasMinIndex = graph.Nodes.OfType<VariableNode>()
                .Any(v => v.Name.Equals("min_index", StringComparison.OrdinalIgnoreCase));
            var hasArray = graph.Nodes.OfType<ArrayNode>().Any();
            if (hasMinIndex && !hasArray && context.InputPath?.EndsWith(".md", StringComparison.OrdinalIgnoreCase) == true)
            {
                var mdText = File.ReadAllText(context.InputPath);
                var recovered = TryExtractArrayFromMarkdown(mdText);
                if (recovered != null)
                {
                    var arr = new ArrayNode(Guid.NewGuid(), $"Array:arr[{recovered.Length}]", "arr", "int", recovered.Length, recovered);
                    graph.Add(arr);
                    if (program != null) graph.Connect(program.Id, arr.Id, EdgeType.Contains);
                    _logger.LogInformation("Recovered {Count}-element array from Markdown (LLM spec was incomplete)", recovered.Length);
                }
            }
        }

        // Warn about absent structural sections — but linear programs (direct print: or stdin input)
        // legitimately have no loop.
        var hasDirectPrint = program != null && graph.Edges
            .Any(e => e.From == program.Id && e.Type == EdgeType.Contains
                      && graph.Nodes.OfType<PrintNode>().Any(p => p.Id == e.To));
        var hasInput = graph.Nodes.OfType<InputNode>().Any();
        if (!graph.Nodes.OfType<LoopNode>().Any() && !hasDirectPrint && !hasInput)
            _logger.LogWarning("No loop: section found in spec — graph may be incomplete");
        if (!graph.Nodes.OfType<VariableNode>().Any() && !graph.Nodes.OfType<InputNode>().Any())
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

    private static void BuildAssignNodes(string[] lines, SemanticGraph graph, ProgramNode? program)
    {
        bool inAssign = false;
        string? target = null, op = null, left = null, right = null;

        void Flush()
        {
            if (target == null || op == null || left == null) return;
            // copy requires only left; all others require right too
            if (op != "copy" && right == null) return;
            var label = op == "copy"
                ? $"Assign:{target}=copy({left})"
                : $"Assign:{target}={op}({left},{right})";
            var assign = new AssignNode(Guid.NewGuid(), label, target, op, left, right);
            graph.Add(assign);
            if (program != null) graph.Connect(program.Id, assign.Id, EdgeType.Contains);
            target = null; op = null; left = null; right = null;
        }

        foreach (var line in lines)
        {
            if (line == "assign:") { Flush(); inAssign = true; continue; }
            if (!inAssign) continue;

            if (line.StartsWith("target:")) { target = line["target:".Length..].Trim(); continue; }
            if (line.StartsWith("op:"))     { op     = line["op:".Length..].Trim();     continue; }
            if (line.StartsWith("left:"))   { left   = line["left:".Length..].Trim();   continue; }
            if (line.StartsWith("right:"))  { right  = line["right:".Length..].Trim();  continue; }

            if (line.StartsWith("branch:") || line.StartsWith("loop:") || line.StartsWith("variable:") || line.StartsWith("program:"))
            {
                Flush(); inAssign = false;
            }
        }
        Flush();
    }

    private static int[]? TryExtractArrayFromMarkdown(string markdown)
    {
        // Heuristic: find a line with 3+ space-separated integers, e.g. "Start with: 64 25 12 22 11 90 3 45"
        foreach (var line in markdown.Split('\n'))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var nums = tokens.Select(t => int.TryParse(t, out var v) ? (int?)v : null)
                             .Where(v => v.HasValue)
                             .Select(v => v!.Value)
                             .ToArray();
            if (nums.Length >= 3)
                return nums;
        }
        return null;
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
        string? name = null, type = null, initialValue = null, source = null;

        void FlushVar()
        {
            if (name == null || type == null) return;
            // stdin variables are handled inline in the main loop to preserve spec order.
            if (source?.Equals("stdin", StringComparison.OrdinalIgnoreCase) != true)
                EmitVariable(graph, program, name, type, initialValue);
            name = null; type = null; initialValue = null; source = null;
        }

        foreach (var line in lines)
        {
            if (line == "variable:") { FlushVar(); inVar = true; continue; }
            if (!inVar) continue;
            if (line.StartsWith("name:"))          { name         = line["name:".Length..].Trim();          continue; }
            if (line.StartsWith("type:"))          { type         = line["type:".Length..].Trim();          continue; }
            if (line.StartsWith("initial_value:")) { initialValue = line["initial_value:".Length..].Trim(); continue; }
            if (line.StartsWith("source:"))        { source       = line["source:".Length..].Trim();        continue; }

            // Flush on any non-variable keyword
            if (line.StartsWith("branch:") || line.StartsWith("loop:") || line.StartsWith("program:") || line.StartsWith("assign:"))
            {
                FlushVar(); inVar = false;
            }
        }
        FlushVar();
    }

    private static void EmitVariable(
        SemanticGraph graph, ProgramNode? program, string name, string type, string? initialValue = null)
    {
        // array[int] or int[N] → ArrayNode; plain type → VariableNode
        var arrayMatch = System.Text.RegularExpressions.Regex.Match(type, @"^(?:array\[int\]|int\[(\d+)\])$");

        // Resolve size: from explicit int[N] type, or from the initial_value element count.
        int size = 0;
        if (arrayMatch.Groups[1].Success)
            int.TryParse(arrayMatch.Groups[1].Value, out size);

        int[]? values = null;
        if (initialValue != null)
        {
            var nums = initialValue.Trim('[', ']')
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(t => int.TryParse(t, out var v) ? v : 0)
                .ToArray();
            if (nums.Length > 0)
            {
                values = nums;
                if (size == 0) size = nums.Length;
            }
        }

        if (arrayMatch.Success && size > 0)
        {
            var arr = new ArrayNode(Guid.NewGuid(), $"Array:{name}[{size}]", name, "int", size, values);
            graph.Add(arr);
            if (program != null) graph.Connect(program.Id, arr.Id, EdgeType.Contains);
        }
        else
        {
            int? scalarInit = null;
            if (initialValue != null && int.TryParse(initialValue.Trim(), out var sv))
                scalarInit = sv;
            var varNode = new VariableNode(Guid.NewGuid(), $"Var:{name}", name, type, scalarInit);
            graph.Add(varNode);
            if (program != null) graph.Connect(program.Id, varNode.Id, EdgeType.Contains);
        }
    }
}
