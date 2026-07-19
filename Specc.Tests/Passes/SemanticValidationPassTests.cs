using Specc.Graph;
using Specc.Passes;
using Specc.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Specc.Tests.Passes;

public class SemanticValidationPassTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SemanticValidationPass MakePass() =>
        new(NullLogger<SemanticValidationPass>.Instance);

    /// <summary>
    /// Build a minimal but fully valid context (graph + CFG + StackIR) that
    /// should pass all 13 invariants.
    /// </summary>
    private static CompilationContext BuildValidContext()
    {
        var ctx = PipelineFixtures.AfterStackIr();
        return ctx;
    }

    // ── Minimal graph helpers ─────────────────────────────────────────────────

    private static (SemanticGraph Graph, ProgramNode Program, BranchNode Branch,
                    ModuloNode Modulo, PrintNode Print) BuildMinimalGraph()
    {
        var graph   = new SemanticGraph();
        var prog    = new ProgramNode(Guid.NewGuid(), "Program:Test", "Test");
        var branch  = new BranchNode(Guid.NewGuid(), "Branch:div3", "div3");
        var modulo  = new ModuloNode(Guid.NewGuid(), "Modulo:3", 3);
        var print   = new PrintNode(Guid.NewGuid(), "Print:Fizz", "Fizz");

        graph.Add(prog);
        graph.Add(branch);
        graph.Add(modulo);
        graph.Add(print);

        graph.Connect(prog.Id,   branch.Id, EdgeType.Contains);
        graph.Connect(branch.Id, modulo.Id, EdgeType.DependsOn);
        graph.Connect(branch.Id, print.Id,  EdgeType.TrueBranch);

        return (graph, prog, branch, modulo, print);
    }

    private static (List<CfgBlock> Blocks, string EntryLabel, string ExitLabel) BuildMinimalCfg()
    {
        var entry = new CfgBlock("entry", ["n = 1"], "exit", null);
        var exit  = new CfgBlock("exit",  [],        null,   null);
        return ([entry, exit], "entry", "exit");
    }

    private static List<StackInstruction> BuildMinimalStackIr()
    {
        return
        [
            new(OpCode.Label,  "entry"),
            new(OpCode.LdcI4,  "1"),
            new(OpCode.StlocS, "n"),
            new(OpCode.Br,     "exit"),
            new(OpCode.Label,  "exit"),
            new(OpCode.Ret),
        ];
    }

    private static CompilationContext BuildMinimalContext()
    {
        var (graph, _, _, _, _) = BuildMinimalGraph();
        var (blocks, _, _)      = BuildMinimalCfg();
        var ir                  = BuildMinimalStackIr();

        var ctx = PipelineFixtures.MakeContext();
        ctx.SemanticGraph = graph;
        ctx.CfgBlocks     = blocks;
        ctx.StackIr       = ir;
        return ctx;
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_Passes_ValidFizzBuzzPipeline()
    {
        var ctx  = BuildValidContext();
        var pass = MakePass();
        await pass.ExecuteAsync(ctx);

        Assert.NotNull(ctx.ValidationReport);
        Assert.True(ctx.ValidationReport!.Passed);
        Assert.All(ctx.ValidationReport.Checks, c => Assert.True(c.Passed));
    }

    [Fact]
    public async Task Execute_Passes_MinimalValidContext()
    {
        var ctx  = BuildMinimalContext();
        var pass = MakePass();
        await pass.ExecuteAsync(ctx);

        Assert.NotNull(ctx.ValidationReport);
        Assert.True(ctx.ValidationReport!.Passed);
    }

    [Fact]
    public async Task Execute_SetsValidationReport_OnContext()
    {
        var ctx = BuildMinimalContext();
        await MakePass().ExecuteAsync(ctx);
        Assert.NotNull(ctx.ValidationReport);
    }

    [Fact]
    public async Task Execute_WritesArtifact()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var ctx2 = new CompilationContext
            {
                SpecPath     = "fake.spec",
                ArtifactsDir = tmpDir,
            };
            var (graph, _, _, _, _) = BuildMinimalGraph();
            var (blocks, _, _)      = BuildMinimalCfg();
            ctx2.SemanticGraph = graph;
            ctx2.CfgBlocks     = blocks;
            ctx2.StackIr       = BuildMinimalStackIr();

            await MakePass().ExecuteAsync(ctx2);

            Assert.True(File.Exists(Path.Combine(tmpDir, "08-validation.json")));
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    // ── Graph invariant failures ───────────────────────────────────────────────

    [Fact]
    public async Task Execute_Throws_WhenNoProgramNode()
    {
        var ctx = BuildMinimalContext();
        // Remove the program node; leave other nodes orphaned (also triggers reachability fail)
        var program = ctx.SemanticGraph!.Nodes.OfType<ProgramNode>().Single();
        ctx.SemanticGraph.Nodes.Remove(program);
        // Remove edges referencing the removed program so dangling check doesn't fire first
        ctx.SemanticGraph.Edges.RemoveAll(e => e.From == program.Id || e.To == program.Id);

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("single program node", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenMultipleProgramNodes()
    {
        var ctx = BuildMinimalContext();
        ctx.SemanticGraph!.Add(new ProgramNode(Guid.NewGuid(), "Program:Extra", "Extra"));

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("single program node", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenNodeUnreachable()
    {
        var ctx      = BuildMinimalContext();
        var orphan   = new PrintNode(Guid.NewGuid(), "Print:Orphan", "Orphan");
        ctx.SemanticGraph!.Add(orphan);
        // No edge connects orphan to the graph.

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("all nodes reachable", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenDanglingEdgeEndpoint()
    {
        var ctx     = BuildMinimalContext();
        var ghostId = Guid.NewGuid();
        // Add an edge pointing to a node that doesn't exist.
        var prog = ctx.SemanticGraph!.Nodes.OfType<ProgramNode>().Single();
        ctx.SemanticGraph.Edges.Add(new Edge(Guid.NewGuid(), prog.Id, ghostId, EdgeType.Contains));

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("no dangling edges", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenBranchNodeHasNoTrueBranchEdge()
    {
        var ctx    = BuildMinimalContext();
        var prog   = ctx.SemanticGraph!.Nodes.OfType<ProgramNode>().Single();
        var branch = ctx.SemanticGraph.Nodes.OfType<BranchNode>().Single();
        var print  = ctx.SemanticGraph.Nodes.OfType<PrintNode>().Single();

        // Remove the TrueBranch edge so the branch-check fires.
        ctx.SemanticGraph.Edges.RemoveAll(e => e.From == branch.Id && e.Type == EdgeType.TrueBranch);
        // Keep PrintNode reachable via a Contains edge so the reachability check still passes.
        ctx.SemanticGraph.Connect(prog.Id, print.Id, EdgeType.Contains);

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("branch nodes have true-branch edge", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenModuloNodeHasNoDependsOnEdge()
    {
        var ctx    = BuildMinimalContext();
        var branch = ctx.SemanticGraph!.Nodes.OfType<BranchNode>().Single();
        var modulo = ctx.SemanticGraph.Nodes.OfType<ModuloNode>().Single();

        // Remove the DependsOn edge so the modulo-check fires.
        ctx.SemanticGraph.Edges.RemoveAll(e => e.To == modulo.Id && e.Type == EdgeType.DependsOn);
        // Keep ModuloNode reachable via a Contains edge so reachability check still passes.
        ctx.SemanticGraph.Connect(branch.Id, modulo.Id, EdgeType.Contains);

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("modulo nodes referenced", ex.Message);
    }

    // ── CFG invariant failures ────────────────────────────────────────────────

    [Fact]
    public async Task Execute_Throws_WhenCfgEmpty()
    {
        var ctx = BuildMinimalContext();
        ctx.CfgBlocks.Clear();

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("cfg has blocks", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenCfgLabelsNotUnique()
    {
        var ctx = BuildMinimalContext();
        ctx.CfgBlocks.Add(new CfgBlock("entry", ["x = 1"], null, null));
        // Now "entry" appears twice; the second block is also an exit block
        // so we have two exits (invariant 10 may fire), but label uniqueness fires first.

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("cfg labels unique", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenSuccessorNotInLabels()
    {
        var ctx = BuildMinimalContext();
        // Replace entry block with one pointing to a non-existent label.
        ctx.CfgBlocks[0] = new CfgBlock("entry", ["n = 1"], "ghost_label", null);

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("cfg successors resolve", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenNonExitBlockHasNoInstructions()
    {
        var ctx = BuildMinimalContext();
        // Replace entry with an empty block that still has a successor.
        ctx.CfgBlocks[0] = new CfgBlock("entry", [], "exit", null);

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("non-exit blocks have instructions", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenNoExitBlock()
    {
        var ctx = BuildMinimalContext();
        // Give every block a successor so there's no exit block.
        ctx.CfgBlocks.Clear();
        ctx.CfgBlocks.Add(new CfgBlock("a", ["x = 1"], "b", null));
        ctx.CfgBlocks.Add(new CfgBlock("b", ["y = 2"], "a", null));
        // Update StackIR so the IR checks don't fail first.
        ctx.StackIr =
        [
            new(OpCode.Label,  "a"),
            new(OpCode.LdcI4,  "1"),
            new(OpCode.Br,     "b"),
            new(OpCode.Label,  "b"),
            new(OpCode.LdcI4,  "2"),
            new(OpCode.Br,     "a"),
            new(OpCode.Ret),
        ];

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("exactly one exit block", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenMultipleExitBlocks()
    {
        var ctx = BuildMinimalContext();
        // Add a second block with no successors.
        ctx.CfgBlocks.Add(new CfgBlock("exit2", [], null, null));

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("exactly one exit block", ex.Message);
    }

    // ── Stack IR invariant failures ───────────────────────────────────────────

    [Fact]
    public async Task Execute_Throws_WhenStackIrLabelsNotUnique()
    {
        var ctx = BuildMinimalContext();
        ctx.StackIr.Insert(0, new StackInstruction(OpCode.Label, "entry")); // duplicate "entry"

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("stack ir labels unique", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenBranchTargetUnresolved()
    {
        var ctx = BuildMinimalContext();
        // Replace the Br "exit" with a branch to an undefined label.
        var idx = ctx.StackIr.FindIndex(i => i.Op == OpCode.Br && i.Operand == "exit");
        ctx.StackIr[idx] = new StackInstruction(OpCode.Br, "nowhere");

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("stack ir branches resolve", ex.Message);
    }

    [Fact]
    public async Task Execute_Throws_WhenStackIrDoesNotEndWithRet()
    {
        var ctx = BuildMinimalContext();
        ctx.StackIr.RemoveAll(i => i.Op == OpCode.Ret);
        ctx.StackIr.Add(new StackInstruction(OpCode.Br, "entry")); // ends with Br, not Ret

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("stack ir ends with ret", ex.Message);
    }

    // ── Artifact written even on failure ──────────────────────────────────────

    [Fact]
    public async Task Execute_WritesArtifact_EvenWhenCheckFails()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var ctx = new CompilationContext
            {
                SpecPath     = "fake.spec",
                ArtifactsDir = tmpDir,
            };
            // Add two program nodes — will fail invariant 1.
            var graph = new SemanticGraph();
            graph.Add(new ProgramNode(Guid.NewGuid(), "Program:A", "A"));
            graph.Add(new ProgramNode(Guid.NewGuid(), "Program:B", "B"));
            ctx.SemanticGraph = graph;

            var (blocks, _, _) = BuildMinimalCfg();
            ctx.CfgBlocks = blocks;
            ctx.StackIr   = BuildMinimalStackIr();

            await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));

            Assert.True(File.Exists(Path.Combine(tmpDir, "08-validation.json")));
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    [Fact]
    public async Task Execute_ArtifactContainsFailedChecks_WhenFailing()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var ctx = new CompilationContext
            {
                SpecPath     = "fake.spec",
                ArtifactsDir = tmpDir,
            };
            var graph = new SemanticGraph();
            graph.Add(new ProgramNode(Guid.NewGuid(), "Program:A", "A"));
            graph.Add(new ProgramNode(Guid.NewGuid(), "Program:B", "B"));
            ctx.SemanticGraph = graph;
            var (blocks, _, _) = BuildMinimalCfg();
            ctx.CfgBlocks = blocks;
            ctx.StackIr   = BuildMinimalStackIr();

            await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));

            var json = await File.ReadAllTextAsync(Path.Combine(tmpDir, "08-validation.json"));
            Assert.Contains("\"passed\": false", json);
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    // ── LoadFromArtifactAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task LoadFromArtifact_RestoresValidationReport()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Run pass to produce artifact.
            var runCtx = new CompilationContext
            {
                SpecPath     = "fake.spec",
                ArtifactsDir = tmpDir,
            };
            var (graph, _, _, _, _) = BuildMinimalGraph();
            runCtx.SemanticGraph = graph;
            runCtx.CfgBlocks     = BuildMinimalCfg().Blocks;
            runCtx.StackIr       = BuildMinimalStackIr();
            await MakePass().ExecuteAsync(runCtx);

            // Load from artifact into a fresh context.
            var loadCtx      = PipelineFixtures.MakeContext();
            var artifactPath = Path.Combine(tmpDir, "08-validation.json");
            await MakePass().LoadFromArtifactAsync(artifactPath, loadCtx);

            Assert.NotNull(loadCtx.ValidationReport);
            Assert.True(loadCtx.ValidationReport!.Passed);
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    [Fact]
    public async Task LoadFromArtifact_Throws_WhenReportPassedFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Write a report with passed=false.
            var failReport = new ValidationReport(false,
            [
                new ValidationCheck("single program node", false, "test"),
            ]);
            var json = System.Text.Json.JsonSerializer.Serialize(failReport,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented          = true,
                    PropertyNamingPolicy   = System.Text.Json.JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });
            var artifactPath = Path.Combine(tmpDir, "08-validation.json");
            await File.WriteAllTextAsync(artifactPath, json);

            var ctx = PipelineFixtures.MakeContext();
            var ex  = await Assert.ThrowsAsync<CompilationException>(
                () => MakePass().LoadFromArtifactAsync(artifactPath, ctx));
            Assert.Contains("Prior validation failed", ex.Message);
            Assert.Contains("single program node", ex.Message);
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    [Fact]
    public async Task LoadFromArtifact_SetsReport_WhenPassedTrue()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var passReport = new ValidationReport(true,
            [
                new ValidationCheck("single program node", true),
            ]);
            var json = System.Text.Json.JsonSerializer.Serialize(passReport,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented          = true,
                    PropertyNamingPolicy   = System.Text.Json.JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });
            var artifactPath = Path.Combine(tmpDir, "08-validation.json");
            await File.WriteAllTextAsync(artifactPath, json);

            var ctx = PipelineFixtures.MakeContext();
            await MakePass().LoadFromArtifactAsync(artifactPath, ctx);

            Assert.NotNull(ctx.ValidationReport);
            Assert.True(ctx.ValidationReport!.Passed);
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    // ── AssertionNode exclusion from reachability check ───────────────────────

    [Fact]
    public async Task Execute_PassesReachability_WhenAssertionNodeOnlyReachableViaAssertsEdge()
    {
        var ctx = BuildMinimalContext();
        // Add an AssertionNode connected via Asserts edge — should NOT be in reachability check.
        var prog    = ctx.SemanticGraph!.Nodes.OfType<ProgramNode>().Single();
        var assertion = new AssertionNode(Guid.NewGuid(), "Assert:1=1", 1, "1");
        ctx.SemanticGraph.Add(assertion);
        ctx.SemanticGraph.Edges.Add(new Edge(Guid.NewGuid(), prog.Id, assertion.Id, EdgeType.Asserts));

        // Should pass — AssertionNodes are excluded from the BFS reachability check.
        await MakePass().ExecuteAsync(ctx);
        Assert.True(ctx.ValidationReport!.Passed);
    }

    // ── Array/Index/Swap/NestedLoop graph invariants ──────────────────────────

    /// <summary>
    /// Builds a minimal context with all four new node types properly connected.
    /// ArrayNode "arr", IndexNode referencing "arr", SwapNode referencing "arr",
    /// NestedLoopNode whose BoundExpr "n-1" references VariableNode "n".
    /// </summary>
    private static CompilationContext BuildContextWithNewNodes()
    {
        var graph  = new SemanticGraph();
        var prog   = new ProgramNode(Guid.NewGuid(), "Program:Test", "Test");
        var arr    = new ArrayNode(Guid.NewGuid(), "Array:arr[10]", "arr", "int", 10);
        var idx    = new IndexNode(Guid.NewGuid(), "Index:arr[i]", "arr", "i");
        var swap   = new SwapNode(Guid.NewGuid(), "Swap:arr[j↔j+1]", "arr", "j", "j+1");
        var varN   = new VariableNode(Guid.NewGuid(), "Var:n", "n", "int");
        var nested = new NestedLoopNode(Guid.NewGuid(), "NestedLoop:j<n-1", "j", 0, "n-1");
        var branch = new BranchNode(Guid.NewGuid(), "Branch:cond", "cond");
        var print  = new PrintNode(Guid.NewGuid(), "Print:done", "done");

        graph.Add(prog);
        graph.Add(arr);
        graph.Add(idx);
        graph.Add(swap);
        graph.Add(varN);
        graph.Add(nested);
        graph.Add(branch);
        graph.Add(print);

        // Connect everything reachable from prog
        graph.Connect(prog.Id,   arr.Id,    EdgeType.Contains);
        graph.Connect(prog.Id,   varN.Id,   EdgeType.Contains);
        graph.Connect(arr.Id,    idx.Id,    EdgeType.Contains);
        graph.Connect(arr.Id,    swap.Id,   EdgeType.Contains);
        graph.Connect(prog.Id,   nested.Id, EdgeType.Contains);
        graph.Connect(prog.Id,   branch.Id, EdgeType.Contains);
        graph.Connect(branch.Id, print.Id,  EdgeType.TrueBranch);

        var (blocks, _, _) = BuildMinimalCfg();
        var ctx = PipelineFixtures.MakeContext();
        ctx.SemanticGraph = graph;
        ctx.CfgBlocks     = blocks;
        ctx.StackIr       = BuildMinimalStackIr();
        return ctx;
    }

    [Fact]
    public async Task Execute_Passes_WhenGraphHasArrayIndexSwapNodes()
    {
        var ctx  = BuildContextWithNewNodes();
        var pass = MakePass();
        await pass.ExecuteAsync(ctx);

        Assert.NotNull(ctx.ValidationReport);
        Assert.True(ctx.ValidationReport!.Passed);
    }

    [Fact]
    public async Task Execute_Fails_WhenIndexNodeReferencesUnknownArray()
    {
        var ctx    = BuildContextWithNewNodes();
        var graph  = ctx.SemanticGraph!;

        // Replace the IndexNode with one referencing a non-existent array
        var idx = graph.Nodes.OfType<IndexNode>().Single();
        graph.Nodes.Remove(idx);
        var badIdx = new IndexNode(idx.Id, idx.Label, "nonexistent", "i");
        graph.Nodes.Add(badIdx);

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("index nodes reference existing array", ex.Message);
    }

    [Fact]
    public async Task Execute_Fails_WhenSwapNodeReferencesUnknownArray()
    {
        var ctx   = BuildContextWithNewNodes();
        var graph = ctx.SemanticGraph!;

        // Replace the SwapNode with one referencing a non-existent array
        var swap    = graph.Nodes.OfType<SwapNode>().Single();
        graph.Nodes.Remove(swap);
        var badSwap = new SwapNode(swap.Id, swap.Label, "nonexistent", "j", "j+1");
        graph.Nodes.Add(badSwap);

        var ex = await Assert.ThrowsAsync<CompilationException>(() => MakePass().ExecuteAsync(ctx));
        Assert.Contains("swap nodes reference existing array", ex.Message);
    }

    [Fact]
    public async Task Execute_Passes_WhenNoArrayNodesPresent()
    {
        // A graph with no ArrayNode/IndexNode/SwapNode/NestedLoopNode — checks 6-8 pass trivially.
        var ctx = BuildMinimalContext();
        await MakePass().ExecuteAsync(ctx);
        Assert.True(ctx.ValidationReport!.Passed);
    }

    // ── Name ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Name_IsCorrect()
    {
        Assert.Equal("08-Validation", MakePass().Name);
    }

    [Fact]
    public void ArtifactFile_IsCorrect()
    {
        Assert.Equal("08-validation.json", MakePass().ArtifactFile);
    }
}
