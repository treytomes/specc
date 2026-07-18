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
        string? pendingBranchCompare = null;
        int? pendingBranchValue = null;
        string? pendingBranchCompareWith = null;
        BranchNode? lastBranch = null;

        // while: loop state
        WhileLoopNode? whileNode = null;
        string? whileVariable = null, whileCondition = null, whileRhsVar = null;
        int? whileValue = null;
        bool inWhile = false;

        void FlushWhile()
        {
            if (whileVariable == null || whileCondition == null) return;
            if (!whileValue.HasValue && whileRhsVar == null) return;
            whileNode = whileRhsVar != null
                ? new WhileLoopNode(
                    Guid.NewGuid(),
                    $"WhileLoop:{whileVariable}{whileCondition}{whileRhsVar}",
                    whileVariable, whileCondition, 0, whileRhsVar)
                : new WhileLoopNode(
                    Guid.NewGuid(),
                    $"WhileLoop:{whileVariable}{whileCondition}{whileValue}",
                    whileVariable, whileCondition, whileValue!.Value);
            graph.Add(whileNode);
            if (program != null) graph.Connect(program.Id, whileNode.Id, EdgeType.Contains);
            whileVariable = null; whileCondition = null; whileValue = null; whileRhsVar = null;
        }

        // random: state
        bool inRandom = false;
        string? randomName = null;
        int? randomMin = null, randomMax = null;

        void FlushRandom()
        {
            if (randomName == null || !randomMin.HasValue || !randomMax.HasValue) return;
            var rn = new RandomNode(Guid.NewGuid(), $"Random:{randomName}", randomName, randomMin.Value, randomMax.Value);
            graph.Add(rn);
            if (program != null) graph.Connect(program.Id, rn.Id, EdgeType.Contains);
            randomName = null; randomMin = null; randomMax = null;
        }

        // true_assign: state — assign body for a branch condition (sequentially executed)
        bool inTrueAssign = false;
        string? trueAssignTarget = null, trueAssignOp = null, trueAssignLeft = null, trueAssignRight = null;

        void FlushTrueAssign()
        {
            if (trueAssignTarget == null || trueAssignOp == null || trueAssignLeft == null) return;
            if (trueAssignOp != "copy" && trueAssignRight == null) return;
            var label = trueAssignOp == "copy"
                ? $"Assign:{trueAssignTarget}=copy({trueAssignLeft})"
                : $"Assign:{trueAssignTarget}={trueAssignOp}({trueAssignLeft},{trueAssignRight})";
            var assign = new AssignNode(Guid.NewGuid(), label, trueAssignTarget, trueAssignOp, trueAssignLeft, trueAssignRight);
            graph.Add(assign);
            // Connect to the enclosing branch as a sequential body step.
            if (lastBranch != null)
                graph.Connect(lastBranch.Id, assign.Id, EdgeType.Contains);
            trueAssignTarget = null; trueAssignOp = null; trueAssignLeft = null; trueAssignRight = null;
        }

        // Inline stdin-variable state — parsed and connected in spec order so that
        // Contains edges on the ProgramNode reflect the authorial sequence.
        bool inInlineVar = false;
        string? inlineVarName = null, inlineVarType = null, inlineVarSource = null;

        // For linear (no-loop) programs, all Contains-edge nodes are deferred until after
        // the main loop so that declaration order is preserved across all node types.
        // PrintNodes are added immediately to the graph (their IDs are needed for TrueBranch
        // edges) but their Contains edges are deferred.
        var linearContainsOrder = new List<Node>();
        bool hasLoop = false; // set after BuildLoopNode

        void FlushInlineVar()
        {
            if (inlineVarName == null || inlineVarSource == null) return;
            if (inlineVarSource.Equals("stdin", StringComparison.OrdinalIgnoreCase))
            {
                var varType = inlineVarType ?? "string";
                var inp = new InputNode(Guid.NewGuid(), $"Input:{inlineVarName}", inlineVarName, varType);
                graph.Add(inp);
                if (whileNode != null)
                    // Inside a while body: connect directly so LowerInteractiveWhileLoop finds it.
                    graph.Connect(whileNode.Id, inp.Id, EdgeType.Contains);
                else
                    // Defer Contains edge; emitted in order after the loop for linear programs.
                    linearContainsOrder.Add(inp);
            }
            inlineVarName = null; inlineVarType = null; inlineVarSource = null;
        }

        // Inline assign state — collects assigns in spec order; flushed after the main loop.
        bool inInlineAssign = false;
        string? assignTarget = null, assignOp = null, assignLeft = null, assignRight = null;

        void FlushInlineAssign()
        {
            if (assignTarget == null || assignOp == null || assignLeft == null) return;
            if (assignOp != "copy" && assignRight == null) return;
            var label = assignOp == "copy"
                ? $"Assign:{assignTarget}=copy({assignLeft})"
                : $"Assign:{assignTarget}={assignOp}({assignLeft},{assignRight})";
            var assign = new AssignNode(Guid.NewGuid(), label, assignTarget, assignOp, assignLeft, assignRight);
            graph.Add(assign);
            linearContainsOrder.Add(assign);
            assignTarget = null; assignOp = null; assignLeft = null; assignRight = null;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("program:"))
            {
                FlushInlineVar(); inInlineVar = false;
                FlushInlineAssign(); inInlineAssign = false;
                FlushTrueAssign(); inTrueAssign = false;
                FlushRandom(); inRandom = false;
                var name = line["program:".Length..].Trim();
                program = new ProgramNode(Guid.NewGuid(), $"Program:{name}", name);
                graph.Add(program);
            }
            else if (line == "random:")
            {
                FlushInlineVar(); inInlineVar = false;
                FlushInlineAssign(); inInlineAssign = false;
                FlushTrueAssign(); inTrueAssign = false;
                FlushRandom();
                inRandom = true;
            }
            else if (inRandom && line.StartsWith("name:"))
            {
                randomName = line["name:".Length..].Trim();
            }
            else if (inRandom && line.StartsWith("min:"))
            {
                if (int.TryParse(line["min:".Length..].Trim(), out var rv)) randomMin = rv;
            }
            else if (inRandom && line.StartsWith("max:"))
            {
                if (int.TryParse(line["max:".Length..].Trim(), out var rv)) randomMax = rv;
            }
            else if (line == "while:")
            {
                FlushInlineVar(); inInlineVar = false;
                FlushInlineAssign(); inInlineAssign = false;
                FlushTrueAssign(); inTrueAssign = false;
                FlushRandom(); inRandom = false;
                inWhile = true;
            }
            else if (line.StartsWith("branch:"))
            {
                FlushInlineVar(); inInlineVar = false;
                FlushInlineAssign(); inInlineAssign = false;
                FlushTrueAssign(); inTrueAssign = false;
                FlushRandom(); inRandom = false;
                if (inWhile) { FlushWhile(); inWhile = false; }
                pendingBranchCondition = null;
                pendingBranchDivisor = null;
                pendingBranchCompare = null;
                pendingBranchValue = null;
                pendingBranchCompareWith = null;
                lastBranch = null;
            }
            else if (line == "variable:")
            {
                FlushInlineVar();
                FlushInlineAssign(); inInlineAssign = false;
                if (inWhile) { FlushWhile(); inWhile = false; }
                inInlineVar = true;
            }
            else if (line == "assign:")
            {
                FlushInlineVar(); inInlineVar = false;
                FlushInlineAssign();
                inInlineAssign = true;
            }
            else if (line == "true_assign:")
            {
                FlushTrueAssign();
                inTrueAssign = true;
                // If no condition: was seen before true_assign: (malformed LLM extraction),
                // create a default branch node now so FlushTrueAssign has a parent to connect to.
                if (lastBranch == null)
                {
                    var condition = pendingBranchCondition ?? "default";
                    lastBranch = new BranchNode(Guid.NewGuid(), $"Branch:{condition}", condition);
                    graph.Add(lastBranch);
                    var branchContainer = whileNode as Node ?? program;
                    if (branchContainer != null)
                        graph.Connect(branchContainer.Id, lastBranch.Id, EdgeType.Contains);
                }
            }
            else if (inWhile && line.StartsWith("variable:") && line["variable:".Length..].Trim().Length > 0)
            {
                // Old-style while: variable: n (name on same line)
                whileVariable = line["variable:".Length..].Trim();
            }
            else if (inWhile && line.StartsWith("compare_lhs:"))
            {
                // compare_lhs: {varName} — strip braces
                whileVariable = line["compare_lhs:".Length..].Trim().Trim('{', '}');
            }
            else if (inWhile && line.StartsWith("condition:"))
            {
                whileCondition = line["condition:".Length..].Trim();
            }
            else if (inWhile && line.StartsWith("compare:"))
            {
                whileCondition = line["compare:".Length..].Trim();
            }
            else if (inWhile && line.StartsWith("value:"))
            {
                if (int.TryParse(line["value:".Length..].Trim(), out var wv))
                    whileValue = wv;
            }
            else if (inWhile && line.StartsWith("compare_rhs:"))
            {
                var raw = line["compare_rhs:".Length..].Trim();
                if (int.TryParse(raw, out var wv))
                    whileValue = wv;
                else
                    whileRhsVar = raw.Trim('{', '}');
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
            else if (inInlineVar && (line.StartsWith("print:") || line.StartsWith("loop:")))
            {
                FlushInlineVar(); inInlineVar = false;
                // fall through to handle the line below
            }
            else if (inInlineAssign && line.StartsWith("target:"))
            {
                assignTarget = line["target:".Length..].Trim();
            }
            else if (inInlineAssign && line.StartsWith("op:"))
            {
                assignOp = line["op:".Length..].Trim();
            }
            else if (inInlineAssign && line.StartsWith("left:"))
            {
                assignLeft = line["left:".Length..].Trim();
            }
            else if (inInlineAssign && line.StartsWith("right:"))
            {
                assignRight = line["right:".Length..].Trim();
            }
            else if (inInlineAssign && (line.StartsWith("print:") || line.StartsWith("loop:")))
            {
                FlushInlineAssign(); inInlineAssign = false;
                // fall through to handle the line below
            }
            else if (inTrueAssign && line.StartsWith("target:"))
            {
                trueAssignTarget = line["target:".Length..].Trim();
            }
            else if (inTrueAssign && line.StartsWith("op:"))
            {
                trueAssignOp = line["op:".Length..].Trim();
            }
            else if (inTrueAssign && line.StartsWith("left:"))
            {
                trueAssignLeft = line["left:".Length..].Trim();
            }
            else if (inTrueAssign && line.StartsWith("right:"))
            {
                trueAssignRight = line["right:".Length..].Trim();
            }
            else if (line.StartsWith("condition:"))
            {
                pendingBranchCondition = line["condition:".Length..].Trim();
                // Eagerly create the BranchNode so true_assign: can connect to it even
                // when there is no true_output: (assign-body branches in while loops).
                lastBranch = new BranchNode(Guid.NewGuid(), $"Branch:{pendingBranchCondition}", pendingBranchCondition);
                graph.Add(lastBranch);
                var branchContainer = whileNode as Node ?? program;
                if (branchContainer != null)
                    graph.Connect(branchContainer.Id, lastBranch.Id, EdgeType.Contains);
            }
            else if (line.StartsWith("divisor:"))
            {
                if (int.TryParse(line["divisor:".Length..].Trim(), out var d) && d > 0)
                {
                    pendingBranchDivisor = d;
                    // Eagerly wire the ModuloNode when lastBranch already exists (true_assign:
                    // branches have no true_output: to trigger the deferred path).
                    if (lastBranch != null)
                    {
                        var modNode = new ModuloNode(Guid.NewGuid(), $"Modulo:{d}", d);
                        graph.Add(modNode);
                        graph.Connect(lastBranch.Id, modNode.Id, EdgeType.DependsOn);
                        pendingBranchDivisor = null; // consumed; don't emit again in true_output:
                    }
                }
                // divisor: 0 or non-integer — LLM placeholder for array programs; ignore.
            }
            else if (line.StartsWith("compare:"))
            {
                pendingBranchCompare = line["compare:".Length..].Trim();
            }
            else if (line.StartsWith("value:"))
            {
                if (int.TryParse(line["value:".Length..].Trim(), out var v))
                    pendingBranchValue = v;
            }
            else if (line.StartsWith("compare_with:"))
            {
                // compare_with: {varName} — variable rhs for a branch comparison
                pendingBranchCompareWith = line["compare_with:".Length..].Trim().Trim('{', '}');
            }

            if (line.StartsWith("print:") && !line.StartsWith("program:"))
            {
                FlushInlineAssign(); inInlineAssign = false;
                if (inWhile) { FlushWhile(); inWhile = false; }
                var template = line["print:".Length..].Trim().Trim('"');
                var printNode = new PrintNode(Guid.NewGuid(), $"Print:{template}", template);
                graph.Add(printNode);
                if (whileNode != null)
                    // Inside a while body: connect to the WhileLoopNode.
                    graph.Connect(whileNode.Id, printNode.Id, EdgeType.Contains);
                else
                    // Defer Contains edge for linear programs.
                    linearContainsOrder.Add(printNode);
            }
            else if (line.StartsWith("true_output:"))
            {
                var output = line["true_output:".Length..].Trim().Trim('"');
                var condition = pendingBranchCondition ?? "default";

                // BranchNode may already exist (created eagerly in condition: handler).
                if (lastBranch == null)
                {
                    lastBranch = new BranchNode(Guid.NewGuid(), $"Branch:{condition}", condition);
                    graph.Add(lastBranch);
                    var branchContainer = whileNode as Node ?? program;
                    if (branchContainer != null)
                        graph.Connect(branchContainer.Id, lastBranch.Id, EdgeType.Contains);
                }

                if (pendingBranchCompare != null && pendingBranchCompareWith != null)
                {
                    // Variable-rhs comparison: emit ComparisonNode with RhsVar set.
                    var cmpNode = new ComparisonNode(
                        Guid.NewGuid(),
                        $"Comparison:{pendingBranchCompare}:{pendingBranchCompareWith}",
                        pendingBranchCompare,
                        0,
                        pendingBranchCompareWith);
                    graph.Add(cmpNode);
                    graph.Connect(lastBranch.Id, cmpNode.Id, EdgeType.DependsOn);
                    pendingBranchCompareWith = null;
                }
                else if (pendingBranchCompare != null && pendingBranchValue.HasValue)
                {
                    // Comparison-based branch: emit ComparisonNode with op and value.
                    var cmpNode = new ComparisonNode(
                        Guid.NewGuid(),
                        $"Comparison:{pendingBranchCompare}:{pendingBranchValue}",
                        pendingBranchCompare,
                        pendingBranchValue.Value);
                    graph.Add(cmpNode);
                    graph.Connect(lastBranch.Id, cmpNode.Id, EdgeType.DependsOn);
                }
                else if (pendingBranchDivisor.HasValue)
                {
                    var modNode = new ModuloNode(Guid.NewGuid(), $"Modulo:{pendingBranchDivisor}", pendingBranchDivisor.Value);
                    graph.Add(modNode);
                    graph.Connect(lastBranch.Id, modNode.Id, EdgeType.DependsOn);
                }

                var printNode = new PrintNode(Guid.NewGuid(), $"Print:{output}", output);
                graph.Add(printNode);
                graph.Connect(lastBranch.Id, printNode.Id, EdgeType.TrueBranch);
            }
        }

        FlushInlineVar();    // flush trailing stdin variable if at end of spec
        FlushInlineAssign(); // flush trailing assign if at end of spec
        FlushTrueAssign();   // flush trailing true_assign if at end of spec
        FlushRandom();       // flush trailing random block if at end of spec
        if (inWhile) FlushWhile();

        BuildLoopNode(lines, graph, program);
        BuildVariableNode(lines, graph, program);

        hasLoop = graph.Nodes.OfType<LoopNode>().Any();

        // If the spec placed a print: before the while: header (LLM ordering error), the PrintNode
        // landed in linearContainsOrder as a program-level child. Relocate it to the WhileLoopNode
        // when the while body has branches but no print of its own — it belongs in the loop body.
        if (whileNode != null)
        {
            var whileHasPrint = graph.Edges
                .Any(e => e.From == whileNode.Id && e.Type == EdgeType.Contains
                       && graph.Nodes.FirstOrDefault(n => n.Id == e.To) is PrintNode);
            if (!whileHasPrint)
            {
                var toRelocate = linearContainsOrder.OfType<PrintNode>().ToList();
                foreach (var p in toRelocate)
                {
                    linearContainsOrder.Remove(p);
                    graph.Connect(whileNode.Id, p.Id, EdgeType.Contains);
                }
            }
        }

        // Emit Contains edges in declaration order (works for both loop and linear programs).
        foreach (var node in linearContainsOrder)
            if (program != null) graph.Connect(program.Id, node.Id, EdgeType.Contains);

        if (hasLoop && !graph.Nodes.OfType<AssignNode>().Any())
        {
            // Loop programs where assigns weren't captured inline (e.g. loop spec came before
            // assign in spec order): fall back to BuildAssignNodes.
            BuildAssignNodes(lines, graph, program);
        }

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
        var hasInput  = graph.Nodes.OfType<InputNode>().Any();
        var hasRandom = graph.Nodes.OfType<RandomNode>().Any();
        if (!graph.Nodes.OfType<LoopNode>().Any() && !hasDirectPrint && !hasInput && !hasRandom)
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
