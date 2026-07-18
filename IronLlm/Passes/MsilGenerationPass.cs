using System.Diagnostics;
using System.Text;
using IronLlm.Graph;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

public class MsilGenerationPass : ICompilerPass
{
    private readonly ILogger<MsilGenerationPass> _logger;

    public MsilGenerationPass(ILogger<MsilGenerationPass> logger)
    {
        _logger = logger;
    }

    public string Name          => "06-MSIL";
    public string? ArtifactFile  => "06-program.il";

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        context.MsilOutput = await File.ReadAllTextAsync(artifactPath);
    }

    public Task ExecuteAsync(CompilationContext context)
    {
        if (context.StackIr.Count == 0)
            throw new InvalidOperationException("StackIr not set");

        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();

        var programName = context.SemanticGraph?
            .Nodes.OfType<IronLlm.Graph.ProgramNode>().FirstOrDefault()?.Name
            ?? "Program";

        // ── Collect locals in first-appearance order ──────────────────────────
        // Array locals (LdlocA/StlocA), string locals (LdlocStr/StlocStr), and
        // scalar int locals (LdlocS/StlocS). We keep insertion order so the IL
        // index matches declaration order.
        var localOrder   = new List<string>();    // name → index via position
        var arrayLocals  = new HashSet<string>(); // names that are int[] type
        var stringLocals = new HashSet<string>(); // names that are string type

        foreach (var instr in context.StackIr)
        {
            switch (instr.Op)
            {
                case OpCode.StlocS:
                case OpCode.LdlocS:
                    if (instr.Operand != null && !localOrder.Contains(instr.Operand))
                        localOrder.Add(instr.Operand);
                    break;
                case OpCode.LdlocA:
                case OpCode.StlocA:
                    if (instr.Operand != null)
                    {
                        arrayLocals.Add(instr.Operand);
                        if (!localOrder.Contains(instr.Operand))
                            localOrder.Add(instr.Operand);
                    }
                    break;
                case OpCode.LdlocStr:
                case OpCode.StlocStr:
                    if (instr.Operand != null)
                    {
                        stringLocals.Add(instr.Operand);
                        if (!localOrder.Contains(instr.Operand))
                            localOrder.Add(instr.Operand);
                    }
                    break;
            }
        }

        // ── Emit IL header ────────────────────────────────────────────────────
        sb.AppendLine($".assembly extern mscorlib {{}}");
        sb.AppendLine($".assembly {programName} {{}}");
        sb.AppendLine($".class public {programName} extends [mscorlib]System.Object {{");
        sb.AppendLine("  .method public static void Main() cil managed {");
        sb.AppendLine("    .entrypoint");
        sb.AppendLine("    .maxstack 8");

        // ── Emit .locals init ─────────────────────────────────────────────────
        if (localOrder.Count == 0)
        {
            sb.AppendLine("    .locals init ()");
        }
        else if (localOrder.Count == 1)
        {
            var name = localOrder[0];
            var type = LocalType(name, arrayLocals, stringLocals);
            sb.AppendLine($"    .locals init ({type} V_0)");
        }
        else
        {
            sb.AppendLine("    .locals init (");
            for (var i = 0; i < localOrder.Count; i++)
            {
                var name    = localOrder[i];
                var type    = LocalType(name, arrayLocals, stringLocals);
                var comma   = i < localOrder.Count - 1 ? "," : "";
                sb.AppendLine($"        {type} V_{i}{comma}");
            }
            sb.AppendLine("    )");
        }

        sb.AppendLine();

        // ── Emit instructions ─────────────────────────────────────────────────
        foreach (var instr in context.StackIr)
        {
            string line;
            if (instr.Op == OpCode.LdlocS || instr.Op == OpCode.LdlocA)
            {
                var idx = localOrder.IndexOf(instr.Operand!);
                line = $"    ldloc.{idx}";
            }
            else if (instr.Op == OpCode.StlocS || instr.Op == OpCode.StlocA)
            {
                var idx = localOrder.IndexOf(instr.Operand!);
                line = $"    stloc.{idx}";
            }
            else if (instr.Op == OpCode.LdlocStr)
            {
                var idx = localOrder.IndexOf(instr.Operand!);
                line = $"    ldloc.{idx}";
            }
            else if (instr.Op == OpCode.StlocStr)
            {
                var idx = localOrder.IndexOf(instr.Operand!);
                line = $"    stloc.{idx}";
            }
            else
            {
                line = instr.Op switch
                {
                    OpCode.Label    => $"  {instr.Operand}:",
                    OpCode.LdcI4    => $"    ldc.i4 {instr.Operand}",
                    OpCode.Add      => $"    add",
                    OpCode.Sub      => $"    sub",
                    OpCode.Mul      => $"    mul",
                    OpCode.Rem      => $"    rem",
                    OpCode.Div      => $"    div",
                    OpCode.Ceq      => $"    ceq",
                    OpCode.Cgt      => $"    cgt",
                    OpCode.Clt      => $"    clt",
                    OpCode.Newarr   => $"    newarr [mscorlib]System.Int32",
                    OpCode.LdelemI4 => $"    ldelem.i4",
                    OpCode.StelemI4 => $"    stelem.i4",
                    OpCode.Brfalse  => $"    brfalse {instr.Operand}",
                    OpCode.Brtrue   => $"    brtrue {instr.Operand}",
                    OpCode.Br       => $"    br {instr.Operand}",
                    OpCode.LdstrS     => $"    ldstr \"{instr.Operand}\"",
                    OpCode.Intrinsic  => $"    {IronLlm.Graph.IntrinsicLibrary.Get(instr.Operand!).IlText}",
                    OpCode.Ret        => $"    ret",
                    _               => $"    // unknown: {instr.Op}",
                };
            }
            sb.AppendLine(line);
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");

        context.MsilOutput = sb.ToString();

        var lineCount = context.MsilOutput.Count(c => c == '\n');
        _logger.LogDebug("IL: {Lines} lines, entry point Main(), {LocalCount} locals",
            lineCount, localOrder.Count);
        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
        return Task.CompletedTask;
    }

    private static string LocalType(string name, HashSet<string> arrayLocals, HashSet<string> stringLocals)
    {
        if (arrayLocals.Contains(name))  return "int32[]";
        if (stringLocals.Contains(name)) return "string";
        return "int32";
    }
}
