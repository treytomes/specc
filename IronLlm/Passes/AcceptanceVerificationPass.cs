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
            context.AcceptancePassed = true;
            context.AssertionCount   = 0;
            return;
        }

        _logger.LogInformation("Using {Source} assertions ({Count} total)",
            useAuthorial ? "authorial" : "graph-derived", assertions.Count);

        var launcher = context.LauncherPath ?? context.AssemblyPath
            ?? throw new InvalidOperationException("No launcher or assembly to verify");

        var sw     = Stopwatch.StartNew();
        var stdout = await RunAsync(launcher, context.AssemblyPath, context.TestInput);
        var lines  = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        context.VerificationOutput = lines.Select(l => l.TrimEnd('\r')).ToArray();

        if (lines.Length != assertions.Count)
            throw new AcceptanceFailureException(
                $"Expected {assertions.Count} output lines, got {lines.Length}");

        var failures = new List<AcceptanceFailure>();
        for (var i = 0; i < assertions.Count; i++)
        {
            var assertion = assertions[i];
            var actual    = lines[i].TrimEnd('\r');
            var passed = assertion.IsSubstring
                ? actual.Contains(assertion.Expected, StringComparison.OrdinalIgnoreCase)
                : actual == assertion.Expected;
            if (!passed)
                failures.Add(new AcceptanceFailure(assertion.Iteration, assertion.Expected, actual));
        }

        if (failures.Count > 0)
            throw new AcceptanceFailureException(failures);

        context.AcceptancePassed = true;
        context.AssertionCount   = assertions.Count;
        _logger.LogInformation(
            "Pass {Name} completed in {ElapsedMs}ms — {Count}/{Total} assertions passed",
            Name, sw.ElapsedMilliseconds, assertions.Count, assertions.Count);
    }

    private static bool IsExecutable(string path) =>
        !OperatingSystem.IsWindows() &&
        (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;

    private static async Task<string> RunAsync(string launcher, string? assemblyPath, string? testInput)
    {
        ProcessStartInfo psi;

        if (File.Exists(launcher) && IsExecutable(launcher))
        {
            psi = new ProcessStartInfo(launcher)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = testInput != null,
                UseShellExecute        = false,
            };
        }
        else if (assemblyPath != null && File.Exists(assemblyPath))
        {
            psi = new ProcessStartInfo("dotnet", assemblyPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = testInput != null,
                UseShellExecute        = false,
            };
        }
        else
        {
            throw new InvalidOperationException($"Cannot execute: launcher '{launcher}' not found or not executable");
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process");

        if (testInput != null)
        {
            await process.StandardInput.WriteLineAsync(testInput);
            process.StandardInput.Close();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Read stdout line-by-line with a hard cap so an infinite-loop binary can't
        // overflow the StringBuilder or block forever.
        const int maxLines = 100_000;
        var outputLines = new List<string>(capacity: 256);
        try
        {
            while (!process.StandardOutput.EndOfStream)
            {
                cts.Token.ThrowIfCancellationRequested();
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line == null) break;
                outputLines.Add(line);
                if (outputLines.Count >= maxLines)
                {
                    process.Kill(entireProcessTree: true);
                    throw new AcceptanceFailureException(
                        $"Binary produced more than {maxLines} output lines — likely an infinite loop. " +
                        $"Check the extracted spec.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new AcceptanceFailureException(
                "Binary did not complete within 30 seconds — likely an infinite loop. " +
                "Check the extracted spec.");
        }

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new AcceptanceFailureException(
                $"Process exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }

        return string.Join("\n", outputLines);
    }
}
