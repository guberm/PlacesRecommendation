using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Domain;

public record PlaceRecommendation
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public PlaceCategory Category { get; init; }
    public double ConfidenceScore { get; init; }
    public ConfidenceLevel ConfidenceLevel { get; init; }
    public string? Address { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string SourceProvider { get; init; } = string.Empty;
    public Place? EnrichedPlaceData { get; init; }
    public List<string> Highlights { get; init; } = new();
    public string? WhyRecommended { get; init; }
    public int AgreementCount { get; init; }

    public PlaceRecommendation WithEnrichedData(Place place) =>
        this with { EnrichedPlaceData = place };

    public PlaceRecommendation WithScore(double score, ConfidenceLevel level, int agreementCount) =>
        this with { ConfidenceScore = score, ConfidenceLevel = level, AgreementCount = agreementCount };
}
