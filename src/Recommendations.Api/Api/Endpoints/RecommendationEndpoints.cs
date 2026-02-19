using FluentValidation;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Domain;
using Recommendations.Api.Pipeline;

namespace Recommendations.Api.Api.Endpoints;

public static class RecommendationEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/recommendations")
            .WithTags("Recommendations");

        group.MapPost("/", GetRecommendations)
            .WithSummary("Get place recommendations by location");

        group.MapGet("/cache/status", GetCacheStatus)
            .WithSummary("Get cache statistics");

        group.MapDelete("/cache", ClearCache)
            .WithSummary("Purge the cache");
    }

    private static async Task<IResult> GetRecommendations(
        RecommendationRequest request,
        RecommendationOrchestrator orchestrator,
        IValidator<RecommendationRequest> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return Results.BadRequest(new
            {
                errors = validation.Errors.Select(e => e.ErrorMessage)
            });
        }

        try
        {
            var response = await orchestrator.GetRecommendationsAsync(request, ct);
            return Results.Ok(response);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 503);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(504);
        }
    }

    private static async Task<IResult> GetCacheStatus(
        ICacheService cache,
        CancellationToken ct)
    {
        var stats = await cache.GetStatsAsync(ct);
        return Results.Ok(stats);
    }

    private static async Task<IResult> ClearCache(
        ICacheService cache,
        CancellationToken ct)
    {
        await cache.PurgeExpiredAsync(ct);
        return Results.Ok(new { message = "Expired cache entries purged." });
    }
}
