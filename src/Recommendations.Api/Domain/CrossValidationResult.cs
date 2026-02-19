namespace Recommendations.Api.Domain;

public record CrossValidationResult
{
    public string ValidatedBy { get; init; } = string.Empty;
    public string OriginalSource { get; init; } = string.Empty;
    public List<ValidatedRecommendation> ValidatedItems { get; init; } = new();
}

public record ValidatedRecommendation
{
    public PlaceRecommendation Original { get; init; } = null!;
    public double ValidationScore { get; init; }
    public string? ValidatorComment { get; init; }
    public bool FlaggedAsInaccurate { get; init; }
    public bool FlaggedAsOutOfRange { get; init; }
}
