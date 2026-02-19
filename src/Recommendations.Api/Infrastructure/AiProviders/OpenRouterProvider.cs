using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Configuration;
using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Infrastructure.AiProviders;

/// <summary>
/// AI provider backed by OpenRouter.ai — a unified gateway to 100+ models.
/// Uses the OpenAI-compatible REST API directly (no SDK dependency).
/// Configure AiProviders:OpenRouter:Model to any OpenRouter model slug,
/// e.g. "openai/gpt-4o", "anthropic/claude-3.5-sonnet", "google/gemini-flash-1.5".
/// </summary>
public class OpenRouterProvider : AiProviderBase, IAiProvider
{
    private readonly OpenRouterOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OpenRouterProvider> _logger;

    public string Name => $"OpenRouter ({_options.Model})";
    public bool IsAvailable => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey);

    public OpenRouterProvider(
        IOptions<AiProviderOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<OpenRouterProvider> logger)
    {
        _options = options.Value.OpenRouter;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<AiProviderResult> GenerateRecommendationsAsync(
        double latitude, double longitude, IReadOnlyList<PlaceCategory> categories,
        string? locationContext, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var prompt = BuildGenerationPrompt(latitude, longitude, categories, locationContext);
            var raw = await CallChatAsync(prompt, ct);

            if (string.IsNullOrWhiteSpace(raw))
                _logger.LogWarning("{Provider}: empty response after {Ms}ms (model may have only produced reasoning tokens)", Name, sw.ElapsedMilliseconds);
            else
                _logger.LogDebug("{Provider} raw response ({Len} chars): {Raw}", Name, raw.Length, raw.Length > 500 ? raw[..500] + "..." : raw);

            var recommendations = ParseGenerationJson(raw, Name, categories.Count == 1 ? categories[0] : PlaceCategory.All);

            if (recommendations.Count == 0 && !string.IsNullOrWhiteSpace(raw))
                _logger.LogWarning("{Provider}: JSON parse produced 0 recommendations. Raw starts with: {Start}", Name, raw.Length > 300 ? raw[..300] : raw);

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
            var prompt = BuildValidationPrompt(latitude, longitude, sourceProviderName, recommendations);
            var raw = await CallChatAsync(prompt, ct);
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
            var locationContext = allValidatedResults.FirstOrDefault()?.ValidatedItems
                .FirstOrDefault()?.Original.Address;
            var prompt = BuildSynthesisPrompt(latitude, longitude, categories, scoredCandidates, locationContext);
            var raw = await CallChatAsync(prompt, ct);
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

    private async Task<string> CallChatAsync(string userPrompt, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var http = _httpFactory.CreateClient("openrouter");

        // stream:true — receive tokens as they arrive; avoids buffering timeouts on slow thinking models
        var body = JsonSerializer.Serialize(new
        {
            model = _options.Model,
            max_tokens = _options.MaxTokens,
            stream = true,
            messages = new[] { new { role = "user", content = userPrompt } }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", _options.AppReferer);
        request.Headers.TryAddWithoutValidation("X-Title", _options.AppTitle);

        // ResponseHeadersRead: return as soon as headers arrive, stream body as SSE
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        var contentSb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        int chunkCount = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            // Log first 3 chunks so we can see the actual field structure
            if (chunkCount < 3)
                _logger.LogDebug("{Provider} SSE chunk #{N}: {Data}", Name, chunkCount, data.Length > 300 ? data[..300] : data);
            chunkCount++;

            try
            {
                var node = JsonNode.Parse(data);
                var delta = node?["choices"]?[0]?["delta"];

                // Standard content (non-thinking models)
                var content = delta?["content"]?.GetValue<string>();
                if (content != null) contentSb.Append(content);

                // reasoning_content: used by thinking models (stepfun, qwen-thinking, etc.)
                var reasoning = delta?["reasoning_content"]?.GetValue<string>();
                if (reasoning != null) reasoningSb.Append(reasoning);

                // Some models use "text" or "message" instead of "content"
                var text = delta?["text"]?.GetValue<string>();
                if (text != null) contentSb.Append(text);
            }
            catch { /* skip malformed chunks */ }
        }

        _logger.LogDebug("{Provider}: {Total} chunks, content={CLen} chars, reasoning={RLen} chars",
            Name, chunkCount, contentSb.Length, reasoningSb.Length);

        // Prefer actual content; fall back to reasoning blob (thinking models that embed answer there)
        if (contentSb.Length > 0)
        {
            _logger.LogDebug("{Provider}: {Len} chars from delta.content", Name, contentSb.Length);
            return contentSb.ToString();
        }

        if (reasoningSb.Length > 0)
        {
            _logger.LogDebug("{Provider}: {Len} chars from delta.reasoning_content (thinking model fallback)", Name, reasoningSb.Length);
            return reasoningSb.ToString();
        }

        return string.Empty;
    }
}
