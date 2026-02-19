using System.Diagnostics;
using Microsoft.Extensions.Options;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Configuration;
using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Infrastructure.AiProviders;

public class AzureOpenAiProvider : AiProviderBase, IAiProvider
{
    private readonly AzureOpenAiOptions _options;
    private readonly ILogger<AzureOpenAiProvider> _logger;

    public string Name => "Azure OpenAI";
    public bool IsAvailable => _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiKey)
        && !string.IsNullOrWhiteSpace(_options.Endpoint);

    public AzureOpenAiProvider(IOptions<AiProviderOptions> options, ILogger<AzureOpenAiProvider> logger)
    {
        _options = options.Value.AzureOpenAi;
        _logger = logger;
    }

    private ChatClient CreateClient()
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(_options.Endpoint),
            new System.ClientModel.ApiKeyCredential(_options.ApiKey));
        return azureClient.GetChatClient(_options.DeploymentName);
    }

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
            var completion = await client.CompleteChatAsync(
                new[] { ChatMessage.CreateUserMessage(prompt) },
                cancellationToken: cts.Token);

            var raw = completion.Value.Content[0].Text;
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
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var client = CreateClient();
            var prompt = BuildValidationPrompt(latitude, longitude, sourceProviderName, recommendations);
            var completion = await client.CompleteChatAsync(
                new[] { ChatMessage.CreateUserMessage(prompt) },
                cancellationToken: cts.Token);

            var raw = completion.Value.Content[0].Text;
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
            var completion = await client.CompleteChatAsync(
                new[] { ChatMessage.CreateUserMessage(prompt) },
                cancellationToken: cts.Token);

            var raw = completion.Value.Content[0].Text;
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
