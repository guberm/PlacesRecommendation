using System.Text.Json;
using FluentValidation;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Domain;
using Recommendations.Api.Pipeline;

namespace Recommendations.Api.Api.Endpoints;

public static class RecommendationEndpoints
{
    private static readonly JsonSerializerOptions _logJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
        ILogger<RecommendationOrchestrator> logger,
        CancellationToken ct)
    {
        // ── Log full request payload ──────────────────────────────────────────
        logger.LogDebug("\n══════════════════ RECOMMENDATION REQUEST ══════════════════\n{Payload}\n═══════════════════════════════════════════════════════════",
            JsonSerializer.Serialize(request with { UserApiKeys = MaskApiKeys(request.UserApiKeys) }, _logJson));

        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            var errs = validation.Errors.Select(e => e.ErrorMessage).ToList();
            logger.LogWarning("Request validation failed: {Errors}", string.Join("; ", errs));
            return Results.BadRequest(new { errors = errs });
        }

        try
        {
            var response = await orchestrator.GetRecommendationsAsync(request, ct);

            // ── Log full response ─────────────────────────────────────────────
            logger.LogDebug("\n══════════════════ RECOMMENDATION RESPONSE ══════════════════\n{Payload}\n════════════════════════════════════════════════════════════",
                JsonSerializer.Serialize(response, _logJson));

            return Results.Ok(response);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Bad request: {Message}", ex.Message);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError("Pipeline failed (503): {Message}", ex.Message);
            return Results.Problem(ex.Message, statusCode: 503);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Request cancelled (504)");
            return Results.StatusCode(504);
        }
    }

    /// Mask API key values so they don't appear in logs.
    private static Dictionary<string, string>? MaskApiKeys(Dictionary<string, string>? keys)
    {
        if (keys is null || keys.Count == 0) return keys;
        return keys.ToDictionary(
            k => k.Key,
            k => k.Key.EndsWith("Model", StringComparison.OrdinalIgnoreCase) || k.Key.EndsWith("Endpoint", StringComparison.OrdinalIgnoreCase)
                ? k.Value
                : (k.Value.Length > 8 ? k.Value[..6] + "..." + k.Value[^4..] : "***"));
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
