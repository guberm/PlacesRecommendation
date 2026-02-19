using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Pipeline.Steps;

public class ConsensusScoringStep
{
    private readonly ILogger<ConsensusScoringStep> _logger;

    public ConsensusScoringStep(ILogger<ConsensusScoringStep> logger)
    {
        _logger = logger;
    }

    public Task ExecuteAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        var allRecs = ctx.GenerationResults
            .Where(r => r.Success)
            .SelectMany(r => r.Recommendations)
            .ToList();

        if (allRecs.Count == 0)
        {
            ctx.ScoredCandidates = new();
            return Task.CompletedTask;
        }

        // Group by normalized name
        var groups = allRecs
            .GroupBy(r => Normalize(r.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Scoring {Unique} unique candidates from {Total} total recommendations",
            groups.Count, allRecs.Count);

        var scored = new List<PlaceRecommendation>();

        foreach (var group in groups)
        {
            var recs = group.ToList();
            var representativeRec = recs.OrderByDescending(r => r.ConfidenceScore).First();

            // Base score: average of original confidence scores
            var baseScore = recs.Average(r => r.ConfidenceScore);

            // Agreement bonus: +0.05 per additional AI that mentioned it, max +0.20
            var agreementCount = recs.Count;
            var agreementBonus = Math.Min((agreementCount - 1) * 0.05, 0.20);

            // Validation score: average from cross-validations targeting this place
            var validationScores = ctx.ValidationResults
                .SelectMany(vr => vr.ValidatedItems)
                .Where(vi => string.Equals(Normalize(vi.Original.Name), group.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var validationScore = validationScores.Count > 0
                ? validationScores.Average(v => v.ValidationScore)
                : baseScore;

            // Flag penalties
            var inaccurateFlags = validationScores.Count(v => v.FlaggedAsInaccurate);
            var outOfRangeFlags = validationScores.Count(v => v.FlaggedAsOutOfRange);
            var flagPenalty = inaccurateFlags * 0.20 + outOfRangeFlags * 0.30;

            // Real place bonuses
            var realPlaceBonus = representativeRec.EnrichedPlaceData?.IsVerifiedRealPlace == true ? 0.15 : 0.0;
            var ratingBonus = representativeRec.EnrichedPlaceData?.Rating is double rating
                ? 0.05 * (rating / 5.0)
                : 0.0;

            // Final score
            var finalScore = Math.Clamp(
                baseScore * 0.4 + validationScore * 0.35 + agreementBonus + realPlaceBonus + ratingBonus - flagPenalty,
                0.0, 1.0);

            var level = ScoreToLevel(finalScore);

            // Merge highlights from all providers
            var mergedHighlights = recs
                .SelectMany(r => r.Highlights)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();

            // Pick best description (highest original confidence)
            var bestDescription = recs.OrderByDescending(r => r.ConfidenceScore).First().Description;
            var bestWhy = recs.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.WhyRecommended))?.WhyRecommended;

            scored.Add(representativeRec with
            {
                ConfidenceScore = Math.Round(finalScore, 3),
                ConfidenceLevel = level,
                AgreementCount = agreementCount,
                Highlights = mergedHighlights,
                Description = bestDescription,
                WhyRecommended = bestWhy
            });
        }

        // Sort and trim
        ctx.ScoredCandidates = scored
            .OrderByDescending(r => r.ConfidenceScore)
            .ThenByDescending(r => r.AgreementCount)
            .Take(ctx.Request.MaxResults)
            .ToList();

        _logger.LogInformation("Consensus scoring produced {Count} ranked candidates", ctx.ScoredCandidates.Count);
        return Task.CompletedTask;
    }

    private static string Normalize(string s) =>
        s.ToLowerInvariant()
         .Replace("'", "")
         .Replace("-", " ")
         .Trim();

    private static ConfidenceLevel ScoreToLevel(double score) => score switch
    {
        >= 0.9 => ConfidenceLevel.VeryHigh,
        >= 0.7 => ConfidenceLevel.High,
        >= 0.4 => ConfidenceLevel.Medium,
        _ => ConfidenceLevel.Low
    };
}
