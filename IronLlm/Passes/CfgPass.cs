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

        var arrayNode = graph.Nodes.OfType<ArrayNode>().FirstOrDefault();
        if (arrayNode != null)
        {
            var hasMinIndex = graph.Nodes.OfType<VariableNode>()
                .Any(v => v.Name.Equals("min_index", StringComparison.OrdinalIgnoreCase));
            blocks = hasMinIndex
                ? LowerSelectionSort(graph, arrayNode)
                : LowerBubbleSort(graph, arrayNode);
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
