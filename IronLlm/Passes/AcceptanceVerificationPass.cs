using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

public class AcceptanceVerificationPass : ICompilerPass
{
    private readonly ILogger<AcceptanceVerificationPass> _logger;

    public AcceptanceVerificationPass(ILogger<AcceptanceVerificationPass> logger)
    {
        _logger = logger;
    }

    public string  Name         => "08-AcceptanceVerification";
    public string? ArtifactFile => null;   // terminal pass; no persistent artifact

    public Task LoadFromArtifactAsync(string artifactPath, CompilationContext context) =>
        Task.CompletedTask;

    public async Task ExecuteAsync(CompilationContext context)
    {
        var useAuthorial = context.AuthorialAssertions.Count > 0;
        var assertions   = useAuthorial ? context.AuthorialAssertions : context.Assertions;

        if (assertions.Count == 0)
        {
            _logger.LogDebug("No assertions — AcceptanceVerificationPass is a no-op");
            return;
        }

        _logger.LogInformation("Using {Source} assertions ({Count} total)",
            useAuthorial ? "authorial" : "graph-derived", assertions.Count);

        var launcher = context.LauncherPath ?? context.AssemblyPath
            ?? throw new InvalidOperationException("No launcher or assembly to verify");

        var sw     = Stopwatch.StartNew();
        var stdout = await RunAsync(launcher, context.AssemblyPath);
        var lines  = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length != assertions.Count)
            throw new AcceptanceFailureException(
                $"Expected {assertions.Count} output lines, got {lines.Length}");

        var failures = new List<AcceptanceFailure>();
        for (var i = 0; i < assertions.Count; i++)
        {
            var assertion = assertions[i];
            var actual    = lines[i].TrimEnd('\r');
            if (actual != assertion.Expected)
                failures.Add(new AcceptanceFailure(assertion.Iteration, assertion.Expected, actual));
        }

        if (failures.Count > 0)
            throw new AcceptanceFailureException(failures);

        _logger.LogInformation(
            "Pass {Name} completed in {ElapsedMs}ms — {Count}/{Total} assertions passed",
            Name, sw.ElapsedMilliseconds, assertions.Count, assertions.Count);
    }

    private static async Task<string> RunAsync(string launcher, string? assemblyPath)
    {
        ProcessStartInfo psi;

        if (File.Exists(launcher) && (File.GetUnixFileMode(launcher) & UnixFileMode.UserExecute) != 0)
        {
            psi = new ProcessStartInfo(launcher)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
        }
        else if (assemblyPath != null && File.Exists(assemblyPath))
        {
            psi = new ProcessStartInfo("dotnet", assemblyPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
        }
        else
        {
            throw new InvalidOperationException($"Cannot execute: launcher '{launcher}' not found or not executable");
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new AcceptanceFailureException(
                $"Process exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }

        return stdout;
    }
}
