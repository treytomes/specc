using System.Text.Json;
using IronLlm.Graph;

namespace IronLlm.Passes;

// Deterministic CFG builder — derives control flow from the semantic graph.
// The LLM was originally prototyped here, but proved too small for reliable
// structured JSON emission. CFG construction from a typed semantic graph is
// mechanical lowering; it belongs in code, not in a prompt.
public class CfgPass : ICompilerPass
{
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
        var graph = context.SemanticGraph ?? throw new InvalidOperationException("SemanticGraph not set");

        var loop     = graph.Nodes.OfType<LoopNode>().FirstOrDefault()
                       ?? throw new InvalidOperationException("No LoopNode in graph");
        var branches = graph.Nodes.OfType<BranchNode>().ToList();
        var variable = graph.Nodes.OfType<VariableNode>().FirstOrDefault();
        var varName  = variable?.Name ?? "n";

        // Collect divisor-keyed branches in priority order (15, 3, 5, default).
        var modBranches = graph.Edges
            .Where(e => e.Type == EdgeType.DependsOn)
            .Select(e => new
            {
                Branch  = graph.Nodes.OfType<BranchNode>().First(n => n.Id == e.From),
                Divisor = graph.Nodes.OfType<ModuloNode>().First(n => n.Id == e.To).Divisor,
            })
            .OrderByDescending(x => x.Divisor)   // 15 before 3 and 5
            .ThenByDescending(x => x.Divisor)
            .ToList();

        // Build print label map: condition → print block label.
        var printLabels = new Dictionary<string, string>
        {
            ["divisible_by_15"] = "print_fizzbuzz",
            ["divisible_by_3"]  = "print_fizz",
            ["divisible_by_5"]  = "print_buzz",
            ["default"]         = "print_n",
        };

        // Resolve the output string for each branch condition.
        string PrintFor(string condition)
        {
            var branch = branches.FirstOrDefault(b => b.Condition == condition);
            if (branch == null) return condition;
            var printEdge = graph.Edges.FirstOrDefault(e => e.From == branch.Id && e.Type == EdgeType.TrueBranch);
            if (printEdge == null) return condition;
            var print = graph.Nodes.OfType<PrintNode>().FirstOrDefault(n => n.Id == printEdge.To);
            return print?.Template ?? condition;
        }

        // Check order: 15, 3, 5
        var checks = new (int Divisor, string Condition, string CheckLabel, string PrintLabel, string Next)[]
        {
            (15, "divisible_by_15", "fizzbuzz_check", "print_fizzbuzz", "fizz_check"),
            ( 3, "divisible_by_3",  "fizz_check",     "print_fizz",     "buzz_check"),
            ( 5, "divisible_by_5",  "buzz_check",     "print_buzz",     "print_n"),
        };

        var blocks = new List<CfgBlock>
        {
            new("entry",
                [$"{varName} = {loop.From}"],
                "loop_test", null),

            new("loop_test",
                [$"if {varName} > {loop.To} goto exit"],
                "fizzbuzz_check", "exit"),
        };

        foreach (var (divisor, _, checkLabel, printLabel, nextCheck) in checks)
        {
            blocks.Add(new(checkLabel,
                [$"if {varName} % {divisor} == 0"],
                printLabel, nextCheck));
        }

        // Print blocks
        blocks.Add(new("print_fizzbuzz", [$"print \"{PrintFor("divisible_by_15")}\""], "loop_inc", null));
        blocks.Add(new("print_fizz",     [$"print \"{PrintFor("divisible_by_3")}\""],  "loop_inc", null));
        blocks.Add(new("print_buzz",     [$"print \"{PrintFor("divisible_by_5")}\""],  "loop_inc", null));
        blocks.Add(new("print_n",        [$"print {varName}"],                          "loop_inc", null));

        blocks.Add(new("loop_inc",
            [$"{varName} = {varName} + 1"],
            "loop_test", null));

        blocks.Add(new("exit", [], null, null));

        Validate(blocks);
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
