using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;
using Recommendations.Api.Pipeline.Steps;

namespace Recommendations.Api.Pipeline;

public class RecommendationOrchestrator
{
    private readonly GeocodeStep _geocodeStep;
    private readonly CacheCheckStep _cacheCheckStep;
    private readonly ParallelGenerationStep _parallelGenerationStep;
    private readonly GooglePlacesEnrichmentStep _googleEnrichmentStep;
    private readonly CrossValidationStep _crossValidationStep;
    private readonly ConsensusScoringStep _consensusScoringStep;
    private readonly SynthesisStep _synthesisStep;
    private readonly CacheWriteStep _cacheWriteStep;
    private readonly ILogger<RecommendationOrchestrator> _logger;

    public RecommendationOrchestrator(
        GeocodeStep geocodeStep,
        CacheCheckStep cacheCheckStep,
        ParallelGenerationStep parallelGenerationStep,
        GooglePlacesEnrichmentStep googleEnrichmentStep,
        CrossValidationStep crossValidationStep,
        ConsensusScoringStep consensusScoringStep,
        SynthesisStep synthesisStep,
        CacheWriteStep cacheWriteStep,
        ILogger<RecommendationOrchestrator> logger)
    {
        _geocodeStep = geocodeStep;
        _cacheCheckStep = cacheCheckStep;
        _parallelGenerationStep = parallelGenerationStep;
        _googleEnrichmentStep = googleEnrichmentStep;
        _crossValidationStep = crossValidationStep;
        _consensusScoringStep = consensusScoringStep;
        _synthesisStep = synthesisStep;
        _cacheWriteStep = cacheWriteStep;
        _logger = logger;
    }

    public async Task<RecommendationResponse> GetRecommendationsAsync(
        RecommendationRequest request, CancellationToken ct = default)
    {
        var ctx = new PipelineContext { Request = request };

        _logger.LogInformation("Starting recommendation pipeline for request: {Category} at ({Lat},{Lng}) / '{Address}'",
            request.Category, request.Latitude, request.Longitude, request.Address);

        // Step 1: Geocode
        await _geocodeStep.ExecuteAsync(ctx, ct);

        // Step 2: Cache check
        await _cacheCheckStep.ExecuteAsync(ctx, ct);
        if (ctx.CacheHit && ctx.CachedResponse is not null)
        {
            _logger.LogInformation("Returning cached response for key {Key}", ctx.CacheKey);
            return ctx.CachedResponse with { FromCache = true };
        }

        // Step 3: Parallel AI generation
        await _parallelGenerationStep.ExecuteAsync(ctx, ct);

        // Step 4: Google Places enrichment
        await _googleEnrichmentStep.ExecuteAsync(ctx, ct);

        // Step 5: Cross-validation
        await _crossValidationStep.ExecuteAsync(ctx, ct);

        // Step 6: Consensus scoring
        await _consensusScoringStep.ExecuteAsync(ctx, ct);

        // Step 7: Synthesis
        await _synthesisStep.ExecuteAsync(ctx, ct);

        // Build final response
        ctx.FinalResponse = BuildResponse(ctx);

        // Step 8: Cache write (fire-and-forget)
        await _cacheWriteStep.ExecuteAsync(ctx, ct);

        _logger.LogInformation("Pipeline complete in {Ms}ms: {Count} recommendations",
            ctx.Stopwatch.ElapsedMilliseconds, ctx.FinalResponse.Recommendations.Count);

        return ctx.FinalResponse;
    }

    private static RecommendationResponse BuildResponse(PipelineContext ctx)
    {
        var elapsed = ctx.Stopwatch.Elapsed;
        var allCandidates = ctx.GenerationResults.SelectMany(r => r.Recommendations).Count();

        return new RecommendationResponse
        {
            Latitude = ctx.Latitude,
            Longitude = ctx.Longitude,
            ResolvedAddress = ctx.ResolvedAddress,
            Category = ctx.Request.EffectiveCategories.Count == 1 ? ctx.Request.EffectiveCategories[0] : PlaceCategory.All,
            Categories = ctx.Request.EffectiveCategories.ToList(),
            Recommendations = ctx.ScoredCandidates,
            FromCache = false,
            GeneratedAt = DateTimeOffset.UtcNow,
            Metadata = new PipelineMetadata
            {
                ProvidersUsed = ctx.GenerationResults.Where(r => r.Success).Select(r => r.ProviderName).ToList(),
                ProvidersFailed = ctx.FailedProviders,
                GooglePlacesEnriched = ctx.GoogleEnriched,
                TotalCandidatesEvaluated = allCandidates,
                TotalElapsed = elapsed.ToString(@"mm\:ss\.fff"),
                SynthesizedBy = ctx.SynthesizedBy
            }
        };
    }
}
