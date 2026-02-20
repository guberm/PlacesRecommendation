using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recommendations.Api.Api.Endpoints;

public static class ModelsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/providers/models", GetModels)
            .WithTags("Providers")
            .WithSummary("List available models for a given AI provider");
    }

    private static async Task<IResult> GetModels(
        string provider,
        IHttpClientFactory httpFactory,
        string? apiKey = null,
        string? endpoint = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return Results.BadRequest(new { error = "provider is required" });

        try
        {
            var (models, warning) = provider.ToUpperInvariant() switch
            {
                "OPENROUTER"              => await GetOpenRouterModels(httpFactory, apiKey, ct),
                "OPENAI"                  => await GetOpenAiModels(httpFactory, apiKey, ct),
                "GEMINI"                  => await GetGeminiModels(httpFactory, apiKey, ct),
                "ANTHROPIC"               => await GetAnthropicModels(httpFactory, apiKey, ct),
                "AZUREOPENAI" or "AZURE"  => await GetAzureModels(httpFactory, apiKey, endpoint, ct),
                _ => throw new ArgumentException($"Unknown provider: {provider}")
            };

            return Results.Ok(new { models, warning });
        }
        catch (HttpRequestException ex)
        {
            return Results.Ok(new
            {
                models = Array.Empty<ModelInfo>(),
                warning = $"Could not reach {provider} API: {ex.Message}"
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    // ─── OpenRouter ───────────────────────────────────────────────────────────

    private static async Task<(IReadOnlyList<ModelInfo>, string?)> GetOpenRouterModels(
        IHttpClientFactory httpFactory, string? apiKey, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);
        var data = root?["data"]?.AsArray();
        if (data is null) return (Array.Empty<ModelInfo>(), "Empty response from OpenRouter");

        var models = data
            .Where(m => m is not null)
            .Select(m =>
            {
                var id   = m!["id"]?.GetValue<string>() ?? string.Empty;
                var name = m["name"]?.GetValue<string>() ?? id;
                var ctx  = TryGetLong(m, "context_length");
                var ctxSuffix = ctx.HasValue ? $" [{FormatCtx(ctx.Value)}]" : string.Empty;
                return new ModelInfo(id, name + ctxSuffix);
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .OrderBy(m => m.Id)
            .ToList();

        return (models, null);
    }

    // ─── OpenAI ───────────────────────────────────────────────────────────────

    private static async Task<(IReadOnlyList<ModelInfo>, string?)> GetOpenAiModels(
        IHttpClientFactory httpFactory, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (Array.Empty<ModelInfo>(), "Enter your OpenAI API key and click Load Models.");

        using var http = httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);
        var data = root?["data"]?.AsArray();
        if (data is null) return (Array.Empty<ModelInfo>(), "Empty response from OpenAI");

        var models = data
            .Where(m => m is not null)
            .Select(m => m!["id"]?.GetValue<string>() ?? string.Empty)
            .Where(id =>
                !string.IsNullOrWhiteSpace(id) &&
                (id.StartsWith("gpt-",  StringComparison.OrdinalIgnoreCase) ||
                 id.StartsWith("o1",    StringComparison.OrdinalIgnoreCase) ||
                 id.StartsWith("o3",    StringComparison.OrdinalIgnoreCase) ||
                 id.StartsWith("o4",    StringComparison.OrdinalIgnoreCase)))
            .OrderBy(id => id)
            .Select(id => new ModelInfo(id, id))
            .ToList();

        return (models, models.Count == 0 ? "No chat models found for this key." : null);
    }

    // ─── Google Gemini ────────────────────────────────────────────────────────

    private static async Task<(IReadOnlyList<ModelInfo>, string?)> GetGeminiModels(
        IHttpClientFactory httpFactory, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (Array.Empty<ModelInfo>(), "Enter your Gemini API key and click Load Models.");

        using var http = httpFactory.CreateClient();
        var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(apiKey)}";
        var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);
        var arr  = root?["models"]?.AsArray();
        if (arr is null) return (Array.Empty<ModelInfo>(), "Empty response from Gemini API");

        var models = arr
            .Where(m => m is not null)
            .Where(m =>
            {
                var methods = m!["supportedGenerationMethods"]?.AsArray();
                return methods?.Any(x => x?.GetValue<string>() == "generateContent") ?? false;
            })
            .Select(m =>
            {
                var fullName    = m!["name"]?.GetValue<string>() ?? string.Empty;
                var displayName = m["displayName"]?.GetValue<string>() ?? fullName;
                var shortId = fullName.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                    ? fullName["models/".Length..]
                    : fullName;
                return new ModelInfo(shortId, displayName);
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .OrderBy(m => m.Id)
            .ToList();

        return (models, models.Count == 0 ? "No generateContent-capable models found." : null);
    }

    // ─── Anthropic ────────────────────────────────────────────────────────────
    // Anthropic models API: GET https://api.anthropic.com/v1/models
    // Requires: x-api-key header, anthropic-version header

    private static async Task<(IReadOnlyList<ModelInfo>, string?)> GetAnthropicModels(
        IHttpClientFactory httpFactory, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (Array.Empty<ModelInfo>(), "Enter your Anthropic API key and click Load Models.");

        using var http = httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);
        var data = root?["data"]?.AsArray();
        if (data is null) return (Array.Empty<ModelInfo>(), "Empty response from Anthropic API");

        var models = data
            .Where(m => m is not null)
            .Select(m =>
            {
                var id          = m!["id"]?.GetValue<string>() ?? string.Empty;
                var displayName = m["display_name"]?.GetValue<string>() ?? id;
                return new ModelInfo(id, displayName);
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .OrderByDescending(m => m.Id) // newest first
            .ToList();

        return (models, models.Count == 0 ? "No models returned by Anthropic API." : null);
    }

    // ─── Azure OpenAI ─────────────────────────────────────────────────────────
    // Azure OpenAI models API: GET {endpoint}/openai/models?api-version=2024-10-21
    // Requires: api-key header and the resource endpoint

    private static async Task<(IReadOnlyList<ModelInfo>, string?)> GetAzureModels(
        IHttpClientFactory httpFactory, string? apiKey, string? resourceEndpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(resourceEndpoint))
            return (Array.Empty<ModelInfo>(), "Enter your Azure OpenAI API key and endpoint, then click Load.");

        var baseUrl = resourceEndpoint.TrimEnd('/');
        var url = $"{baseUrl}/openai/models?api-version=2024-10-21";

        using var http = httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("api-key", apiKey);

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);
        var data = root?["data"]?.AsArray();
        if (data is null) return (Array.Empty<ModelInfo>(), "Empty response from Azure OpenAI API");

        var models = data
            .Where(m => m is not null)
            .Select(m =>
            {
                var id = m!["id"]?.GetValue<string>() ?? string.Empty;
                return new ModelInfo(id, id);
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .OrderBy(m => m.Id)
            .ToList();

        return (models, models.Count == 0 ? "No models found in this Azure resource." : null);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static long? TryGetLong(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<long>(); }
        catch { return null; }
    }

    private static string FormatCtx(long tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000}M ctx"
        : tokens >= 1_000   ? $"{tokens / 1_000}k ctx"
        : $"{tokens} ctx";

    private record ModelInfo(string Id, string Name);
}
