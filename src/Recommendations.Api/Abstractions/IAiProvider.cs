using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Abstractions;

public interface IAiProvider
{
    string Name { get; }
    bool IsAvailable { get; }

    Task<AiProviderResult> GenerateRecommendationsAsync(
        double latitude,
        double longitude,
        IReadOnlyList<PlaceCategory> categories,
        string? locationContext,
        CancellationToken ct = default);

    Task<CrossValidationResult> ValidateRecommendationsAsync(
        double latitude,
        double longitude,
        IReadOnlyList<PlaceRecommendation> recommendationsToValidate,
        string sourceProviderName,
        CancellationToken ct = default);

    Task<AiProviderResult> SynthesizeAsync(
        double latitude,
        double longitude,
        IReadOnlyList<PlaceCategory> categories,
        IReadOnlyList<CrossValidationResult> allValidatedResults,
        IReadOnlyList<PlaceRecommendation> scoredCandidates,
        CancellationToken ct = default);
}
