using System.Diagnostics;
using System.Text.Json;
using Specc.Graph;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

public class AcceptanceCriteriaPass : ICompilerPass
{
    private readonly ILogger<AcceptanceCriteriaPass> _logger;

    public AcceptanceCriteriaPass(ILogger<AcceptanceCriteriaPass> logger)
    {
        _logger = logger;
    }

    public string  Name         => "02b-AcceptanceCriteria";
    public string? ArtifactFile => "00-acceptance.json";

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json = await File.ReadAllTextAsync(artifactPath);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        context.Assertions = JsonSerializer.Deserialize<List<AssertionRecord>>(json, opts) ?? [];
        _logger.LogDebug("Loaded {Count} assertions from {Path}", context.Assertions.Count, artifactPath);
    }

    public Task ExecuteAsync(CompilationContext context)
    {
        var graph = context.SemanticGraph
            ?? throw new InvalidOperationException("SemanticGraph not set");

        // Array programs require running the sort to determine expected output — graph-derived
        // assertions cannot be computed statically.
        // Linear programs (no loop, no array) use authorial assertions only.
        var hasLoop = graph.Nodes.OfType<LoopNode>().Any();
        if (!hasLoop && !graph.Nodes.OfType<ArrayNode>().Any())
        {
            context.Assertions = [];
            _logger.LogInformation(
                "Linear program detected — skipping graph-derived assertions ({Authorial} authorial assertion(s) available)",
                context.AuthorialAssertions.Count);
            return Task.CompletedTask;
        }

        if (graph.Nodes.OfType<WhileLoopNode>().Any())
        {
            context.Assertions = [];
            _logger.LogInformation(
                "While-loop program detected — skipping graph-derived assertions ({Authorial} authorial assertion(s) available)",
                context.AuthorialAssertions.Count);
            return Task.CompletedTask;
        }

        var arrayNode = graph.Nodes.OfType<ArrayNode>().FirstOrDefault();
        if (arrayNode != null)
        {
            context.Assertions = [];

            // Validate authorial assertions against the array size. The LLM sometimes
            // produces an incorrect count or wrong values; discard them if the count
            // doesn't match the expected output line count (one line per array element).
            if (context.AuthorialAssertions.Count != arrayNode.Size)
            {
                if (context.AuthorialAssertions.Count > 0)
                {
                    _logger.LogWarning(
                        "Discarding {Count} authorial assertions — expected {Size} (one per array element)",
                        context.AuthorialAssertions.Count, arrayNode.Size);
                    // Remove the stale artifact so it isn't reloaded on the next incremental run.
                    var badFile = Path.Combine(context.ArtifactsDir, "00-authorial-criteria.json");
                    if (File.Exists(badFile)) File.Delete(badFile);
                }
                context.AuthorialAssertions = [];
            }

            _logger.LogInformation(
                "Array program detected — skipping graph-derived assertions ({Authorial} authorial assertion(s) available)",
                context.AuthorialAssertions.Count);
            return Task.CompletedTask;
        }

        var sw       = Stopwatch.StartNew();
        var loop     = graph.Nodes.OfType<LoopNode>().FirstOrDefault()
                       ?? throw new InvalidOperationException("No LoopNode in graph — cannot derive acceptance criteria");
        var variable = graph.Nodes.OfType<VariableNode>().FirstOrDefault();
        var varName  = variable?.Name ?? "n";

        // Modulo branches sorted descending by divisor so the most-constrained check wins first.
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
            .ToList();

        var modBranchIds  = modBranches.Select(mb => mb.Branch.Id).ToHashSet();
        var defaultBranch = graph.Nodes.OfType<BranchNode>()
            .FirstOrDefault(b => !modBranchIds.Contains(b.Id));

        string PrintFor(BranchNode branch)
        {
            var printEdge = graph.Edges
                .FirstOrDefault(e => e.From == branch.Id && e.Type == EdgeType.TrueBranch);
            var print = printEdge != null
                ? graph.Nodes.OfType<PrintNode>().FirstOrDefault(n => n.Id == printEdge.To)
                : null;
            return print?.Template ?? branch.Condition;
        }

        var program    = graph.Nodes.OfType<ProgramNode>().FirstOrDefault();
        var assertions = new List<AssertionRecord>();

        for (var i = loop.From; i <= loop.To; i++)
        {
            string expected;
            var matched = false;

            foreach (var mb in modBranches)
            {
                if (i % mb.Divisor == 0)
                {
                    expected = PrintFor(mb.Branch);
                    assertions.Add(new AssertionRecord(i, expected));
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                var template = defaultBranch != null ? PrintFor(defaultBranch) : $"{{{varName}}}";
                // Resolve variable placeholder to the iteration value.
                expected = (template == $"{{{varName}}}" || template == "{n}")
                    ? i.ToString()
                    : template;
                assertions.Add(new AssertionRecord(i, expected));
            }
        }

        // Add AssertionNodes to the graph so the expected output is a first-class graph citizen.

        foreach (var a in assertions)
        {
            var node = new AssertionNode(Guid.NewGuid(), $"Assert:{a.Iteration}={a.Expected}", a.Iteration, a.Expected);
            graph.Add(node);
            if (program != null)
                graph.Connect(program.Id, node.Id, EdgeType.Asserts);
        }

        context.Assertions = assertions;

        _logger.LogInformation(
            "Pass {Name} completed in {ElapsedMs}ms — {Count} assertions over iterations {From}..{To}",
            Name, sw.ElapsedMilliseconds, assertions.Count, loop.From, loop.To);

        return Task.CompletedTask;
    }
}
