using Recommendations.Api.Abstractions;

namespace Recommendations.Api.Pipeline.Steps;

public class GooglePlacesEnrichmentStep
{
    private readonly IPlacesProvider _places;
    private readonly ILogger<GooglePlacesEnrichmentStep> _logger;

    public GooglePlacesEnrichmentStep(IPlacesProvider places, ILogger<GooglePlacesEnrichmentStep> logger)
    {
        _places = places;
        _logger = logger;
    }

    public async Task ExecuteAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        if (!_places.IsAvailable)
        {
            _logger.LogWarning("Google Places API not available, skipping enrichment");
            ctx.GoogleEnriched = false;
            return;
        }

        if (!ctx.GeocodingAvailable)
        {
            _logger.LogInformation("Geocoding unavailable, skipping Google Places enrichment");
            ctx.GoogleEnriched = false;
            return;
        }

        try
        {
            var realPlaces = await _places.SearchNearbyAsync(
                ctx.Latitude, ctx.Longitude,
                ctx.Request.Category,
                ctx.Request.RadiusMeters,
                20, ct);

            ctx.RealPlaces = realPlaces.ToList();
            ctx.GoogleEnriched = ctx.RealPlaces.Count > 0;

            // Enrich AI recommendations with real place data
            foreach (var result in ctx.GenerationResults)
            {
                var enriched = result.Recommendations.Select(rec =>
                {
                    var match = FindBestMatch(rec.Name, ctx.RealPlaces);
                    return match is not null ? rec.WithEnrichedData(match) : rec;
                }).ToList();

                ctx.GenerationResults[ctx.GenerationResults.IndexOf(result)] =
                    result with { Recommendations = enriched };
            }

            _logger.LogInformation("Google Places enrichment: {RealCount} real places found, matched with AI recommendations",
                ctx.RealPlaces.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google Places enrichment failed");
            ctx.GoogleEnriched = false;
        }
    }

    private static Domain.Place? FindBestMatch(string aiName, IReadOnlyList<Domain.Place> realPlaces)
    {
        if (realPlaces.Count == 0) return null;

        var normalized = Normalize(aiName);

        // Exact match first
        var exact = realPlaces.FirstOrDefault(p => Normalize(p.Name) == normalized);
        if (exact is not null) return exact;

        // Contains match
        var contains = realPlaces.FirstOrDefault(p =>
            Normalize(p.Name).Contains(normalized) || normalized.Contains(Normalize(p.Name)));
        if (contains is not null) return contains;

        // Word overlap match (at least 60% of words match)
        var aiWords = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var best = realPlaces
            .Select(p => new { Place = p, Score = WordOverlap(aiWords, Normalize(p.Name)) })
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return best?.Place;
    }

    private static double WordOverlap(string[] aiWords, string realName)
    {
        if (aiWords.Length == 0) return 0;
        var realWords = realName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = aiWords.Count(w => realWords.Contains(w));
        return (double)matches / aiWords.Length;
    }

    private static string Normalize(string s) =>
        s.ToLowerInvariant()
         .Replace("'", "")
         .Replace("-", " ")
         .Trim();
}
