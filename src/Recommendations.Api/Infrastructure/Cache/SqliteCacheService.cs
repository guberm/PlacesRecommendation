using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Domain;
using Recommendations.Api.Infrastructure.Persistence;
using Recommendations.Api.Infrastructure.Persistence.Entities;

namespace Recommendations.Api.Infrastructure.Cache;

public class SqliteCacheService : ICacheService
{
    private readonly RecommendationsDbContext _db;
    private readonly ILogger<SqliteCacheService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public SqliteCacheService(RecommendationsDbContext db, ILogger<SqliteCacheService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RecommendationResponse?> GetAsync(string cacheKey, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = await _db.CachedRecommendations
            .Where(x => x.CacheKey == cacheKey && x.ExpiresAt > now)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return null;

        entity.HitCount++;
        entity.LastAccessedAt = DateTime.UtcNow;
        _ = _db.SaveChangesAsync(CancellationToken.None);

        try
        {
            return JsonSerializer.Deserialize<RecommendationResponse>(entity.ResponseJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize cached response for key {Key}", cacheKey);
            return null;
        }
    }

    public async Task SetAsync(string cacheKey, RecommendationResponse response, TimeSpan ttl, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        var now = DateTime.UtcNow;

        var existing = await _db.CachedRecommendations
            .FirstOrDefaultAsync(x => x.CacheKey == cacheKey, ct);

        if (existing is not null)
        {
            existing.ResponseJson = json;
            existing.ExpiresAt = now.Add(ttl);
            existing.CreatedAt = now;
            existing.LastAccessedAt = now;
            existing.HitCount = 0;
        }
        else
        {
            _db.CachedRecommendations.Add(new CachedRecommendation
            {
                CacheKey = cacheKey,
                ResponseJson = json,
                CreatedAt = now,
                ExpiresAt = now.Add(ttl),
                LastAccessedAt = now,
                Latitude = response.Latitude,
                Longitude = response.Longitude,
                Category = response.Category.ToString(),
                HitCount = 0
            });
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("Cached response for key {Key}, expires at {Expiry}", cacheKey, now.Add(ttl));
    }

    public async Task InvalidateAsync(string cacheKey, CancellationToken ct = default)
    {
        var entity = await _db.CachedRecommendations
            .FirstOrDefaultAsync(x => x.CacheKey == cacheKey, ct);
        if (entity is not null)
        {
            _db.CachedRecommendations.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task PurgeExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expired = await _db.CachedRecommendations
            .Where(x => x.ExpiresAt <= now)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        _db.CachedRecommendations.RemoveRange(expired);
        var deleted = await _db.SaveChangesAsync(ct);

        if (deleted > 0)
            _logger.LogInformation("Purged {Count} expired cache entries", deleted);
    }

    public async Task<CacheStats> GetStatsAsync(CancellationToken ct = default)
    {
        var total = await _db.CachedRecommendations.CountAsync(ct);
        var now = DateTime.UtcNow;
        var expired = await _db.CachedRecommendations
            .Where(x => x.ExpiresAt <= now)
            .CountAsync(ct);
        var oldest = await _db.CachedRecommendations
            .OrderBy(x => x.CreatedAt)
            .Select(x => (DateTime?)x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var newest = await _db.CachedRecommendations
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => (DateTime?)x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return new CacheStats
        {
            TotalEntries = total,
            ExpiredEntries = expired,
            OldestEntry = oldest,
            NewestEntry = newest
        };
    }
}
