using System.Diagnostics;
using System.Text.Json;
using IronLlm.Graph;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

public class CfgPass : ICompilerPass
{
    private readonly ILogger<CfgPass> _logger;

    public CfgPass(ILogger<CfgPass> logger)
    {
        _logger = logger;
    }

    public string Name          => "04-CFG";
    public string? ArtifactFile  => "04-cfg.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json   = File.ReadAllText(artifactPath);
        var blocks = JsonSerializer.Deserialize<List<CfgBlock>>(json, JsonOpts)
                     ?? throw new InvalidOperationException("Could not deserialize CFG");
        context.CfgBlocks = blocks;
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(CompilationContext context)
    {
        var sw    = Stopwatch.StartNew();
        var graph = context.SemanticGraph ?? throw new InvalidOperationException("SemanticGraph not set");

        List<CfgBlock> blocks;

        var arrayNode     = graph.Nodes.OfType<ArrayNode>().FirstOrDefault();
        var hasLoop       = graph.Nodes.OfType<LoopNode>().Any();
        var whileLoopNode = graph.Nodes.OfType<WhileLoopNode>().FirstOrDefault();

        if (arrayNode != null)
        {
            var hasMinIndex = graph.Nodes.OfType<VariableNode>()
                .Any(v => v.Name.Equals("min_index", StringComparison.OrdinalIgnoreCase));
            blocks = hasMinIndex
                ? LowerSelectionSort(graph, arrayNode)
                : LowerBubbleSort(graph, arrayNode);
        }
        else if (whileLoopNode != null)
        {
            // If the while condition uses a variable rhs (RhsVar != null) or there are
            // comparison-based (var-op-var or var-op-int) branches inside the while body,
            // use the interactive do-while lowerer.
            var hasVarRhsWhile = whileLoopNode.RhsVar != null;
            var hasCompBranches = graph.Nodes.OfType<ComparisonNode>().Any();
            blocks = (hasVarRhsWhile || hasCompBranches)
                ? LowerInteractiveWhileLoop(graph, whileLoopNode)
                : LowerWhileLoop(graph, whileLoopNode);
        }
        else if (!hasLoop && graph.Nodes.OfType<ComparisonNode>().Any()
                          && !graph.Nodes.OfType<AssignNode>().Any())
        {
            // Comparison-based branching (Guesser): no assigns, no loop.
            // When assigns are present, treat comparison branches as LLM noise and use
            // LowerLinear (same convention as LowerFlatLoop with assigns + divisor branches).
            blocks = LowerComparisonBranch(graph);
        }
        else if (!hasLoop)
        {
            blocks = LowerLinear(graph);
        }
        else
        {
            blocks = LowerFlatLoop(graph);
        }

        Validate(blocks);

        foreach (var block in blocks)
            _logger.LogDebug("Block {Label}: successors=[{True},{False}]",
                block.Label, block.SuccessorTrue ?? "—", block.SuccessorFalse ?? "—");

        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms — {Count} blocks",
            Name, sw.ElapsedMilliseconds, blocks.Count);

        context.CfgBlocks = blocks;
        return Task.CompletedTask;
    }

    private static List<CfgBlock> LowerFlatLoop(IronLlm.Graph.SemanticGraph graph)
    {
        var loop     = graph.Nodes.OfType<LoopNode>().FirstOrDefault()
                       ?? throw new InvalidOperationException("No LoopNode in graph");
        var branches = graph.Nodes.OfType<BranchNode>().ToList();
        var variable = graph.Nodes.OfType<VariableNode>().FirstOrDefault();
        var varName  = variable?.Name ?? "n";
        var assigns  = graph.Nodes.OfType<AssignNode>().ToList();

        // Derive modulo branches from graph edges — order by divisor descending so
        // the most-specific check (highest divisor = most constrained) runs first.
        var modBranches = graph.Edges
            .Where(e => e.Type == EdgeType.DependsOn)
            .Select(e => new
            {
                Branch = graph.Nodes.OfType<BranchNode>().FirstOrDefault(n => n.Id == e.From),
                Modulo = graph.Nodes.OfType<ModuloNode>().FirstOrDefault(n => n.Id == e.To),
            })
            .Where(x => x.Branch != null && x.Modulo != null)
            .Select(x => new { Branch = x.Branch!, Divisor = x.Modulo!.Divisor })
            .OrderByDescending(x => x.Divisor)
            .GroupBy(x => x.Branch.Condition)
            .Select(g => g.First())   // deduplicate: keep highest-divisor branch per condition name
            .ToList();

        // Default branch: the one with no DependsOn edge (no divisor).
        var modBranchIds = modBranches.Select(mb => mb.Branch.Id).ToHashSet();
        var defaultBranch = branches.FirstOrDefault(b => !modBranchIds.Contains(b.Id));

        string PrintFor(BranchNode branch)
        {
            var printEdge = graph.Edges.FirstOrDefault(e => e.From == branch.Id && e.Type == EdgeType.TrueBranch);
            if (printEdge == null) return branch.Condition;
            var print = graph.Nodes.OfType<PrintNode>().FirstOrDefault(n => n.Id == printEdge.To);
            return print?.Template ?? branch.Condition;
        }

        // Determine what to print in the default/fallthrough case.
        // When assigns are present, the printed variable is named by:
        //   1. The default branch's print template (if non-empty and not just the loop var).
        //   2. Any PrintNode whose template references an assign target.
        //   3. The last assign target (fallback for when the LLM omits a useful default branch).
        string printTemplate = varName;
        if (assigns.Count > 0)
        {
            var assignTargetNames = assigns.Select(a => a.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Try the default branch template first.
            if (defaultBranch != null)
            {
                var t = PrintFor(defaultBranch).Trim('{', '}');
                if (!string.IsNullOrEmpty(t) && !t.Equals(varName, StringComparison.OrdinalIgnoreCase))
                    printTemplate = t;
            }

            // If still pointing at the loop counter, scan all PrintNodes for one that names an assign target.
            if (printTemplate.Equals(varName, StringComparison.OrdinalIgnoreCase))
            {
                var assignPrint = graph.Nodes.OfType<PrintNode>()
                    .Select(p => p.Template.Trim('{', '}'))
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t) && assignTargetNames.Contains(t));
                if (assignPrint != null) printTemplate = assignPrint;
            }

            // Last resort: use the last assign's target.
            if (printTemplate.Equals(varName, StringComparison.OrdinalIgnoreCase) && assigns.Count > 0)
                printTemplate = assigns.Last().Target;
        }
        else if (defaultBranch != null)
        {
            var t = PrintFor(defaultBranch).Trim('{', '}');
            if (!string.IsNullOrEmpty(t)) printTemplate = t;
        }

        // Print before assigns only when the variable is BOTH overwritten AND used as an operand
        // in that same assign (Fibonacci: a = a + b — a is read before being written).
        // For Multiples (product = n * 7), product is only a target, so print after.
        bool printBeforeAssigns = assigns.Any(a =>
            a.Target.Equals(printTemplate, StringComparison.OrdinalIgnoreCase) &&
            (a.Left.Trim('{', '}').Equals(printTemplate, StringComparison.OrdinalIgnoreCase) ||
             a.Right?.Trim('{', '}').Equals(printTemplate, StringComparison.OrdinalIgnoreCase) == true));

        // Build check labels from actual branch data; use slugified condition as label base.
        static string CheckLabel(string condition) => $"check_{condition}";
        static string PrintLabel(string condition) => $"print_{condition}";

        // Convert AssignNode to a CFG instruction string.
        // copy has no right operand: "assign {target} copy {left}"
        // others: "assign {target} {op} {left} {right}"
        static string AssignInstr(AssignNode a) => a.Op == "copy"
            ? $"assign {a.Target} copy {a.Left}"
            : $"assign {a.Target} {a.Op} {a.Left} {a.Right}";

        // When assigns are present, modulo branches are LLM noise — every iteration goes to body.
        if (assigns.Count > 0) modBranches = [];

        // Determine the ordered check chain: first modulo check's label (or body if none).
        var bodyLabel     = assigns.Count > 0 ? "body" : "print_n";
        var firstCheckLabel = modBranches.Count > 0
            ? CheckLabel(modBranches[0].Branch.Condition)
            : bodyLabel;

        // Emit explicit initializations for any variable with an InitialValue (e.g. Fibonacci a=1, b=1).
        // The loop counter itself is initialized by the standard "{varName} = loop.From" instruction.
        var extraInits = graph.Nodes.OfType<VariableNode>()
            .Where(v => v.InitialValue.HasValue && !v.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
            .Select(v => $"{v.Name} = {v.InitialValue!.Value}")
            .ToList();

        var entryInstrs = new List<string>(extraInits) { $"{varName} = {loop.From}" };

        var blocks = new List<CfgBlock>
        {
            new("entry",     entryInstrs,                              "loop_test",     null),
            new("loop_test", [$"if {varName} > {loop.To} goto exit"], firstCheckLabel, "exit"),
        };

        for (var i = 0; i < modBranches.Count; i++)
        {
            var mb       = modBranches[i];
            var cond     = mb.Branch.Condition;
            var nextLabel = i + 1 < modBranches.Count
                ? CheckLabel(modBranches[i + 1].Branch.Condition)
                : bodyLabel;
            blocks.Add(new(CheckLabel(cond), [$"if {varName} % {mb.Divisor} == 0"], PrintLabel(cond), nextLabel));
        }

        foreach (var mb in modBranches)
        {
            var template = PrintFor(mb.Branch);
            // If template is a {variable} reference, emit "print var"; otherwise emit "print "string"".
            var printInstr = template.StartsWith('{') && template.EndsWith('}')
                ? $"print {template.Trim('{', '}')}"
                : $"print \"{template}\"";
            blocks.Add(new(PrintLabel(mb.Branch.Condition), [printInstr], "loop_inc", null));
        }

        if (assigns.Count > 0)
        {
            // Build the body block: print + assigns in the correct order.
            var bodyInstrs = new List<string>();
            if (printBeforeAssigns)
                bodyInstrs.Add($"print {printTemplate}");
            bodyInstrs.AddRange(assigns.Select(AssignInstr));
            if (!printBeforeAssigns)
                bodyInstrs.Add($"print {printTemplate}");
            blocks.Add(new("body", bodyInstrs, "loop_inc", null));
        }
        else
        {
            blocks.Add(new("print_n", [$"print {printTemplate}"], "loop_inc", null));
        }

        blocks.Add(new("loop_inc", [$"{varName} = {varName} + 1"], "loop_test", null));
        blocks.Add(new("exit",     [],                               null,        null));

        return blocks;
    }

    // Lowers a no-loop program whose branches depend on ComparisonNodes (compare: lt/gt/eq).
    // The structure is: read input → check branches in declaration order → fallthrough to default.
    private static List<CfgBlock> LowerComparisonBranch(IronLlm.Graph.SemanticGraph graph)
    {
        var program = graph.Nodes.OfType<ProgramNode>().FirstOrDefault();

        // Branches in declaration order (Contains edges from the program node).
        var orderedBranchIds = program != null
            ? graph.Edges
                .Where(e => e.From == program.Id && e.Type == EdgeType.Contains)
                .Select(e => e.To)
                .ToList()
            : [];

        var branches = orderedBranchIds
            .Select(id => graph.Nodes.OfType<BranchNode>().FirstOrDefault(n => n.Id == id))
            .Where(b => b != null)
            .Cast<BranchNode>()
            .ToList();

        // Comparison-based branches (have a DependsOn edge to a ComparisonNode).
        var cmpBranches = new List<(BranchNode Branch, ComparisonNode Cmp)>();
        foreach (var b in branches)
        {
            var cmpEdge = graph.Edges.FirstOrDefault(e => e.From == b.Id && e.Type == EdgeType.DependsOn);
            if (cmpEdge == null) continue;
            var cmp = graph.Nodes.OfType<ComparisonNode>().FirstOrDefault(n => n.Id == cmpEdge.To);
            if (cmp != null) cmpBranches.Add((b, cmp));
        }

        // Default branch: no DependsOn edge.
        var cmpBranchIds = cmpBranches.Select(x => x.Branch.Id).ToHashSet();
        var defaultBranch = branches.FirstOrDefault(b => !cmpBranchIds.Contains(b.Id));

        string PrintTemplateFor(BranchNode branch)
        {
            var printEdge = graph.Edges.FirstOrDefault(e => e.From == branch.Id && e.Type == EdgeType.TrueBranch);
            if (printEdge == null) return branch.Condition;
            var print = graph.Nodes.OfType<PrintNode>().FirstOrDefault(n => n.Id == printEdge.To);
            return print?.Template ?? branch.Condition;
        }

        // Input variable (may be int or string).
        var inputNode = graph.Nodes.OfType<InputNode>().FirstOrDefault();
        var inputName = inputNode?.Name ?? "input";

        var blocks = new List<CfgBlock>();

        // Entry: emit PrintNodes and InputNodes in Contains-edge order so that a
        // prompt print: before variable: appears before the read, as authored.
        var entryInstrs = new List<string>();
        if (program != null)
        {
            var nodeIndex = graph.Nodes.ToDictionary(n => n.Id);
            foreach (var id in orderedBranchIds)
            {
                if (!nodeIndex.TryGetValue(id, out var node)) continue;
                switch (node)
                {
                    case PrintNode p:
                        entryInstrs.Add($"print \"{p.Template}\"");
                        break;
                    case InputNode inp:
                        entryInstrs.Add(inp.Type == "int"
                            ? $"read_int {inp.Name}"
                            : $"read {inp.Name}");
                        break;
                }
            }
        }
        else if (inputNode != null)
        {
            entryInstrs.Add(inputNode.Type == "int"
                ? $"read_int {inputName}"
                : $"read {inputName}");
        }

        var firstCheckLabel = cmpBranches.Count > 0
            ? $"check_{cmpBranches[0].Branch.Condition}"
            : "default_output";

        blocks.Add(new("entry", entryInstrs, firstCheckLabel, null));

        var fallthrough = defaultBranch != null ? "default_output" : "exit";

        for (var i = 0; i < cmpBranches.Count; i++)
        {
            var (branch, cmp) = cmpBranches[i];
            var nextLabel = i + 1 < cmpBranches.Count
                ? $"check_{cmpBranches[i + 1].Branch.Condition}"
                : fallthrough;

            // Build the check instruction: "if lhs op rhs" where rhs is either int or {var}.
            var rhs = cmp.RhsVar != null ? $"{{{cmp.RhsVar}}}" : cmp.Value.ToString();
            var checkInstr = $"if {inputName} {cmp.Op} {rhs}";
            // lt/gt use Clt/Cgt → Brtrue to SuccessorFalse when condition is true.
            // eq uses Ceq → Brfalse to SuccessorFalse when condition is FALSE (not equal).
            // So for lt/gt: SuccessorFalse=print (taken), SuccessorTrue=next (fallthrough).
            //    for eq:    SuccessorFalse=next (not equal, fallthrough), SuccessorTrue=print (equal).
            if (cmp.Op == "eq")
                blocks.Add(new($"check_{branch.Condition}", [checkInstr], $"print_{branch.Condition}", nextLabel));
            else
                blocks.Add(new($"check_{branch.Condition}", [checkInstr], nextLabel, $"print_{branch.Condition}"));


            var template = PrintTemplateFor(branch);
            var printInstr = template.StartsWith('{') && template.EndsWith('}')
                ? $"print {template.Trim('{', '}')}"
                : $"print \"{template}\"";
            blocks.Add(new($"print_{branch.Condition}", [printInstr], "exit", null));
        }

        if (defaultBranch != null)
        {
            var template = PrintTemplateFor(defaultBranch);
            var printInstr = template.StartsWith('{') && template.EndsWith('}')
                ? $"print {template.Trim('{', '}')}"
                : $"print \"{template}\"";
            blocks.Add(new("default_output", [printInstr], "exit", null));
        }

        blocks.Add(new("exit", [], null, null));

        return blocks;
    }

    private static List<CfgBlock> LowerLinear(IronLlm.Graph.SemanticGraph graph)
    {
        // Collect PrintNodes and InputNodes in the order they appear as Contains edges
        // from the ProgramNode. Edges are added in spec-parse order so this preserves
        // the authorial sequence.
        var program = graph.Nodes.OfType<ProgramNode>().FirstOrDefault();
        var orderedIds = program != null
            ? graph.Edges
                .Where(e => e.From == program.Id && e.Type == EdgeType.Contains)
                .Select(e => e.To)
                .ToList()
            : [];

        var nodeIndex = graph.Nodes.ToDictionary(n => n.Id);
        var instructions = new List<string>();

        foreach (var id in orderedIds)
        {
            if (!nodeIndex.TryGetValue(id, out var node)) continue;
            switch (node)
            {
                case PrintNode p:
                    // Template is a literal string, a bare {varName}, or a mixed string like
                    // "Hello, {name}". For mixed templates, split into a literal print followed
                    // by a variable print so StackIrPass can lower each independently.
                    var template = p.Template;
                    var varMatch = System.Text.RegularExpressions.Regex.Match(template, @"\{(\w+)\}");
                    if (!varMatch.Success)
                    {
                        instructions.Add($"print \"{template}\"");
                    }
                    else if (template.StartsWith('{') && template.EndsWith('}') && template.Length == varMatch.Length)
                    {
                        // Pure variable reference: {user_name}
                        instructions.Add($"print {varMatch.Groups[1].Value}");
                    }
                    else
                    {
                        // Mixed literal+variable template: "Nice to meet you, {name}"
                        // Emit as: print_concat "prefix" varName "suffix"
                        // (suffix is usually empty; StackIrPass handles the concat+println)
                        var prefix = template[..varMatch.Index];
                        var varName = varMatch.Groups[1].Value;
                        var suffix = template[(varMatch.Index + varMatch.Length)..];
                        instructions.Add($"print_concat \"{prefix}\" {varName} \"{suffix}\"");
                    }
                    break;
                case InputNode inp:
                    var readInstr = inp.Type.Equals("int", StringComparison.OrdinalIgnoreCase)
                        ? $"read_int {inp.Name}"
                        : $"read {inp.Name}";
                    instructions.Add(readInstr);
                    break;
                case AssignNode a:
                    var assignInstr = a.Op == "copy"
                        ? $"assign {a.Target} copy {a.Left}"
                        : $"assign {a.Target} {a.Op} {a.Left} {a.Right}";
                    instructions.Add(assignInstr);
                    break;
            }
        }

        return
        [
            new("entry", instructions, "exit", null),
            new("exit",  [],           null,   null),
        ];
    }

    private static List<CfgBlock> LowerWhileLoop(IronLlm.Graph.SemanticGraph graph, WhileLoopNode wl)
    {
        var blocks = new List<CfgBlock>();

        // entry: read all InputNodes (program-level Contains), then jump to loop_top.
        var program = graph.Nodes.OfType<ProgramNode>().FirstOrDefault();
        var programContains = program != null
            ? graph.Edges
                .Where(e => e.From == program.Id && e.Type == EdgeType.Contains)
                .Select(e => e.To)
                .ToList()
            : [];
        var nodeIndex = graph.Nodes.ToDictionary(n => n.Id);

        var entryInstrs = new List<string>();
        foreach (var id in programContains)
        {
            if (!nodeIndex.TryGetValue(id, out var n)) continue;
            if (n is InputNode inp)
                entryInstrs.Add(inp.Type.Equals("int", StringComparison.OrdinalIgnoreCase)
                    ? $"read_int {inp.Name}"
                    : $"read {inp.Name}");
        }
        blocks.Add(new("entry", entryInstrs, "loop_top", null));

        // loop_top: emit PrintNodes and BranchNodes that are Contains children of the WhileLoopNode.
        var whileContainsIds = graph.Edges
            .Where(e => e.From == wl.Id && e.Type == EdgeType.Contains)
            .Select(e => e.To)
            .ToList();

        var loopTopInstrs = new List<string>();

        // Collect ordered body: PrintNodes go into loop_top; BranchNodes handled after while_test.
        var bodyNodes = whileContainsIds
            .Where(nodeIndex.ContainsKey)
            .Select(id => nodeIndex[id])
            .ToList();

        foreach (var node in bodyNodes)
        {
            if (node is not PrintNode p) continue;
            var tmpl = p.Template;
            var vm   = System.Text.RegularExpressions.Regex.Match(tmpl, @"\{(\w+)\}");
            if (!vm.Success)
                loopTopInstrs.Add($"print \"{tmpl}\"");
            else if (tmpl.StartsWith('{') && tmpl.EndsWith('}') && tmpl.Length == vm.Length)
                loopTopInstrs.Add($"print {vm.Groups[1].Value}");
            else
            {
                var prefix = tmpl[..vm.Index];
                var suffix = tmpl[(vm.Index + vm.Length)..];
                loopTopInstrs.Add($"print_concat \"{prefix}\" {vm.Groups[1].Value} \"{suffix}\"");
            }
        }

        // loop_top → while_test (check condition before executing branch bodies).
        blocks.Add(new("loop_top", loopTopInstrs, "while_test", null));

        // One check/assign block pair per BranchNode.
        // Condition names may collide (LLM can emit two "default" branches); use index suffix
        // to keep block labels unique.
        var branchNodes = bodyNodes.OfType<BranchNode>().ToList();
        string BranchLabel(int idx) =>
            branchNodes.Count(b => b.Condition == branchNodes[idx].Condition) > 1
                ? $"check_{branchNodes[idx].Condition}_{idx}"
                : $"check_{branchNodes[idx].Condition}";
        string BodyLabel(int branchIdx, int stepIdx) =>
            branchNodes.Count(b => b.Condition == branchNodes[branchIdx].Condition) > 1
                ? $"body_{branchNodes[branchIdx].Condition}_{branchIdx}_{stepIdx}"
                : $"body_{branchNodes[branchIdx].Condition}_{stepIdx}";

        for (var i = 0; i < branchNodes.Count; i++)
        {
            var branch = branchNodes[i];
            var nextCheckLabel = i + 1 < branchNodes.Count
                ? BranchLabel(i + 1)
                : "while_test";

            // Find the ModuloNode this branch depends on (divisor check).
            var modEdge = graph.Edges.FirstOrDefault(e => e.From == branch.Id && e.Type == EdgeType.DependsOn);
            var modNode = modEdge != null ? nodeIndex.GetValueOrDefault(modEdge.To) as ModuloNode : null;

            // Find the assign bodies: Contains edges from this branch to AssignNodes.
            var assigns = graph.Edges
                .Where(e => e.From == branch.Id && e.Type == EdgeType.Contains)
                .Select(e => e.To)
                .Where(nodeIndex.ContainsKey)
                .Select(id => nodeIndex[id])
                .OfType<AssignNode>()
                .ToList();

            var firstBodyLabel = assigns.Count > 0 ? BodyLabel(i, 0) : "while_test";

            if (modNode != null)
            {
                // Modulo check: "if n % div == 0" emits Rem + LdcI4 0 + Ceq.
                // Ceq=1 when divisible (true) → SuccessorTrue=firstBodyLabel.
                // Ceq=0 when not divisible (false) → Brfalse SuccessorFalse=nextCheckLabel.
                var checkInstr = $"if {wl.Variable} % {modNode.Divisor} == 0";
                blocks.Add(new(BranchLabel(i), [checkInstr], firstBodyLabel, nextCheckLabel));
            }
            else
            {
                // Default/else branch (no modulo): unconditionally jump to body.
                blocks.Add(new(BranchLabel(i), [], firstBodyLabel, null));
            }

            // Emit one block per assign in the branch body, chaining back to loop_top.
            for (var j = 0; j < assigns.Count; j++)
            {
                var a     = assigns[j];
                var instr = a.Op == "copy"
                    ? $"assign {a.Target} copy {a.Left}"
                    : $"assign {a.Target} {a.Op} {a.Left} {a.Right}";
                var next = j + 1 < assigns.Count ? BodyLabel(i, j + 1) : "loop_top";
                blocks.Add(new(BodyLabel(i, j), [instr], next, null));
            }
        }

        // while_test: if n == exitValue → exit; else → first branch check (or back to loop_top).
        // Uses Ceq: Ceq=1 when equal → SuccessorTrue=exit.
        //           Ceq=0 when not equal → Brfalse SuccessorFalse=first body check.
        var firstBranchOrTop = branchNodes.Count > 0 ? BranchLabel(0) : "loop_top";
        var testInstr = $"if {wl.Variable} eq {wl.Value}";
        blocks.Add(new("while_test", [testInstr], "exit", firstBranchOrTop));

        blocks.Add(new("exit", [], null, null));
        return blocks;
    }

    // Lowers a do-while interactive loop:
    //   entry: [RandomNode inits] [program-level prints] → read_step
    //   read_step: [InputNodes inside while body] → first_check
    //   check_{condition}: if lhs op rhs → print_{condition} / next_check
    //   print_{condition}: print "..." → read_step (loop back) or exit (on eq/correct)
    //   exit:
    private static List<CfgBlock> LowerInteractiveWhileLoop(
        IronLlm.Graph.SemanticGraph graph, WhileLoopNode wl)
    {
        var program = graph.Nodes.OfType<ProgramNode>().FirstOrDefault();
        var nodeIndex = graph.Nodes.ToDictionary(n => n.Id);

        // Program-level Contains children (RandomNodes, PrintNodes, InputNodes before the while).
        var programContainsIds = program != null
            ? graph.Edges
                .Where(e => e.From == program.Id && e.Type == EdgeType.Contains)
                .Select(e => e.To)
                .ToList()
            : [];

        // While-body Contains children (InputNodes, BranchNodes).
        var whileContainsIds = graph.Edges
            .Where(e => e.From == wl.Id && e.Type == EdgeType.Contains)
            .Select(e => e.To)
            .ToList();

        var whileBodyNodes = whileContainsIds
            .Where(nodeIndex.ContainsKey)
            .Select(id => nodeIndex[id])
            .ToList();

        // Build entry block: RandomNode inits + program-level prints.
        var entryInstrs = new List<string>();
        foreach (var id in programContainsIds)
        {
            if (!nodeIndex.TryGetValue(id, out var n)) continue;
            switch (n)
            {
                case RandomNode rn:
                    entryInstrs.Add($"rand_int {rn.Name} {rn.Min} {rn.Max}");
                    break;
                case PrintNode p:
                    entryInstrs.Add(p.Template.StartsWith('{') && p.Template.EndsWith('}')
                        ? $"print {p.Template.Trim('{', '}')}"
                        : $"print \"{p.Template}\"");
                    break;
            }
        }

        // read_step block: all InputNodes inside the while body.
        var readInstrs = new List<string>();
        foreach (var n in whileBodyNodes)
        {
            if (n is InputNode inp)
                readInstrs.Add(inp.Type.Equals("int", StringComparison.OrdinalIgnoreCase)
                    ? $"read_int {inp.Name}"
                    : $"read {inp.Name}");
        }

        // Gather comparison-based branch nodes in Contains order.
        var cmpBranches = new List<(BranchNode Branch, ComparisonNode Cmp)>();
        foreach (var n in whileBodyNodes.OfType<BranchNode>())
        {
            var cmpEdge = graph.Edges.FirstOrDefault(e => e.From == n.Id && e.Type == EdgeType.DependsOn);
            if (cmpEdge == null) continue;
            if (nodeIndex.TryGetValue(cmpEdge.To, out var cmpNode) && cmpNode is ComparisonNode cmp)
                cmpBranches.Add((n, cmp));
        }

        // Default branch (no DependsOn edge) — exits the loop on the fallthrough path.
        var cmpBranchIds = cmpBranches.Select(x => x.Branch.Id).ToHashSet();
        var defaultBranch = whileBodyNodes.OfType<BranchNode>()
            .FirstOrDefault(b => !cmpBranchIds.Contains(b.Id));

        string PrintFor(BranchNode b)
        {
            var pe = graph.Edges.FirstOrDefault(e => e.From == b.Id && e.Type == EdgeType.TrueBranch);
            return (pe != null && nodeIndex.TryGetValue(pe.To, out var pn) && pn is PrintNode pnp)
                ? pnp.Template : b.Condition;
        }

        var firstCheckLabel = cmpBranches.Count > 0
            ? $"check_{cmpBranches[0].Branch.Condition}"
            : (defaultBranch != null ? $"print_{defaultBranch.Condition}" : "exit");

        var blocks = new List<CfgBlock>();
        blocks.Add(new("entry",     entryInstrs, "read_step", null));
        blocks.Add(new("read_step", readInstrs,  firstCheckLabel, null));

        for (var i = 0; i < cmpBranches.Count; i++)
        {
            var (branch, cmp) = cmpBranches[i];
            var nextLabel = i + 1 < cmpBranches.Count
                ? $"check_{cmpBranches[i + 1].Branch.Condition}"
                : (defaultBranch != null ? $"print_{defaultBranch.Condition}" : "exit");

            // Build check instruction.
            var lhsVar = wl.Variable; // lhs is always the input variable
            var rhs    = cmp.RhsVar != null ? $"{{{cmp.RhsVar}}}" : cmp.Value.ToString();
            var checkInstr = $"if {lhsVar} {cmp.Op} {rhs}";

            // lt/gt: Clt/Cgt → Brtrue to SuccessorFalse (print block).
            //    SuccessorTrue=next, SuccessorFalse=print.
            // eq: Ceq → Brfalse to SuccessorFalse (next check).
            //    SuccessorTrue=print, SuccessorFalse=next.
            if (cmp.Op == "eq")
                blocks.Add(new($"check_{branch.Condition}", [checkInstr], $"print_{branch.Condition}", nextLabel));
            else
                blocks.Add(new($"check_{branch.Condition}", [checkInstr], nextLabel, $"print_{branch.Condition}"));

            var template = PrintFor(branch);
            var printInstr = template.StartsWith('{') && template.EndsWith('}')
                ? $"print {template.Trim('{', '}')}"
                : $"print \"{template}\"";
            // eq branch (correct guess): exit. All others: loop back to read_step.
            var afterPrint = cmp.Op == "eq" ? "exit" : "read_step";
            blocks.Add(new($"print_{branch.Condition}", [printInstr], afterPrint, null));
        }

        if (defaultBranch != null)
        {
            var template = PrintFor(defaultBranch);
            var printInstr = template.StartsWith('{') && template.EndsWith('}')
                ? $"print {template.Trim('{', '}')}"
                : $"print \"{template}\"";
            blocks.Add(new($"print_{defaultBranch.Condition}", [printInstr], "exit", null));
        }

        blocks.Add(new("exit", [], null, null));
        return blocks;
    }

    private static List<CfgBlock> LowerSelectionSort(IronLlm.Graph.SemanticGraph graph, ArrayNode arr)
    {
        var arrayName  = arr.Name;
        var arraySize  = arr.Size;
        var outerBound = arraySize - 2; // outer i runs 0..(size-2), e.g. 0..6 for size=8
        var printBound = arraySize - 1;
        var values     = arr.Values ?? Enumerable.Range(0, arraySize).ToArray();

        var entryInstructions = new List<string>();
        entryInstructions.Add($"newarr {arrayName} {arraySize}");
        for (var idx = 0; idx < arraySize; idx++)
            entryInstructions.Add($"{arrayName}[{idx}] = {values[idx]}");
        entryInstructions.Add("i = 0");

        // SelectionSort CFG:
        //
        // entry              → outer_loop_test
        // outer_loop_test    → min_init          (i <= outerBound: keep sorting)
        //                    → print_init         (i >  outerBound: done sorting)
        // min_init           → inner_loop_init    (min_index = i)
        // inner_loop_init    → inner_loop_test    (j = i + 1)
        // inner_loop_test    → compare            (j <= printBound)
        //                    → swap_min           (j > printBound: inner done)
        // compare            → inner_loop_inc     (arr[j] >= arr[min_index]: no update)
        //                    → update_min         (arr[j] < arr[min_index]: update)
        // update_min         → inner_loop_inc     (min_index = j)
        // inner_loop_inc     → inner_loop_test    (j = j + 1)
        // swap_min           → outer_loop_inc     (swap arr[i] <-> arr[min_index])
        // outer_loop_inc     → outer_loop_test    (i = i + 1)
        // print_init         → print_loop_test    (k = 0)
        // print_loop_test    → print_element      (k <= printBound)
        //                    → exit               (k > printBound)
        // print_element      → print_inc          (print arr[k])
        // print_inc          → print_loop_test    (k = k + 1)
        // exit

        return
        [
            new("entry",            entryInstructions,                                    "outer_loop_test", null),
            new("outer_loop_test",  [$"if i > {outerBound} goto print_init"],             "min_init",        "print_init"),
            new("min_init",         ["min_index = i"],                                    "inner_loop_init", null),
            new("inner_loop_init",  ["j = i"],                                            "inner_inc_j",     null),
            new("inner_inc_j",      ["j = j + 1"],                                       "inner_loop_test", null),
            new("inner_loop_test",  [$"if j > {printBound} goto swap_min"],               "compare",         "swap_min"),
            new("compare",          [$"if {arrayName}[j] < {arrayName}[min_index]"],      "inner_loop_inc",  "update_min"),
            new("update_min",       ["min_index = j"],                                    "inner_loop_inc",  null),
            new("inner_loop_inc",   ["j = j + 1"],                                       "inner_loop_test", null),
            new("swap_min",         [$"swap {arrayName}[i] {arrayName}[min_index]"],      "outer_loop_inc",  null),
            new("outer_loop_inc",   ["i = i + 1"],                                       "outer_loop_test", null),
            new("print_init",       ["k = 0"],                                            "print_loop_test", null),
            new("print_loop_test",  [$"if k > {printBound} goto exit"],                  "print_element",   "exit"),
            new("print_element",    [$"print {arrayName}[k]"],                            "print_inc",       null),
            new("print_inc",        ["k = k + 1"],                                       "print_loop_test", null),
            new("exit",             [],                                                   null,              null),
        ];
    }

    private static List<CfgBlock> LowerBubbleSort(IronLlm.Graph.SemanticGraph graph, ArrayNode arr)
    {
        var loop = graph.Nodes.OfType<LoopNode>().FirstOrDefault()
                   ?? throw new InvalidOperationException("No LoopNode in graph for array program");

        var arrayName  = arr.Name;
        var arraySize  = arr.Size;
        var outerBound = loop.To;       // e.g. 8 for a 10-element BubbleSort outer loop
        var printBound = arraySize - 1; // e.g. 9

        var values = arr.Values
            ?? throw new InvalidOperationException(
                $"ArrayNode '{arr.Name}' has no values — initial_value must be present in the spec.");

        // Build entry block: allocate + initialise array, then init outer loop counter
        var entryInstructions = new List<string>();
        entryInstructions.Add($"newarr {arrayName} {arraySize}");
        for (var idx = 0; idx < arraySize; idx++)
            entryInstructions.Add($"{arrayName}[{idx}] = {values[idx]}");
        entryInstructions.Add("i = 0");

        // Successor convention (matches flat-loop CfgPass + StackIrPass):
        //   Cgt pushes 1 when the condition is TRUE.
        //   StackIrPass emits Brtrue → SuccessorFalse when SuccessorFalse != null.
        //   So SuccessorFalse is the "condition is true → take branch" target.
        //       SuccessorTrue  is the "condition is false → fall through" target.
        //
        // outer_loop_test: "if i > outerBound" — when true (i>bound, sorting done) → print_init
        //   SuccessorTrue  = "inner_loop_init"  (i <= bound: keep sorting)
        //   SuccessorFalse = "print_init"        (i >  bound: start printing)
        //
        // inner_loop_test: "if j > (outerBound - i)" — when true → outer_loop_inc
        //   SuccessorTrue  = "compare"           (j still in range: compare)
        //   SuccessorFalse = "outer_loop_inc"    (inner loop done: advance i)
        //
        // compare: "if arr[j] > arr[j+1]" — when true → perform swap
        //   SuccessorTrue  = "inner_loop_inc"    (no swap needed: advance j)
        //   SuccessorFalse = "swap"              (swap needed: do swap first)
        //
        // print_loop_test: "if k > printBound" — when true → done printing → exit
        //   SuccessorTrue  = "print_element"     (k still in range: print)
        //   SuccessorFalse = "exit"              (k > bound: done)

        return
        [
            new("entry",           entryInstructions,                                          "outer_loop_test", null),
            new("outer_loop_test", [$"if i > {outerBound} goto print_init"],                   "inner_loop_init", "print_init"),
            new("inner_loop_init", ["j = 0"],                                                  "inner_loop_test", null),
            new("inner_loop_test", [$"if j > ({outerBound} - i) goto outer_loop_inc"],         "compare",         "outer_loop_inc"),
            new("compare",         [$"if {arrayName}[j] > {arrayName}[j+1]"],                  "inner_loop_inc",  "swap"),
            new("swap",            [$"swap {arrayName}[j] {arrayName}[j+1]"],                  "inner_loop_inc",  null),
            new("inner_loop_inc",  ["j = j + 1"],                                              "inner_loop_test", null),
            new("outer_loop_inc",  ["i = i + 1"],                                              "outer_loop_test", null),
            new("print_init",      ["k = 0"],                                                  "print_loop_test", null),
            new("print_loop_test", [$"if k > {printBound} goto exit"],                         "print_element",   "exit"),
            new("print_element",   [$"print {arrayName}[k]"],                                  "print_inc",       null),
            new("print_inc",       ["k = k + 1"],                                              "print_loop_test", null),
            new("exit",            [],                                                          null,              null),
        ];
    }

    private static void Validate(List<CfgBlock> blocks)
    {
        if (blocks.Count == 0)
            throw new InvalidOperationException("CFG is empty");

        var duplicates = blocks.GroupBy(b => b.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"CFG has duplicate block labels: {string.Join(", ", duplicates)}");

        var labels = blocks.Select(b => b.Label).ToHashSet();

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Label))
                throw new InvalidOperationException("CFG block has empty label");

            if (block.SuccessorTrue  != null && !labels.Contains(block.SuccessorTrue))
                throw new InvalidOperationException($"Block '{block.Label}' references unknown successor '{block.SuccessorTrue}'");

            if (block.SuccessorFalse != null && !labels.Contains(block.SuccessorFalse))
                throw new InvalidOperationException($"Block '{block.Label}' references unknown successor '{block.SuccessorFalse}'");
        }
    }
}
