using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Domain;

public record RecommendationResponse
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string? ResolvedAddress { get; init; }
    public PlaceCategory Category { get; init; }
    public List<PlaceCategory> Categories { get; init; } = new();
    public List<PlaceRecommendation> Recommendations { get; init; } = new();
    public PipelineMetadata Metadata { get; init; } = new();
    public bool FromCache { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record PipelineMetadata
{
    public List<string> ProvidersUsed { get; init; } = new();
    public List<string> ProvidersFailed { get; init; } = new();
    public bool GooglePlacesEnriched { get; init; }
    public int TotalCandidatesEvaluated { get; init; }
    public string TotalElapsed { get; init; } = string.Empty;
    public string SynthesizedBy { get; init; } = string.Empty;
}
