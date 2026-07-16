using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using IronLlm.Graph;
using Microsoft.Extensions.Logging;
using IrOp = IronLlm.Graph.OpCode;   // alias to avoid clash with System.Reflection.Emit.OpCode

namespace IronLlm.Passes;

// Translates StackIR directly into a runnable .NET assembly using
// PersistedAssemblyBuilder — no external tools required.
// Produces:
//   07-program.dll            — the managed PE (framework-dependent)
//   07-program.runtimeconfig.json
//   {ProgramName}             — apphost-patched native launcher, directly executable
[ExcludeFromCodeCoverage(Justification = "PE emit + apphost filesystem I/O; covered by scripts/test.sh")]
public class AssemblyEmitPass : ICompilerPass
{
    private readonly ILogger<AssemblyEmitPass> _logger;

    public AssemblyEmitPass(ILogger<AssemblyEmitPass> logger)
    {
        _logger = logger;
    }

    public string Name          => "07-Assembly";
    public string? ArtifactFile  => "07-program.dll";

    public async Task ExecuteAsync(CompilationContext context)
    {
        if (context.StackIr.Count == 0)
            throw new InvalidOperationException("StackIr not set");

        var sw = Stopwatch.StartNew();
        var programName = context.SemanticGraph?.Nodes
            .OfType<ProgramNode>().FirstOrDefault()?.Name ?? "Program";

        var asmName    = new AssemblyName(programName);
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule(programName);
        var typBuilder = modBuilder.DefineType(
            programName,
            TypeAttributes.Public | TypeAttributes.Class);

        var methBuilder = typBuilder.DefineMethod(
            "Main",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            Type.EmptyTypes);

        var il = methBuilder.GetILGenerator();
        il.DeclareLocal(typeof(int));   // local 0 = n

        // All labels must be defined before any are marked
        var labels = new Dictionary<string, Label>();
        foreach (var instr in context.StackIr)
            if (instr.Op == IrOp.Label && instr.Operand != null)
                labels[instr.Operand] = il.DefineLabel();

        var writeLineInt    = typeof(Console).GetMethod("WriteLine", [typeof(int)])!;
        var writeLineString = typeof(Console).GetMethod("WriteLine", [typeof(string)])!;

        foreach (var instr in context.StackIr)
        {
            switch (instr.Op)
            {
                case IrOp.Label:
                    il.MarkLabel(labels[instr.Operand!]);
                    break;
                case IrOp.LdcI4:
                    il.Emit(OpCodes.Ldc_I4, int.Parse(instr.Operand!));
                    break;
                case IrOp.LdlocS:
                    il.Emit(OpCodes.Ldloc_0);
                    break;
                case IrOp.StlocS:
                    il.Emit(OpCodes.Stloc_0);
                    break;
                case IrOp.Add:
                    il.Emit(OpCodes.Add);
                    break;
                case IrOp.Rem:
                    il.Emit(OpCodes.Rem);
                    break;
                case IrOp.Ceq:
                    il.Emit(OpCodes.Ceq);
                    break;
                case IrOp.Cgt:
                    il.Emit(OpCodes.Cgt);
                    break;
                case IrOp.Brfalse:
                    il.Emit(OpCodes.Brfalse, labels[instr.Operand!]);
                    break;
                case IrOp.Brtrue:
                    il.Emit(OpCodes.Brtrue, labels[instr.Operand!]);
                    break;
                case IrOp.Br:
                    il.Emit(OpCodes.Br, labels[instr.Operand!]);
                    break;
                case IrOp.LdstrS:
                    il.Emit(OpCodes.Ldstr, instr.Operand!);
                    break;
                case IrOp.Call:
                    var m = instr.Operand!.Contains("string") ? writeLineString : writeLineInt;
                    il.EmitCall(OpCodes.Call, m, null);
                    break;
                case IrOp.Ret:
                    il.Emit(OpCodes.Ret);
                    break;
            }
        }

        typBuilder.CreateType();

        // The two-argument overload returns the assembly's MetadataBuilder directly.
        // (The three-argument overload's third out param is PDB metadata — not what we want here.)
        var metadataBuilder = asmBuilder.GenerateMetadata(out var ilBlob, out var mappedFieldData);

        // Locate the Main method's definition handle from its metadata token.
        var entryHandle = MetadataTokens.MethodDefinitionHandle(methBuilder.MetadataToken);

        var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage);
        var peBuilder = new ManagedPEBuilder(
            peHeaderBuilder,
            new MetadataRootBuilder(metadataBuilder),
            ilBlob,
            entryPoint: entryHandle,
            flags: CorFlags.ILOnly);

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);

        var outPath = Path.Combine(context.ArtifactsDir, "07-program.dll");
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        peBlob.WriteContentTo(fs);

        // Write the runtimeconfig.json required by the dotnet host to locate the framework.
        var runtimeVersion = Environment.Version;
        var runtimeConfig = $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{runtimeVersion.Major}}.{{runtimeVersion.Minor}}",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{runtimeVersion.Major}}.{{runtimeVersion.Minor}}.0"
                }
              }
            }
            """;
        var configPath = Path.ChangeExtension(outPath, ".runtimeconfig.json");
        await File.WriteAllTextAsync(configPath, runtimeConfig);

        // Write the native launcher so the program is directly executable.
        var dllFilename  = Path.GetFileName(outPath);   // "07-program.dll"
        var launcherPath = Path.Combine(context.ArtifactsDir, programName);
        WriteAppHost(dllFilename, launcherPath);

        context.AssemblyPath  = outPath;
        context.LauncherPath  = launcherPath;

        _logger.LogDebug("PE blob: {Bytes} bytes, runtimeconfig: {Config}", peBlob.Count, configPath);
        _logger.LogInformation("Apphost: {Path}", launcherPath);
        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
    }

    // Patches the .NET apphost stub with the managed dll filename and writes
    // it as a chmod +x executable — the same thing `dotnet publish` does.
    private static void WriteAppHost(string dllFilename, string outputPath)
    {
        var apphostPath = FindAppHost()
            ?? throw new InvalidOperationException(
                "Could not locate the .NET apphost binary. " +
                "Ensure the Microsoft.NETCore.App.Host pack is installed.");

        var template    = File.ReadAllBytes(apphostPath);
        var placeholder = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2"u8.ToArray();
        var fieldSize   = 1024;

        int placeholderIdx = IndexOf(template, placeholder);
        if (placeholderIdx < 0)
            throw new InvalidOperationException("apphost placeholder not found in template binary.");

        // Replace the placeholder field with the dll filename, null-padded to fieldSize bytes.
        var patched      = (byte[])template.Clone();
        var nameBytes    = System.Text.Encoding.UTF8.GetBytes(dllFilename);
        if (nameBytes.Length + 1 > fieldSize)
            throw new InvalidOperationException($"DLL filename too long for apphost field ({nameBytes.Length} bytes).");

        Array.Clear(patched, placeholderIdx, fieldSize);
        nameBytes.CopyTo(patched, placeholderIdx);   // null terminator already present from Clear

        File.WriteAllBytes(outputPath, patched);

        // Mark executable (rwxr-xr-x)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(outputPath,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string? FindAppHost()
    {
        // Walk the installed host packs, prefer the version matching the running runtime.
        var runtimeVersion = Environment.Version;
        var packsRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)!)!,
            "..", "..", "packs");
        packsRoot = Path.GetFullPath(packsRoot);

        if (!Directory.Exists(packsRoot))
            return null;

        // RID-specific: e.g. ubuntu.24.04-x64
        var rid = RuntimeInformation.RuntimeIdentifier;

        // Gather all apphost candidates and prefer the best version match.
        var candidates = Directory.GetFiles(packsRoot, "apphost", SearchOption.AllDirectories);

        string? best = null;
        var bestScore = -1;

        foreach (var c in candidates)
        {
            var score = 0;
            if (c.Contains(rid))                                           score += 2;
            if (c.Contains($"{runtimeVersion.Major}.{runtimeVersion.Minor}")) score += 1;
            if (score > bestScore) { bestScore = score; best = c; }
        }

        return best;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    public Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        context.AssemblyPath = artifactPath;
        // Infer the launcher path from the dll path
        var dir         = Path.GetDirectoryName(artifactPath)!;
        var programName = context.SemanticGraph?.Nodes
            .OfType<ProgramNode>().FirstOrDefault()?.Name ?? "Program";
        context.LauncherPath = Path.Combine(dir, programName);
        return Task.CompletedTask;
    }
}
