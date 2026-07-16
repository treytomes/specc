using System.Diagnostics;
using System.Text.RegularExpressions;
using IronLlm.Graph;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

public class StackIrPass : ICompilerPass
{
    private readonly ILogger<StackIrPass> _logger;

    public StackIrPass(ILogger<StackIrPass> logger)
    {
        _logger = logger;
    }

    public string Name          => "05-StackIR";
    public string? ArtifactFile  => "05-stackir.json";

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json = await File.ReadAllTextAsync(artifactPath);
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        context.StackIr = System.Text.Json.JsonSerializer.Deserialize<List<StackInstruction>>(json, opts) ?? [];
    }

    public Task ExecuteAsync(CompilationContext context)
    {
        if (context.CfgBlocks.Count == 0)
            throw new InvalidOperationException("CfgBlocks not set");

        var sw = Stopwatch.StartNew();
        var ir = new List<StackInstruction>();

        foreach (var block in context.CfgBlocks)
        {
            ir.Add(new StackInstruction(OpCode.Label, block.Label));

            var blockOps = new List<StackInstruction>();
            foreach (var instr in block.Instructions)
            {
                var lowered = LowerInstruction(instr).ToList();
                if (lowered.Count == 0 && !string.IsNullOrWhiteSpace(instr))
                    _logger.LogWarning(
                        "StackIR: unrecognised instruction pattern in block {Block}: \"{Instruction}\"",
                        block.Label, instr);
                blockOps.AddRange(lowered);
            }

            ir.AddRange(blockOps);

            var lastOp = blockOps.LastOrDefault()?.Op;

            if (block.SuccessorFalse != null)
            {
                if (lastOp == OpCode.Cgt)
                    ir.Add(new StackInstruction(OpCode.Brtrue,  block.SuccessorFalse));
                else
                    ir.Add(new StackInstruction(OpCode.Brfalse, block.SuccessorFalse));

                if (block.SuccessorTrue != null)
                    ir.Add(new StackInstruction(OpCode.Br, block.SuccessorTrue));
            }
            else if (block.SuccessorTrue != null && block.Label != "exit")
            {
                ir.Add(new StackInstruction(OpCode.Br, block.SuccessorTrue));
            }

            _logger.LogDebug("Block {Label}: {Count} ops", block.Label, blockOps.Count);
        }

        ir.Add(new StackInstruction(OpCode.Ret));
        context.StackIr = ir;

        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms — {Count} instructions",
            Name, sw.ElapsedMilliseconds, ir.Count);
        return Task.CompletedTask;
    }

    // Maps deterministic CFG instruction strings to stack ops.
    // CfgPass emits exact patterns; we match them precisely and extract values via regex.
    private static IEnumerable<StackInstruction> LowerInstruction(string instr)
    {
        var s = instr.Trim();

        // "{var} = {int}" — loop variable init, e.g. "n = 1"
        // Must not match "{var} = {var} + 1" (increment).
        var initMatch = Regex.Match(s, @"^(\w+)\s*=\s*(-?\d+)$");
        if (initMatch.Success)
        {
            yield return new(OpCode.LdcI4,  initMatch.Groups[2].Value);
            yield return new(OpCode.StlocS, initMatch.Groups[1].Value);
            yield break;
        }

        // "if {var} > {int} goto exit" — loop termination test
        var loopTestMatch = Regex.Match(s, @"^if\s+(\w+)\s*>\s*(-?\d+)\s+goto\s+\w+$");
        if (loopTestMatch.Success)
        {
            yield return new(OpCode.LdlocS, loopTestMatch.Groups[1].Value);
            yield return new(OpCode.LdcI4,  loopTestMatch.Groups[2].Value);
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "if {var} % {int} == 0" — divisibility check
        var modMatch = Regex.Match(s, @"^if\s+(\w+)\s*%\s*(-?\d+)\s*==\s*0$");
        if (modMatch.Success)
        {
            yield return new(OpCode.LdlocS, modMatch.Groups[1].Value);
            yield return new(OpCode.LdcI4,  modMatch.Groups[2].Value);
            yield return new(OpCode.Rem);
            yield return new(OpCode.LdcI4,  "0");
            yield return new(OpCode.Ceq);
            yield break;
        }

        // "print {var}" — print integer variable (no quotes, ends with identifier)
        var printVarMatch = Regex.Match(s, @"^print\s+(\w+)$");
        if (printVarMatch.Success)
        {
            yield return new(OpCode.LdlocS, printVarMatch.Groups[1].Value);
            yield return new(OpCode.Call,   "Console.WriteLine(int)");
            yield break;
        }

        // "print "...string..."" — print string literal
        var printStrMatch = Regex.Match(s, @"^print\s+""(.*)""$");
        if (printStrMatch.Success)
        {
            yield return new(OpCode.LdstrS, printStrMatch.Groups[1].Value);
            yield return new(OpCode.Call,   "Console.WriteLine(string)");
            yield break;
        }

        // "{var} = {var} + 1" — loop increment
        var incrMatch = Regex.Match(s, @"^(\w+)\s*=\s*(\w+)\s*\+\s*1$");
        if (incrMatch.Success)
        {
            yield return new(OpCode.LdlocS, incrMatch.Groups[2].Value);
            yield return new(OpCode.LdcI4,  "1");
            yield return new(OpCode.Add);
            yield return new(OpCode.StlocS, incrMatch.Groups[1].Value);
            yield break;
        }
    }
}
