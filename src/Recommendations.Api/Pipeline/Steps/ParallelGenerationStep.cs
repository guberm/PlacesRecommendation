using Recommendations.Api.Abstractions;

namespace Recommendations.Api.Pipeline.Steps;

public class ParallelGenerationStep
{
    private readonly IEnumerable<IAiProvider> _providers;
    private readonly ILogger<ParallelGenerationStep> _logger;

    public ParallelGenerationStep(IEnumerable<IAiProvider> providers, ILogger<ParallelGenerationStep> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task ExecuteAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        var available = _providers.Where(p => p.IsAvailable).ToList();
        if (available.Count == 0)
            throw new InvalidOperationException("No AI providers are configured or available.");

        _logger.LogInformation("Running parallel generation with {Count} providers: {Names}",
            available.Count, string.Join(", ", available.Select(p => p.Name)));

        var tasks = available.Select(provider =>
            Task.Run(async () =>
            {
                try
                {
                    return await provider.GenerateRecommendationsAsync(
                        ctx.Latitude, ctx.Longitude,
                        ctx.Request.EffectiveCategories, ctx.ResolvedAddress, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Provider {Name} threw during generation", provider.Name);
                    return new Domain.AiProviderResult
                    {
                        ProviderName = provider.Name,
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }
            }, ct)
        );

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            if (result.Success && result.Recommendations.Count > 0)
            {
                ctx.GenerationResults.Add(result);
                _logger.LogInformation("{Provider}: {Count} recommendations in {Ms}ms",
                    result.ProviderName, result.Recommendations.Count, result.Elapsed.TotalMilliseconds);
            }
            else
            {
                ctx.FailedProviders.Add(result.ProviderName);
                _logger.LogWarning("{Provider} failed: {Error}", result.ProviderName, result.ErrorMessage);
            }
        }

        if (ctx.GenerationResults.Count == 0)
            throw new InvalidOperationException("All AI providers failed to generate recommendations.");

        _logger.LogInformation("{Success}/{Total} providers succeeded",
            ctx.GenerationResults.Count, available.Count);
    }
}
