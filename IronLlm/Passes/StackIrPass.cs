using System.Diagnostics;
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

    // Maps CFG pseudo-instructions to stack ops by recognising common patterns.
    // Patterns are intentionally broad — the LLM's exact phrasing varies across runs.
    private static IEnumerable<StackInstruction> LowerInstruction(string instr)
    {
        var s = instr.Trim();

        // "n = 1" — loop variable initialisation
        if (s.Contains("n =") && !s.Contains("n = n"))
        {
            var digits = new string(s.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var initVal))
            {
                yield return new(OpCode.LdcI4, initVal.ToString());
                yield return new(OpCode.StlocS, "n");
            }
            yield break;
        }

        // "n > 100" / "check n <= 100" — loop test
        if (s.Contains("100") && (s.Contains("<=") || s.Contains(">") || s.Contains("check n")))
        {
            yield return new(OpCode.LdlocS, "n");
            yield return new(OpCode.LdcI4, "100");
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "n % 15 == 0" — divisibility check
        if (s.Contains('%') && s.Contains("== 0"))
        {
            var afterMod   = s[(s.IndexOf('%') + 1)..];
            var divisorStr = new string(afterMod.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(divisorStr, out var d))
            {
                yield return new(OpCode.LdlocS, "n");
                yield return new(OpCode.LdcI4, d.ToString());
                yield return new(OpCode.Rem);
                yield return new(OpCode.LdcI4, "0");
                yield return new(OpCode.Ceq);
            }
            yield break;
        }

        // "print {n}" / "print n" — print the integer variable
        if (s.StartsWith("print") && (s.Contains("{n}") || s.TrimEnd().EndsWith(" n")))
        {
            yield return new(OpCode.LdlocS, "n");
            yield return new(OpCode.Call, "Console.WriteLine(int)");
            yield break;
        }

        // "print "FizzBuzz"" — print a string literal
        if (s.StartsWith("print "))
        {
            var arg = s["print ".Length..].Trim().Trim('"');
            yield return new(OpCode.LdstrS, arg);
            yield return new(OpCode.Call, "Console.WriteLine(string)");
            yield break;
        }

        // "n = n + 1" / "n++" / "increment"
        if (s.Contains("n + 1") || s.Contains("n++") || s.Contains("increment"))
        {
            yield return new(OpCode.LdlocS, "n");
            yield return new(OpCode.LdcI4, "1");
            yield return new(OpCode.Add);
            yield return new(OpCode.StlocS, "n");
            yield break;
        }
    }
}
