using System.Diagnostics;
using Recommendations.Api.Domain;

namespace Recommendations.Api.Pipeline;

public class PipelineContext
{
    public RecommendationRequest Request { get; set; } = null!;

    // After GeocodeStep
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? ResolvedAddress { get; set; }
    public bool GeocodingAvailable { get; set; } = true;

    // After CacheCheckStep
    public bool CacheHit { get; set; }
    public string? CacheKey { get; set; }
    public RecommendationResponse? CachedResponse { get; set; }

    // After ParallelGenerationStep
    public List<AiProviderResult> GenerationResults { get; set; } = new();

    // After GooglePlacesEnrichmentStep
    public List<Place> RealPlaces { get; set; } = new();
    public bool GoogleEnriched { get; set; }

    // After CrossValidationStep
    public List<CrossValidationResult> ValidationResults { get; set; } = new();

    // After ConsensusScoringStep
    public List<PlaceRecommendation> ScoredCandidates { get; set; } = new();

    // After SynthesisStep
    public RecommendationResponse? FinalResponse { get; set; }
    public string SynthesizedBy { get; set; } = string.Empty;

    // Metadata tracking
    public List<string> FailedProviders { get; set; } = new();
    public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
}
