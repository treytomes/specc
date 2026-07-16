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

        sb.AppendLine(".assembly FizzBuzz {}");
        sb.AppendLine(".class public FizzBuzz {");
        sb.AppendLine("  .method public static void Main() cil managed {");
        sb.AppendLine("    .entrypoint");
        sb.AppendLine("    .locals init (int32 V_0)");
        sb.AppendLine();

        foreach (var instr in context.StackIr)
        {
            var line = instr.Op switch
            {
                OpCode.Label    => $"  {instr.Operand}:",
                OpCode.LdcI4    => $"    ldc.i4 {instr.Operand}",
                OpCode.LdlocS   => $"    ldloc.0",
                OpCode.StlocS   => $"    stloc.0",
                OpCode.Add      => $"    add",
                OpCode.Rem      => $"    rem",
                OpCode.Ceq      => $"    ceq",
                OpCode.Cgt      => $"    cgt",
                OpCode.Brfalse  => $"    brfalse {instr.Operand}",
                OpCode.Brtrue   => $"    brtrue {instr.Operand}",
                OpCode.Br       => $"    br {instr.Operand}",
                OpCode.LdstrS   => $"    ldstr \"{instr.Operand}\"",
                OpCode.Call when instr.Operand!.Contains("string")
                                => $"    call void [mscorlib]System.Console::WriteLine(string)",
                OpCode.Call     => $"    call void [mscorlib]System.Console::WriteLine(int32)",
                OpCode.Ret      => $"    ret",
                _               => $"    // unknown: {instr.Op}",
            };
            sb.AppendLine(line);
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");

        context.MsilOutput = sb.ToString();

        var lineCount = context.MsilOutput.Count(c => c == '\n');
        _logger.LogDebug("IL: {Lines} lines, entry point Main()", lineCount);
        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
        return Task.CompletedTask;
    }
}
