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
                Branch  = graph.Nodes.OfType<BranchNode>().First(n => n.Id == e.From),
                Divisor = graph.Nodes.OfType<ModuloNode>().First(n => n.Id == e.To).Divisor,
            })
            .OrderByDescending(x => x.Divisor)
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

        if (defaultBranch != null)
            blocks.Add(new("print_n", [$"print {varName}"], "loop_inc", null));
        else
            blocks.Add(new("print_n", [$"print {varName}"], "loop_inc", null));

        blocks.Add(new("loop_inc", [$"{varName} = {varName} + 1"], "loop_test", null));
        blocks.Add(new("exit",     [],                               null,        null));

        Validate(blocks);

        foreach (var block in blocks)
            _logger.LogDebug("Block {Label}: successors=[{True},{False}]",
                block.Label, block.SuccessorTrue ?? "—", block.SuccessorFalse ?? "—");

        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms — {Count} blocks",
            Name, sw.ElapsedMilliseconds, blocks.Count);

        context.CfgBlocks = blocks;
        return Task.CompletedTask;
    }

    private static void Validate(List<CfgBlock> blocks)
    {
        if (blocks.Count == 0)
            throw new InvalidOperationException("CFG is empty");

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
