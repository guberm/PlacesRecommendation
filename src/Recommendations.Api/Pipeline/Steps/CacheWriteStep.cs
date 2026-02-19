using Microsoft.Extensions.Options;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Configuration;

namespace Recommendations.Api.Pipeline.Steps;

public class CacheWriteStep
{
    private readonly ICacheService _cache;
    private readonly CacheOptions _options;
    private readonly ILogger<CacheWriteStep> _logger;

    public CacheWriteStep(ICacheService cache, IOptions<CacheOptions> options, ILogger<CacheWriteStep> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        if (ctx.FinalResponse is null || ctx.CacheKey is null)
            return;

        var ttl = TimeSpan.FromHours(_options.DefaultTtlHours);

        try
        {
            // Await directly — the response is already built so this ~1ms SQLite write
            // doesn't delay the caller, and the scoped DbContext remains alive.
            await _cache.SetAsync(ctx.CacheKey, ctx.FinalResponse, ttl, ct);

            if (Random.Shared.Next(50) == 0)
                await _cache.PurgeExpiredAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache write failed for key {Key}", ctx.CacheKey);
        }
    }
}
