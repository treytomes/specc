using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Specc.Graph;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

// ── Output types ─────────────────────────────────────────────────────────────

public record ValidationReport(bool Passed, List<ValidationCheck> Checks);
public record ValidationCheck(string Name, bool Passed, string? Detail = null);

// ── Pass ─────────────────────────────────────────────────────────────────────

public class SemanticValidationPass : ICompilerPass
{
    private readonly ILogger<SemanticValidationPass> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SemanticValidationPass(ILogger<SemanticValidationPass> logger)
    {
        _logger = logger;
    }

    public string  Name         => "08-Validation";
    public string? ArtifactFile => "08-validation.json";

    // ── Execute ───────────────────────────────────────────────────────────────

    public async Task ExecuteAsync(CompilationContext context)
    {
        var sw     = Stopwatch.StartNew();
        var checks = new List<ValidationCheck>();

        RunGraphChecks(context, checks);
        RunCfgChecks(context, checks);
        RunStackIrChecks(context, checks);

        var passed = checks.All(c => c.Passed);
        var report = new ValidationReport(passed, checks);
        context.ValidationReport = report;

        // Always write the artifact — even on failure — for debuggability.
        Directory.CreateDirectory(context.ArtifactsDir);
        var artifactPath = Path.Combine(context.ArtifactsDir, ArtifactFile!);
        await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(report, JsonOpts));
        _logger.LogDebug("Artifact written: {Path}", artifactPath);

        _logger.LogInformation(
            "Pass {Name} completed in {ElapsedMs}ms — {Passed}/{Total} checks passed",
            Name, sw.ElapsedMilliseconds, checks.Count(c => c.Passed), checks.Count);

        if (!passed)
        {
            var first = checks.First(c => !c.Passed);
            throw new CompilationException(
                first.Detail != null
                    ? $"Validation failed [{first.Name}]: {first.Detail}"
                    : $"Validation failed [{first.Name}]");
        }
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json   = await File.ReadAllTextAsync(artifactPath);
        var report = JsonSerializer.Deserialize<ValidationReport>(json, JsonOpts)
            ?? throw new CompilationException("Could not deserialize validation report");

        context.ValidationReport = report;

        if (!report.Passed)
        {
            var first = report.Checks.FirstOrDefault(c => !c.Passed);
            var desc  = first != null
                ? (first.Detail != null ? $"{first.Name}: {first.Detail}" : first.Name)
                : "unknown";
            throw new CompilationException($"Prior validation failed: {desc}");
        }
    }

    // ── Semantic Graph checks (invariants 1-5) ────────────────────────────────

    private static void RunGraphChecks(CompilationContext context, List<ValidationCheck> checks)
    {
        var graph = context.SemanticGraph;

        if (graph == null)
        {
            checks.Add(new ValidationCheck("single program node",    false, "SemanticGraph is null"));
            checks.Add(new ValidationCheck("all nodes reachable",    false, "SemanticGraph is null"));
            checks.Add(new ValidationCheck("no dangling edges",      false, "SemanticGraph is null"));
            checks.Add(new ValidationCheck("branch nodes have true-branch edge", false, "SemanticGraph is null"));
            checks.Add(new ValidationCheck("modulo nodes referenced", false, "SemanticGraph is null"));
            return;
        }

        var nodes   = graph.Nodes;
        var edges   = graph.Edges;
        var nodeIds = nodes.Select(n => n.Id).ToHashSet();

        // 1. Exactly one ProgramNode
        var programNodes = nodes.OfType<ProgramNode>().ToList();
        var singleProgram = programNodes.Count == 1;
        checks.Add(new ValidationCheck("single program node",
            singleProgram,
            singleProgram ? null : $"found {programNodes.Count} ProgramNode(s)"));

        // 2. Every non-AssertionNode reachable from ProgramNode via non-Asserts edges (BFS)
        if (singleProgram)
        {
            var programId    = programNodes[0].Id;
            var reachable    = BfsNonAsserts(programId, nodes, edges);
            var nonAssertion = nodes.Where(n => n is not AssertionNode).ToList();
            var unreachable  = nonAssertion.Where(n => !reachable.Contains(n.Id)).ToList();
            var allReachable = unreachable.Count == 0;
            checks.Add(new ValidationCheck("all nodes reachable",
                allReachable,
                allReachable ? null : $"unreachable nodes: {string.Join(", ", unreachable.Select(n => n.Label))}"));
        }
        else
        {
            checks.Add(new ValidationCheck("all nodes reachable", false,
                "cannot check reachability without exactly one ProgramNode"));
        }

        // 3. No dangling edge endpoints
        var danglingEdges = edges.Where(e => !nodeIds.Contains(e.From) || !nodeIds.Contains(e.To)).ToList();
        var noDangling    = danglingEdges.Count == 0;
        checks.Add(new ValidationCheck("no dangling edges",
            noDangling,
            noDangling ? null : $"{danglingEdges.Count} edge(s) reference unknown node IDs"));

        // 4. Every BranchNode has at least one outgoing TrueBranch edge OR Contains-edge assign body
        //    (true_assign: branches in while: loops use Contains edges instead of TrueBranch).
        var branchNodes         = nodes.OfType<BranchNode>().ToList();
        var branchesWithoutTrue = branchNodes
            .Where(b => !edges.Any(e => e.From == b.Id && e.Type == EdgeType.TrueBranch)
                     && !edges.Any(e => e.From == b.Id && e.Type == EdgeType.Contains
                                     && nodes.OfType<AssignNode>().Any(a => a.Id == e.To)))
            .ToList();
        var branchesOk = branchesWithoutTrue.Count == 0;
        checks.Add(new ValidationCheck("branch nodes have true-branch edge",
            branchesOk,
            branchesOk ? null : $"missing TrueBranch edge on: {string.Join(", ", branchesWithoutTrue.Select(b => b.Label))}"));

        // 5. Every ModuloNode referenced by at least one DependsOn edge
        var moduloNodes        = nodes.OfType<ModuloNode>().ToList();
        var unreferencedModulo = moduloNodes
            .Where(m => !edges.Any(e => e.To == m.Id && e.Type == EdgeType.DependsOn))
            .ToList();
        var moduloOk = unreferencedModulo.Count == 0;
        checks.Add(new ValidationCheck("modulo nodes referenced",
            moduloOk,
            moduloOk ? null : $"unreferenced ModuloNode(s): {string.Join(", ", unreferencedModulo.Select(m => m.Label))}"));

        // 6. Every IndexNode references an ArrayNode that exists in the graph (by Name)
        var arrayNames   = nodes.OfType<ArrayNode>().Select(a => a.Name).ToHashSet();
        var indexNodes   = nodes.OfType<IndexNode>().ToList();
        var badIndex     = indexNodes.Where(ix => !arrayNames.Contains(ix.ArrayName)).ToList();
        var indexOk      = badIndex.Count == 0;
        checks.Add(new ValidationCheck("index nodes reference existing array",
            indexOk,
            indexOk ? null : $"IndexNode(s) with unknown array: {string.Join(", ", badIndex.Select(ix => ix.Label))}"));

        // 7. Every SwapNode references an ArrayNode that exists in the graph
        var badSwap  = nodes.OfType<SwapNode>().Where(sw => !arrayNames.Contains(sw.ArrayName)).ToList();
        var swapOk   = badSwap.Count == 0;
        checks.Add(new ValidationCheck("swap nodes reference existing array",
            swapOk,
            swapOk ? null : $"SwapNode(s) with unknown array: {string.Join(", ", badSwap.Select(sw => sw.Label))}"));

        // 8. Every NestedLoopNode BoundExpr references a variable present in the graph
        var varNames       = nodes.OfType<VariableNode>().Select(v => v.Name)
            .Concat(nodes.OfType<LoopNode>().Select(l => l.Label))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nestedLoops    = nodes.OfType<NestedLoopNode>().ToList();
        var badNestedLoops = nestedLoops.Where(nl =>
            !varNames.Any(v => nl.BoundExpr.Contains(v, StringComparison.OrdinalIgnoreCase))).ToList();
        var nestedLoopOk   = badNestedLoops.Count == 0;
        checks.Add(new ValidationCheck("nested loop bound expr references known variable",
            nestedLoopOk,
            nestedLoopOk ? null : $"NestedLoopNode(s) with unresolved bound: {string.Join(", ", badNestedLoops.Select(nl => nl.Label))}"));
    }

    private static HashSet<Guid> BfsNonAsserts(Guid start, List<Node> nodes, List<Edge> edges)
    {
        var visited = new HashSet<Guid>();
        var queue   = new Queue<Guid>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;

            foreach (var edge in edges)
            {
                if (edge.From == id && edge.Type != EdgeType.Asserts)
                {
                    if (!visited.Contains(edge.To))
                        queue.Enqueue(edge.To);
                }
            }
        }

        return visited;
    }

    // ── CFG checks (invariants 6-10) ──────────────────────────────────────────

    private static void RunCfgChecks(CompilationContext context, List<ValidationCheck> checks)
    {
        var blocks = context.CfgBlocks;

        // 6. At least one block
        var hasBlocks = blocks.Count > 0;
        checks.Add(new ValidationCheck("cfg has blocks",
            hasBlocks,
            hasBlocks ? null : "CfgBlocks is empty"));

        if (!hasBlocks) return;

        // 7. All block labels unique
        var labels        = blocks.Select(b => b.Label).ToList();
        var duplicates    = labels.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var labelsUnique  = duplicates.Count == 0;
        checks.Add(new ValidationCheck("cfg labels unique",
            labelsUnique,
            labelsUnique ? null : $"duplicate labels: {string.Join(", ", duplicates)}"));

        var labelSet = labels.ToHashSet();

        // 8. All successor references resolve to a known label
        var badSuccessors = blocks
            .SelectMany(b => new[]
            {
                (block: b.Label, successor: b.SuccessorTrue),
                (block: b.Label, successor: b.SuccessorFalse),
            })
            .Where(t => t.successor != null && !labelSet.Contains(t.successor))
            .Select(t => $"{t.block}->{t.successor}")
            .ToList();
        var successorsResolve = badSuccessors.Count == 0;
        checks.Add(new ValidationCheck("cfg successors resolve",
            successorsResolve,
            successorsResolve ? null : $"unresolved successors: {string.Join(", ", badSuccessors)}"));

        // 9. Every non-exit block has at least one instruction (exit block = no successors)
        // Identify exit blocks as those with no successors; there could be zero or more; we
        // validate the count separately (invariant 10). For this check, allow any block that
        // has no successors to be instruction-free.
        var noSuccessorLabels = blocks
            .Where(b => b.SuccessorTrue == null && b.SuccessorFalse == null)
            .Select(b => b.Label)
            .ToHashSet();
        // check_* blocks with no SuccessorFalse are unconditional jumps (e.g. check_default in
        // a while loop's else branch) — they legitimately carry no instructions.
        var emptyNonExitBlocks = blocks
            .Where(b => !noSuccessorLabels.Contains(b.Label)
                     && b.Instructions.Count == 0
                     && !(b.Label.StartsWith("check_") && b.SuccessorFalse == null))
            .Select(b => b.Label)
            .ToList();
        var nonExitHaveInstructions = emptyNonExitBlocks.Count == 0;
        checks.Add(new ValidationCheck("non-exit blocks have instructions",
            nonExitHaveInstructions,
            nonExitHaveInstructions ? null : $"empty non-exit blocks: {string.Join(", ", emptyNonExitBlocks)}"));

        // 10. Exactly one block with no successors
        var exitBlockCount    = noSuccessorLabels.Count;
        var exactlyOneExit    = exitBlockCount == 1;
        checks.Add(new ValidationCheck("exactly one exit block",
            exactlyOneExit,
            exactlyOneExit ? null : $"found {exitBlockCount} block(s) with no successors"));
    }

    // ── Stack IR checks (invariants 11-13) ────────────────────────────────────

    private static void RunStackIrChecks(CompilationContext context, List<ValidationCheck> checks)
    {
        var ir = context.StackIr;

        if (ir.Count == 0)
        {
            checks.Add(new ValidationCheck("stack ir labels unique",     false, "StackIr is empty"));
            checks.Add(new ValidationCheck("stack ir branches resolve",  false, "StackIr is empty"));
            checks.Add(new ValidationCheck("stack ir ends with ret",     false, "StackIr is empty"));
            return;
        }

        // 11. Every Label pseudo-op has a unique operand
        var labelOps       = ir.Where(i => i.Op == OpCode.Label).ToList();
        var labelOperands  = labelOps.Select(i => i.Operand).ToList();
        var dupLabels      = labelOperands.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var labelsUnique   = dupLabels.Count == 0;
        checks.Add(new ValidationCheck("stack ir labels unique",
            labelsUnique,
            labelsUnique ? null : $"duplicate label operands: {string.Join(", ", dupLabels)}"));

        var definedLabels = labelOperands.ToHashSet();

        // 12. Every Brfalse/Brtrue/Br operand matches a Label in the sequence
        var branchOps = ir
            .Where(i => i.Op is OpCode.Brfalse or OpCode.Brtrue or OpCode.Br)
            .ToList();
        var unresolvedBranches = branchOps
            .Where(i => i.Operand == null || !definedLabels.Contains(i.Operand))
            .Select(i => $"{i.Op}({i.Operand})")
            .ToList();
        var branchesResolve = unresolvedBranches.Count == 0;
        checks.Add(new ValidationCheck("stack ir branches resolve",
            branchesResolve,
            branchesResolve ? null : $"unresolved branch targets: {string.Join(", ", unresolvedBranches)}"));

        // 13. Sequence ends with Ret
        var endsWithRet = ir[^1].Op == OpCode.Ret;
        checks.Add(new ValidationCheck("stack ir ends with ret",
            endsWithRet,
            endsWithRet ? null : $"last instruction is {ir[^1].Op}, expected Ret"));

        // 14. No local name used as both scalar and array (LdlocA/StlocA vs LdlocS/StlocS)
        var arrayLocals  = ir.Where(i => i.Op is OpCode.LdlocA or OpCode.StlocA)
                             .Select(i => i.Operand).Where(o => o != null).ToHashSet()!;
        var scalarLocals = ir.Where(i => i.Op is OpCode.LdlocS or OpCode.StlocS)
                             .Select(i => i.Operand).Where(o => o != null).ToHashSet()!;
        var mixedLocals  = arrayLocals.Intersect(scalarLocals).ToList();
        var noMixedLocals = mixedLocals.Count == 0;
        checks.Add(new ValidationCheck("no locals used as both scalar and array",
            noMixedLocals,
            noMixedLocals ? null : $"local(s) used as both scalar and array: {string.Join(", ", mixedLocals.Select(n => $"'{n}'"))}"));
    }
}
