using IronLlm.Passes;
using Microsoft.Extensions.AI;

const string OllamaBase = "http://localhost:11434";
const string EmbedModel = "mxbai-embed-large:latest";
const string ChatModel  = "ministral-3:3b";

var specPath = Path.GetFullPath(
    args.Length > 0 ? args[0] : "examples/FizzBuzz/FizzBuzz.spec");

var artifactsDir = Path.GetFullPath(
    args.Length > 1 ? args[1] : "examples/FizzBuzz/artifacts");

Console.WriteLine("IronLlm — spec compiler");
Console.WriteLine($"Spec:      {specPath}");
Console.WriteLine($"Artifacts: {artifactsDir}");
Console.WriteLine();

Directory.CreateDirectory(artifactsDir);

var embedder = new OllamaEmbeddingGenerator(new Uri(OllamaBase), EmbedModel);
var chat     = new OllamaChatClient(new Uri(OllamaBase), ChatModel);

var context = new CompilationContext
{
    SpecPath     = specPath,
    ArtifactsDir = artifactsDir,
    Embedder     = embedder,
    ChatClient   = chat,
};

ICompilerPass[] passes =
[
    new ParseSpecPass(),
    new SemanticGraphPass(),
    new EmbeddingPass(),
    new CfgPass(),
    new StackIrPass(),
    new MsilGenerationPass(),
    new AssemblyEmitPass(),
];

foreach (var pass in passes)
{
    var artifactPath = pass.ArtifactFile is { } f
        ? Path.Combine(artifactsDir, f)
        : null;

    if (artifactPath != null && File.Exists(artifactPath))
    {
        Console.WriteLine($"[{pass.Name}] skipped (artifact exists)");
        await pass.LoadFromArtifactAsync(artifactPath, context);
        continue;
    }

    Console.Write($"[{pass.Name}] ... ");
    await pass.ExecuteAsync(context);
    Console.WriteLine("done");

    // Write artifact immediately after the pass succeeds
    await ArtifactWriter.WritePassArtifactAsync(pass, context);
}

Console.WriteLine();
Console.WriteLine("Compilation complete.");

if (context.LauncherPath != null)
    Console.WriteLine($"Executable: {context.LauncherPath}");
else if (context.AssemblyPath != null)
    Console.WriteLine($"Assembly:   {context.AssemblyPath}");
