using Specc.Passes.Repository;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

/// <summary>
/// Runs after EmbeddingPass (before SemanticNormalizationPass).
/// Queries the repository for semantically similar prior compilations and
/// stores the results in context.SimilarPriors.
/// </summary>
public class RepositoryRetrievalPass : ICompilerPass
{
    private readonly ILogger<RepositoryRetrievalPass> _logger;

    public RepositoryRetrievalPass(ILogger<RepositoryRetrievalPass> logger)
    {
        _logger = logger;
    }

    public string  Name          => "03c-RepositoryRetrieval";
    public string? ArtifactFile  => null;

    public async Task ExecuteAsync(CompilationContext context)
    {
        var priors = await GraphRepository.FindSimilarAsync(context);
        context.SimilarPriors = priors;
        _logger.LogInformation("Repository retrieval found {Count} similar prior(s)", priors.Count);
    }

    public Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
        => Task.CompletedTask;
}

/// <summary>
/// Runs after AcceptanceVerificationPass (final pass).
/// Persists the current compilation to the repository.
/// </summary>
public class RepositoryPersistPass : ICompilerPass
{
    private readonly ILogger<RepositoryPersistPass> _logger;

    public RepositoryPersistPass(ILogger<RepositoryPersistPass> logger)
    {
        _logger = logger;
    }

    public string  Name          => "09-RepositoryPersist";
    public string? ArtifactFile  => null;

    public async Task ExecuteAsync(CompilationContext context)
    {
        if (context.AcceptancePassed == false)
        {
            _logger.LogWarning("Repository: skipping persist — acceptance verification failed");
            return;
        }
        await GraphRepository.PersistAsync(context);
        _logger.LogInformation("Repository: persisted compilation unit for {SpecPath}", context.SpecPath);
    }

    public Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
        => Task.CompletedTask;
}
