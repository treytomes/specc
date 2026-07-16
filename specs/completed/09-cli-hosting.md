# Spec 09 — CLI, App Hosting, Logging, and Dependency Injection

**Status:** Design  
**Scope:** `Program.cs`, `IronLlm.csproj`, new `IronLlm.csproj` package references

## Packages

```xml
<PackageReference Include="System.CommandLine" Version="2.*-beta*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.*" />
```

`Microsoft.Extensions.DependencyInjection` is pulled in transitively by Hosting.

## CLI Surface (`System.CommandLine`)

Replace the current positional-argument convention with explicit options:

```
ironllm compile [options]

Options:
  --spec <path>       Path to the .spec file [default: examples/FizzBuzz/FizzBuzz.spec]
  --out  <dir>        Artifacts output directory [default: <spec-dir>/artifacts]
  --force <pass>      Delete a pass's artifact to force re-execution (repeatable)
  --verbose           Emit per-instruction trace during StackIR lowering
  -h, --help          Show help
```

`--force 04-CFG` deletes `04-cfg.json` before the pipeline runs, triggering re-execution of that pass and all downstream passes. Multiple `--force` flags are supported.

## App Hosting

Use `Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder()`:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        new OllamaEmbeddingGenerator(new Uri(ollamaBase), embedModel))
    .AddSingleton<IChatClient>(sp =>
        new OllamaChatClient(new Uri(ollamaBase), chatModel))
    .AddTransient<CompilationPipeline>()
    .AddTransient<ICompilerPass, ParseSpecPass>()
    .AddTransient<ICompilerPass, SemanticGraphPass>()
    .AddTransient<ICompilerPass, GraphVisualizationPass>()
    .AddTransient<ICompilerPass, EmbeddingPass>()
    .AddTransient<ICompilerPass, CfgPass>()
    .AddTransient<ICompilerPass, StackIrPass>()
    .AddTransient<ICompilerPass, MsilGenerationPass>()
    .AddTransient<ICompilerPass, AssemblyEmitPass>();
```

`CompilationPipeline` takes `IEnumerable<ICompilerPass>` and `ILogger<CompilationPipeline>` in its constructor.

## Logging

Replace all `Console.Write`/`Console.WriteLine` in passes with `ILogger<T>` calls:

| Current | Replacement |
|---------|-------------|
| `Console.Write($"[{pass.Name}] ... ")` | `logger.LogInformation("Running pass {Name}", pass.Name)` |
| `Console.WriteLine("done")` | (completion is implicit — log at Debug if needed) |
| `Console.WriteLine($"  → {path}")` | `logger.LogDebug("Artifact written: {Path}", path)` |
| Pass-skipped message | `logger.LogInformation("Skipped {Name} (artifact exists)", pass.Name)` |

Default log level: `Information`. `--verbose` sets it to `Debug`.

## `CompilationContext` Changes

Remove `IChatClient` and `IEmbeddingGenerator` from `CompilationContext` — they are injected directly into the passes that need them (`EmbeddingPass`, `CfgPass`). `CompilationContext` becomes a plain data bag.

## Ordering Note

Implement Spec 08 (.env config) first — the DI configuration needs to read the Ollama endpoint and model names from the environment, which Spec 08 establishes.
