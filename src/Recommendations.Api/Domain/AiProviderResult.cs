namespace Recommendations.Api.Domain;

public record AiProviderResult
{
    public string ProviderName { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public List<PlaceRecommendation> Recommendations { get; init; } = new();
    public string? RawResponse { get; init; }
    public TimeSpan Elapsed { get; init; }
}
