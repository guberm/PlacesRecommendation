using Recommendations.Api.Abstractions;

namespace Recommendations.Api.Pipeline.Steps;

public class CrossValidationStep
{
    private readonly IEnumerable<IAiProvider> _providers;
    private readonly ILogger<CrossValidationStep> _logger;

    public CrossValidationStep(IEnumerable<IAiProvider> providers, ILogger<CrossValidationStep> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task ExecuteAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        var availableProviders = _providers.Where(p => p.IsAvailable).ToList();
        var successfulResults = ctx.GenerationResults.Where(r => r.Success).ToList();

        if (successfulResults.Count <= 1)
        {
            _logger.LogInformation("Only {Count} successful provider(s), skipping cross-validation", successfulResults.Count);
            return;
        }

        // Build validation tasks: each provider validates every other provider's output
        var validationTasks = new List<Task<Domain.CrossValidationResult>>();

        foreach (var validator in availableProviders)
        {
            foreach (var source in successfulResults.Where(s => s.ProviderName != validator.Name))
            {
                if (source.Recommendations.Count == 0) continue;

                var v = validator;
                var s = source;
                validationTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        return await v.ValidateRecommendationsAsync(
                            ctx.Latitude, ctx.Longitude,
                            s.Recommendations, s.ProviderName, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "{Validator} failed to validate {Source}'s results", v.Name, s.ProviderName);
                        return new Domain.CrossValidationResult
                        {
                            ValidatedBy = v.Name,
                            OriginalSource = s.ProviderName,
                            ValidatedItems = new()
                        };
                    }
                }, ct));
            }
        }

        _logger.LogInformation("Running {Count} cross-validation tasks", validationTasks.Count);
        var results = await Task.WhenAll(validationTasks);
        ctx.ValidationResults = results.ToList();

        _logger.LogInformation("Cross-validation complete: {Count} result sets", ctx.ValidationResults.Count);
    }
}
