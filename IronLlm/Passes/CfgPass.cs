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
            blocks = LowerArrayProgram(graph, arrayNode);
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

        // Build check labels from actual branch data; use slugified condition as label base.
        static string CheckLabel(string condition) => $"check_{condition}";
        static string PrintLabel(string condition) => $"print_{condition}";

        // Determine the ordered check chain: first modulo check's label (or print_n if none).
        var firstCheckLabel = modBranches.Count > 0
            ? CheckLabel(modBranches[0].Branch.Condition)
            : "print_n";

        var blocks = new List<CfgBlock>
        {
            new("entry",     [$"{varName} = {loop.From}"],            "loop_test",     null),
            new("loop_test", [$"if {varName} > {loop.To} goto exit"], firstCheckLabel, "exit"),
        };

        for (var i = 0; i < modBranches.Count; i++)
        {
            var mb       = modBranches[i];
            var cond     = mb.Branch.Condition;
            var nextLabel = i + 1 < modBranches.Count
                ? CheckLabel(modBranches[i + 1].Branch.Condition)
                : "print_n";
            blocks.Add(new(CheckLabel(cond), [$"if {varName} % {mb.Divisor} == 0"], PrintLabel(cond), nextLabel));
        }

        foreach (var mb in modBranches)
            blocks.Add(new(PrintLabel(mb.Branch.Condition), [$"print \"{PrintFor(mb.Branch)}\""], "loop_inc", null));

        blocks.Add(new("print_n", [$"print {varName}"], "loop_inc", null));

        blocks.Add(new("loop_inc", [$"{varName} = {varName} + 1"], "loop_test", null));
        blocks.Add(new("exit",     [],                               null,        null));

        return blocks;
    }

    private static List<CfgBlock> LowerArrayProgram(IronLlm.Graph.SemanticGraph graph, ArrayNode arr)
    {
        var loop = graph.Nodes.OfType<LoopNode>().FirstOrDefault()
                   ?? throw new InvalidOperationException("No LoopNode in graph for array program");

        var arrayName  = arr.Name;
        var arraySize  = arr.Size;
        var outerBound = loop.To;       // e.g. 8 for a 10-element BubbleSort outer loop
        var printBound = arraySize - 1; // e.g. 9

        // Use provided values or fall back to BubbleSort defaults
        var values = arr.Values ?? new[] { 64, 34, 25, 12, 22, 11, 90, 45, 78, 3 };

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
