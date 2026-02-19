using Recommendations.Api.Domain;

namespace Recommendations.Api.Abstractions;

public interface ICacheService
{
    Task<RecommendationResponse?> GetAsync(string cacheKey, CancellationToken ct = default);
    Task SetAsync(string cacheKey, RecommendationResponse response, TimeSpan ttl, CancellationToken ct = default);
    Task InvalidateAsync(string cacheKey, CancellationToken ct = default);
    Task PurgeExpiredAsync(CancellationToken ct = default);
    Task<CacheStats> GetStatsAsync(CancellationToken ct = default);
}

public record CacheStats
{
    public int TotalEntries { get; init; }
    public int ExpiredEntries { get; init; }
    public DateTime? OldestEntry { get; init; }
    public DateTime? NewestEntry { get; init; }
}
