using System.Diagnostics;
using Microsoft.Extensions.Options;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Configuration;
using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;
using Recommendations.Api.Infrastructure;

namespace Recommendations.Api.Infrastructure.AiProviders;

public class AnthropicProvider : AiProviderBase, IAiProvider
{
    private readonly AnthropicOptions _options;
    private readonly ILogger<AnthropicProvider> _logger;

    public string Name => "Claude";
    public bool IsAvailable => _options.Enabled && UserApiKeyContext.HasEffectiveKey("Anthropic", _options.ApiKey);

    public AnthropicProvider(IOptions<AiProviderOptions> options, ILogger<AnthropicProvider> logger)
    {
        _options = options.Value.Anthropic;
        _logger = logger;
    }

    private AnthropicClient CreateClient() => new(UserApiKeyContext.GetEffectiveKey("Anthropic", _options.ApiKey));

    public async Task<AiProviderResult> GenerateRecommendationsAsync(
        double latitude, double longitude, IReadOnlyList<PlaceCategory> categories,
        string? locationContext, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var client = CreateClient();
            var prompt = BuildGenerationPrompt(latitude, longitude, categories, locationContext);
            var response = await client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model = UserApiKeyContext.GetEffectiveModel("AnthropicModel", _options.Model),
                MaxTokens = _options.MaxTokens,
                Messages = new List<Message> { new() { Role = RoleType.User, Content = new List<ContentBase> { new TextContent { Text = prompt } } } }
            }, cts.Token);

            var raw = (response.Content.FirstOrDefault() as TextContent)?.Text ?? string.Empty;
            var recommendations = ParseGenerationJson(raw, Name, categories.Count == 1 ? categories[0] : PlaceCategory.All);

            _logger.LogInformation("{Provider} generated {Count} recommendations in {Ms}ms",
                Name, recommendations.Count, sw.ElapsedMilliseconds);

            return new AiProviderResult
            {
                ProviderName = Name,
                Success = true,
                Recommendations = recommendations,
                RawResponse = raw,
                Elapsed = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Provider} generation failed for ({Lat},{Lng})", Name, latitude, longitude);
            return new AiProviderResult { ProviderName = Name, Success = false, ErrorMessage = ex.Message, Elapsed = sw.Elapsed };
        }
    }

    public async Task<CrossValidationResult> ValidateRecommendationsAsync(
        double latitude, double longitude,
        IReadOnlyList<PlaceRecommendation> recommendations,
        string sourceProviderName, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var client = CreateClient();
            var prompt = BuildValidationPrompt(latitude, longitude, sourceProviderName, recommendations);
            var response = await client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model = UserApiKeyContext.GetEffectiveModel("AnthropicModel", _options.Model),
                MaxTokens = _options.MaxTokens,
                Messages = new List<Message> { new() { Role = RoleType.User, Content = new List<ContentBase> { new TextContent { Text = prompt } } } }
            }, cts.Token);

            var raw = (response.Content.FirstOrDefault() as TextContent)?.Text ?? string.Empty;
            return ParseValidationJson(raw, Name, sourceProviderName, recommendations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Provider} validation failed", Name);
            return new CrossValidationResult { ValidatedBy = Name, OriginalSource = sourceProviderName };
        }
    }

    public async Task<AiProviderResult> SynthesizeAsync(
        double latitude, double longitude, IReadOnlyList<PlaceCategory> categories,
        IReadOnlyList<CrossValidationResult> allValidatedResults,
        IReadOnlyList<PlaceRecommendation> scoredCandidates,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var client = CreateClient();
            var locationContext = allValidatedResults.FirstOrDefault()?.ValidatedItems
                .FirstOrDefault()?.Original.Address;
            var prompt = BuildSynthesisPrompt(latitude, longitude, categories, scoredCandidates, locationContext);
            var response = await client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model = UserApiKeyContext.GetEffectiveModel("AnthropicModel", _options.Model),
                MaxTokens = _options.MaxTokens,
                Messages = new List<Message> { new() { Role = RoleType.User, Content = new List<ContentBase> { new TextContent { Text = prompt } } } }
            }, cts.Token);

            var raw = (response.Content.FirstOrDefault() as TextContent)?.Text ?? string.Empty;
            var synthesized = ApplySynthesisText(raw, scoredCandidates);

            return new AiProviderResult
            {
                ProviderName = Name,
                Success = true,
                Recommendations = synthesized,
                Elapsed = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Provider} synthesis failed", Name);
            return new AiProviderResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = ex.Message,
                Recommendations = scoredCandidates.ToList(),
                Elapsed = sw.Elapsed
            };
        }
    }
}
