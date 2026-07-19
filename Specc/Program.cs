using System.CommandLine;
using Specc.Passes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// ── .env config (Spec 08) ────────────────────────────────────────────────────
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var dotEnv   = LoadDotEnv(Path.Combine(repoRoot, ".env"));

var ollamaBase = Cfg(dotEnv, "IRONLLM_OLLAMA_BASE", "http://localhost:11434");
var embedModel = Cfg(dotEnv, "IRONLLM_EMBED_MODEL",  "mxbai-embed-large:latest");
var chatModel  = Cfg(dotEnv, "IRONLLM_CHAT_MODEL",   "ministral-3:3b");

// ── CLI (Spec 09) ─────────────────────────────────────────────────────────────
var specOpt = new Option<FileInfo?>("--spec")
{
    Description = "Path to the .spec (or .md) input file",
};

var outOpt = new Option<DirectoryInfo?>("--out")
{
    Description = "Artifacts output directory (default: <spec-dir>/artifacts)",
};

var forceOpt = new Option<string[]>("--force")
{
    Description = "Delete a pass artifact to force re-execution (repeatable)",
    AllowMultipleArgumentsPerToken = false,
    Arity = ArgumentArity.ZeroOrMore,
};

var verboseOpt = new Option<bool>("--verbose")
{
    Description = "Set log level to Debug",
};

var runOpt = new Option<bool>("--run")
{
    Description = "Execute the compiled binary immediately after a successful build",
};

var chatModelOpt = new Option<string?>("--chat-model")
{
    Description = "Override the chat model for this compilation (default: IRONLLM_CHAT_MODEL env / ministral-3:3b)",
};

var compileCommand = new Command("compile", "Compile a .spec or .md file to a native executable")
{
    specOpt, outOpt, forceOpt, verboseOpt, runOpt, chatModelOpt,
};

var rootCommand = new RootCommand("Specc — spec compiler") { compileCommand };

compileCommand.SetAction(async result =>
{
    var spec    = result.GetValue(specOpt);
    var outDir  = result.GetValue(outOpt);
    var force   = result.GetValue(forceOpt) ?? [];
    var verbose         = result.GetValue(verboseOpt);
    var run             = result.GetValue(runOpt);
    var chatModelOverride = result.GetValue(chatModelOpt);
    var effectiveChatModel = chatModelOverride ?? chatModel;

    var specPath = spec?.FullName
        ?? Path.GetFullPath("examples/FizzBuzz/FizzBuzz.md");

    var artifactsDir = outDir?.FullName
        ?? Path.Combine(Path.GetDirectoryName(specPath)!, "artifacts");

    // ── Delete forced artifacts ───────────────────────────────────────────────
    foreach (var passName in force)
    {
        var target = passName.EndsWith(".json") || passName.EndsWith(".il") || passName.EndsWith(".dll")
            ? Path.Combine(artifactsDir, passName)
            : Directory.Exists(artifactsDir)
                ? Directory.GetFiles(artifactsDir)
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                        .Contains(passName, StringComparison.OrdinalIgnoreCase))
                : null;

        if (target != null && File.Exists(target))
        {
            File.Delete(target);
            Console.WriteLine($"Forced: deleted {target}");
        }
    }

    // ── Host + DI ─────────────────────────────────────────────────────────────
    var logLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;
    var logDir   = Path.Combine(artifactsDir, "logs");
    Directory.CreateDirectory(logDir);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Is(logLevel)
        .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            Path.Combine(logDir, "ironllm-.log"),
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger, dispose: true);

    var isMarkdown = specPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    builder.Services
        .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
            new OllamaEmbeddingGenerator(new Uri(ollamaBase), embedModel))
        .AddSingleton<IChatClient>(_ =>
            new OllamaChatClient(new Uri(ollamaBase), effectiveChatModel));

    if (isMarkdown)
        builder.Services.AddTransient<ICompilerPass, MarkdownSpecPass>();

    builder.Services
        .AddTransient<ICompilerPass, ParseSpecPass>()
        .AddTransient<ICompilerPass, SemanticGraphPass>()
        .AddTransient<ICompilerPass, GraphVisualizationPass>()
        .AddTransient<ICompilerPass, AcceptanceCriteriaPass>()
        .AddTransient<ICompilerPass, EmbeddingPass>()
        .AddTransient<ICompilerPass, RepositoryRetrievalPass>()
        .AddTransient<ICompilerPass, SemanticNormalizationPass>()
        .AddTransient<ICompilerPass, NodeMlpPass>()
        .AddTransient<ICompilerPass, CfgPass>()
        .AddTransient<ICompilerPass, CfgVisualizationPass>()
        .AddTransient<ICompilerPass, StackIrPass>()
        .AddTransient<ICompilerPass, SemanticValidationPass>()
        .AddTransient<ICompilerPass, MsilGenerationPass>()
        .AddTransient<ICompilerPass, AssemblyEmitPass>()
        .AddTransient<ICompilerPass, AcceptanceVerificationPass>()
        .AddTransient<ICompilerPass, RepositoryPersistPass>()
        .AddTransient<CompilationPipeline>();

    using var host = builder.Build();

    Directory.CreateDirectory(artifactsDir);

    var context = new CompilationContext
    {
        SpecPath       = specPath,
        InputPath      = specPath,
        ArtifactsDir   = artifactsDir,
        RepositoryPath = Path.Combine(repoRoot, "repository"),
    };

    var pipeline = host.Services.GetRequiredService<CompilationPipeline>();
    Log.Logger.Information("Chat model: {Model}", effectiveChatModel);
    await pipeline.RunAsync(context);

    if (run)
    {
        var launcher = context.LauncherPath ?? context.AssemblyPath;
        if (launcher == null)
        {
            Console.Error.WriteLine("--run: no executable produced by this compilation");
            return;
        }

        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName  = launcher,
            UseShellExecute = false,
        });
        await proc!.WaitForExitAsync();
    }
});

return await rootCommand.Parse(args).InvokeAsync();

// ── Helpers ───────────────────────────────────────────────────────────────────
static Dictionary<string, string> LoadDotEnv(string path)
{
    if (!File.Exists(path)) return [];
    return File.ReadLines(path)
        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
        .Select(l => l.Split('=', 2))
        .Where(p => p.Length == 2)
        .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
}

static string Cfg(Dictionary<string, string> env, string key, string fallback) =>
    Environment.GetEnvironmentVariable(key)
    ?? env.GetValueOrDefault(key)
    ?? fallback;
