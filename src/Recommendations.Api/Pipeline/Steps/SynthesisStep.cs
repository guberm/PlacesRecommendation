using Recommendations.Api.Abstractions;

namespace Recommendations.Api.Pipeline.Steps;

public class SynthesisStep
{
    private readonly IEnumerable<IAiProvider> _providers;
    private readonly ILogger<SynthesisStep> _logger;

    public SynthesisStep(IEnumerable<IAiProvider> providers, ILogger<SynthesisStep> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task ExecuteAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        if (ctx.ScoredCandidates.Count == 0)
        {
            _logger.LogWarning("No scored candidates to synthesize");
            return;
        }

        // Choose best provider for synthesis: fastest successful one
        var synthesizer = ctx.GenerationResults
            .Where(r => r.Success)
            .OrderBy(r => r.Elapsed)
            .Select(r => _providers.FirstOrDefault(p => p.Name == r.ProviderName && p.IsAvailable))
            .FirstOrDefault(p => p is not null);

        if (synthesizer is null)
        {
            _logger.LogWarning("No synthesizer available, using scored candidates as-is");
            ctx.SynthesizedBy = "Consensus";
            return;
        }

        _logger.LogInformation("Synthesizing with {Provider}", synthesizer.Name);

        try
        {
            var result = await synthesizer.SynthesizeAsync(
                ctx.Latitude, ctx.Longitude,
                ctx.Request.EffectiveCategories,
                ctx.ValidationResults,
                ctx.ScoredCandidates,
                ct);

            if (result.Success && result.Recommendations.Count > 0)
            {
                ctx.ScoredCandidates = result.Recommendations;
                ctx.SynthesizedBy = synthesizer.Name;
            }
            else
            {
                ctx.SynthesizedBy = "Consensus";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synthesis failed for {Provider}", synthesizer.Name);
            ctx.SynthesizedBy = "Consensus";
        }
    }
}
