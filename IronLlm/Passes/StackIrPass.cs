using IronLlm.Graph;

namespace IronLlm.Passes;

// Deterministic lowering: CFG basic blocks → stack-machine instructions.
// No LLM. Each pseudo-instruction in a block is pattern-matched to stack ops.
public class StackIrPass : ICompilerPass
{
    public string Name          => "05-StackIR";
    public string? ArtifactFile  => "05-stackir.json";

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json = await File.ReadAllTextAsync(artifactPath);
        var opts = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        context.StackIr = System.Text.Json.JsonSerializer.Deserialize<List<StackInstruction>>(json, opts) ?? [];
    }

    public Task ExecuteAsync(CompilationContext context)
    {
        if (context.CfgBlocks.Count == 0)
            throw new InvalidOperationException("CfgBlocks not set");

        var ir = new List<StackInstruction>();

        foreach (var block in context.CfgBlocks)
        {
            ir.Add(new StackInstruction(OpCode.Label, block.Label));

            var blockOps = block.Instructions.SelectMany(LowerInstruction).ToList();
            ir.AddRange(blockOps);

            var lastOp = blockOps.LastOrDefault()?.Op;

            if (block.SuccessorFalse != null)
            {
                // cgt leaves 1 on stack when condition is true (n > limit).
                // Branch to the "false" (exit) successor when cgt result is true.
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
        }

        ir.Add(new StackInstruction(OpCode.Ret));
        context.StackIr = ir;
        return Task.CompletedTask;
    }

    // Maps CFG pseudo-instructions to stack ops by recognising common patterns.
    // Patterns are intentionally broad — the LLM's exact phrasing varies across runs.
    private static IEnumerable<StackInstruction> LowerInstruction(string instr)
    {
        var s = instr.Trim();

        // "init n = 1" / "n = 1" — loop variable initialisation
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

        // "check n <= 100" / "n > 100" / "loop_start: check n <= 100" — loop test
        if (s.Contains("100") && (s.Contains("<=") || s.Contains(">") || s.Contains("check n")))
        {
            yield return new(OpCode.LdlocS, "n");
            yield return new(OpCode.LdcI4, "100");
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "n % 15 == 0" / "check divisible_by_15: n % 15 == 0" — divisibility check
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

        // "print "FizzBuzz"" / "print Fizz" — print a string literal
        if (s.StartsWith("print "))
        {
            var arg = s["print ".Length..].Trim().Trim('"');
            yield return new(OpCode.LdstrS, arg);
            yield return new(OpCode.Call, "Console.WriteLine(string)");
            yield break;
        }

        // "n = n + 1" / "increment n: n = n + 1" / "n++"
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
