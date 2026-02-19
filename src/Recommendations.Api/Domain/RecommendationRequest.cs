using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Domain;

public record RecommendationRequest
{
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Address { get; init; }

    // Single category (backward-compatible)
    public PlaceCategory Category { get; init; } = PlaceCategory.All;

    // Multi-category: if provided and non-empty, takes priority over Category
    public List<PlaceCategory>? Categories { get; init; }

    public int MaxResults { get; init; } = 10;
    public int RadiusMeters { get; init; } = 1000;
    public bool ForceRefresh { get; init; } = false;

    /// <summary>The resolved category list: Categories (if set) else [Category].</summary>
    public IReadOnlyList<PlaceCategory> EffectiveCategories =>
        Categories is { Count: > 0 } ? Categories : new[] { Category };
}
