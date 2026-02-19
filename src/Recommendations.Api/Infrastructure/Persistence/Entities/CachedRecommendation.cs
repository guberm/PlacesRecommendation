namespace Recommendations.Api.Infrastructure.Persistence.Entities;

public class CachedRecommendation
{
    public int Id { get; set; }
    public string CacheKey { get; set; } = string.Empty;
    public string ResponseJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Category { get; set; } = string.Empty;
    public int HitCount { get; set; }
}
