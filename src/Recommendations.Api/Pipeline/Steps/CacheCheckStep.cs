using Microsoft.Extensions.Options;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Configuration;
using Recommendations.Api.Domain.Enums;
using Recommendations.Api.Infrastructure.Cache;

namespace Recommendations.Api.Pipeline.Steps;

public class CacheCheckStep
{
    private readonly ICacheService _cache;
    private readonly CacheOptions _options;
    private readonly ILogger<CacheCheckStep> _logger;

    public CacheCheckStep(ICacheService cache, IOptions<CacheOptions> options, ILogger<CacheCheckStep> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        var effective = ctx.Request.EffectiveCategories;
        var key = ctx.GeocodingAvailable
            ? CacheKeyBuilder.Build(ctx.Latitude, ctx.Longitude, effective, _options.GridPrecisionDecimalPlaces)
            : CacheKeyBuilder.BuildFromAddress(
                ctx.ResolvedAddress ?? ctx.Request.Address ?? string.Empty,
                effective.Count == 1 ? effective[0] : PlaceCategory.All);
        ctx.CacheKey = key;

        if (ctx.Request.ForceRefresh)
        {
            _logger.LogInformation("Force refresh requested, skipping cache for key {Key}", key);
            return;
        }

        var cached = await _cache.GetAsync(key, ct);
        if (cached is not null)
        {
            _logger.LogInformation("Cache hit for key {Key}", key);
            ctx.CacheHit = true;
            ctx.CachedResponse = cached;
        }
        else
        {
            _logger.LogInformation("Cache miss for key {Key}", key);
        }
    }
}
