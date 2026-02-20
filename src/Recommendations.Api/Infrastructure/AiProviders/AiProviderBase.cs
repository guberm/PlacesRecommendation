using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Infrastructure.AiProviders;

public abstract class AiProviderBase
{
    protected static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected static string BuildGenerationPrompt(double lat, double lng, IReadOnlyList<PlaceCategory> categories, string? locationContext)
    {
        var categoryLabel = FormatCategoryLabel(categories);

        // When geocoding failed (lat/lng = 0,0), use only the address string
        var locationDesc = (lat == 0 && lng == 0 && locationContext is not null)
            ? locationContext
            : $"coordinates ({lat:F5}, {lng:F5}){(locationContext is not null ? $" ({locationContext})" : string.Empty)}";

        return $$"""
            You are an expert local travel guide. Recommend real, existing {{categoryLabel}} near {{locationDesc}}.

            Return ONLY valid JSON with NO additional text, markdown, or explanation:
            {
              "recommendations": [
                {
                  "name": "string - exact real place name",
                  "description": "string - 2-3 sentences about the place",
                  "address": "string or null - full street address if known",
                  "latitude": number or null,
                  "longitude": number or null,
                  "confidenceScore": number between 0.0 and 1.0,
                  "highlights": ["string", "string", "string"],
                  "whyRecommended": "string - why this is a great choice"
                }
              ]
            }

            Rules:
            - Provide 12-15 recommendations
            - Only include real, verified existing places
            - Order by relevance and quality
            - Be specific about addresses and coordinates
            - confidenceScore should reflect how certain you are this place exists and is relevant
            """;
    }

    protected static string BuildValidationPrompt(double lat, double lng, string sourceProvider, IReadOnlyList<PlaceRecommendation> recommendations)
    {
        var recJson = JsonSerializer.Serialize(recommendations.Select(r => new { r.Name, r.Address, r.Latitude, r.Longitude, r.Description }));
        var locationDesc = (lat == 0 && lng == 0)
            ? recommendations.FirstOrDefault()?.Address ?? "the requested area"
            : $"({lat:F5}, {lng:F5})";

        return $$"""
            You are a fact-checking expert. Validate these place recommendations near {{locationDesc}} provided by {{sourceProvider}}.

            Places to validate:
            {{recJson}}

            Return ONLY valid JSON with NO additional text:
            {
              "validations": [
                {
                  "name": "string - exact name from input list",
                  "validationScore": number between 0.0 and 1.0,
                  "flaggedAsInaccurate": boolean,
                  "flaggedAsOutOfRange": boolean,
                  "comment": "string or null - brief note if flagged"
                }
              ]
            }

            Scoring guide:
            - 0.9-1.0: Confident this is a real, well-known place in the area
            - 0.7-0.9: Likely real but details uncertain
            - 0.5-0.7: Uncertain, may exist but hard to verify
            - 0.0-0.5: Suspicious, possibly fabricated or wrong location
            - flaggedAsInaccurate: true if description/details are wrong
            - flaggedAsOutOfRange: true if the place is more than 5km from coordinates
            """;
    }

    protected static string BuildSynthesisPrompt(double lat, double lng, IReadOnlyList<PlaceCategory> categories, IReadOnlyList<PlaceRecommendation> scoredCandidates, string? locationContext)
    {
        var categoryLabel = FormatCategoryLabel(categories);
        var locationInfo = locationContext is not null ? $" in {locationContext}" : string.Empty;
        var candidatesJson = JsonSerializer.Serialize(scoredCandidates.Select(r => new
        {
            r.Name,
            r.Address,
            r.ConfidenceScore,
            r.AgreementCount,
            r.Highlights,
            r.WhyRecommended
        }));

        return $$"""
            You are a senior travel expert. Write polished final recommendations for {{categoryLabel}}{{locationInfo}} near ({{lat:F5}}, {{lng:F5}}).

            Top consensus candidates (already scored and ranked):
            {{candidatesJson}}

            Return ONLY valid JSON with NO additional text:
            {
              "recommendations": [
                {
                  "name": "string - exact name",
                  "description": "string - 2-3 compelling sentences",
                  "highlights": ["string", "string", "string"],
                  "whyRecommended": "string - why this tops the list"
                }
              ]
            }

            Write all {{scoredCandidates.Count}} entries. Keep the same order. Descriptions should be vivid and helpful for a visitor.
            """;
    }

    protected static List<PlaceRecommendation> ParseGenerationJson(string raw, string providerName, PlaceCategory category)
    {
        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json)) return new();

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return new(); }

        var arr = root?["recommendations"]?.AsArray();
        if (arr is null) return new();

        var results = new List<PlaceRecommendation>();
        foreach (var item in arr)
        {
            if (item is null) continue;
            try
            {
                var name = item["name"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Use TryGetDouble so string-formatted numbers ("0.98") don't throw
                var score = TryGetDouble(item, "confidenceScore") ?? 0.7;
                score = Math.Clamp(score, 0.0, 1.0);

                results.Add(new PlaceRecommendation
                {
                    Name = name.Trim(),
                    Description = item["description"]?.GetValue<string>() ?? string.Empty,
                    Category = category,
                    ConfidenceScore = score,
                    ConfidenceLevel = ScoreToLevel(score),
                    Address = item["address"]?.GetValue<string>(),
                    Latitude = TryGetDouble(item, "latitude"),
                    Longitude = TryGetDouble(item, "longitude"),
                    SourceProvider = providerName,
                    Highlights = ParseStringArray(item["highlights"]),
                    WhyRecommended = item["whyRecommended"]?.GetValue<string>(),
                    AgreementCount = 1
                });
            }
            catch { /* skip malformed items, keep parsing the rest */ }
        }
        return results;
    }

    protected static CrossValidationResult ParseValidationJson(string raw, string validatedBy, string originalSource, IReadOnlyList<PlaceRecommendation> originals)
    {
        var json = ExtractJson(raw);
        var validated = new List<ValidatedRecommendation>();

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var node = JsonNode.Parse(json);
                var arr = node?["validations"]?.AsArray();
                if (arr is not null)
                {
                    foreach (var item in arr)
                    {
                        if (item is null) continue;
                        var name = item["name"]?.GetValue<string>() ?? string.Empty;
                        var original = originals.FirstOrDefault(r =>
                            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (original is null) continue;

                        var score = item["validationScore"]?.GetValue<double>() ?? 0.5;
                        validated.Add(new ValidatedRecommendation
                        {
                            Original = original,
                            ValidationScore = Math.Clamp(score, 0.0, 1.0),
                            FlaggedAsInaccurate = item["flaggedAsInaccurate"]?.GetValue<bool>() ?? false,
                            FlaggedAsOutOfRange = item["flaggedAsOutOfRange"]?.GetValue<bool>() ?? false,
                            ValidatorComment = item["comment"]?.GetValue<string>()
                        });
                    }
                }
            }
            catch { }
        }

        // Add entries for any originals not covered by validation
        foreach (var original in originals)
        {
            if (!validated.Any(v => string.Equals(v.Original.Name, original.Name, StringComparison.OrdinalIgnoreCase)))
            {
                validated.Add(new ValidatedRecommendation
                {
                    Original = original,
                    ValidationScore = 0.5,
                    FlaggedAsInaccurate = false,
                    FlaggedAsOutOfRange = false
                });
            }
        }

        return new CrossValidationResult
        {
            ValidatedBy = validatedBy,
            OriginalSource = originalSource,
            ValidatedItems = validated
        };
    }

    protected static List<PlaceRecommendation> ApplySynthesisText(string raw, IReadOnlyList<PlaceRecommendation> scoredCandidates)
    {
        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json)) return scoredCandidates.ToList();

        try
        {
            var node = JsonNode.Parse(json);
            var arr = node?["recommendations"]?.AsArray();
            if (arr is null) return scoredCandidates.ToList();

            var synthMap = new Dictionary<string, (string Desc, List<string> Highlights, string? Why)>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr)
            {
                if (item is null) continue;
                var name = item["name"]?.GetValue<string>() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    synthMap[name] = (
                        item["description"]?.GetValue<string>() ?? string.Empty,
                        ParseStringArray(item["highlights"]),
                        item["whyRecommended"]?.GetValue<string>()
                    );
                }
            }

            return scoredCandidates.Select(r =>
            {
                if (synthMap.TryGetValue(r.Name, out var synth))
                {
                    return r with
                    {
                        Description = !string.IsNullOrWhiteSpace(synth.Desc) ? synth.Desc : r.Description,
                        Highlights = synth.Highlights.Count > 0 ? synth.Highlights : r.Highlights,
                        WhyRecommended = synth.Why ?? r.WhyRecommended,
                        SourceProvider = "Consensus"
                    };
                }
                return r with { SourceProvider = "Consensus" };
            }).ToList();
        }
        catch
        {
            return scoredCandidates.ToList();
        }
    }

    protected static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // 1. Prefer explicit markdown code block
        var match = Regex.Match(raw, @"```(?:json)?\s*([\s\S]*?)\s*```");
        if (match.Success) return BalancedJson(match.Groups[1].Value, 0);

        // 2. Look for our known schema keys — handles thinking models that embed JSON inside reasoning text
        foreach (var key in new[] { "\"recommendations\"", "\"validations\"" })
        {
            var keyIdx = raw.LastIndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (keyIdx >= 0)
            {
                var braceIdx = raw.LastIndexOf('{', keyIdx);
                if (braceIdx >= 0)
                    return BalancedJson(raw, braceIdx);
            }
        }

        // 3. Fall back: first { or [
        var start = raw.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return raw;
        return BalancedJson(raw, start);
    }

    // Extract the balanced JSON object/array starting at startIndex,
    // stopping at the matching close brace so trailing prose doesn't break parsing.
    private static string BalancedJson(string s, int startIndex)
    {
        if (startIndex >= s.Length) return string.Empty;
        char open = s[startIndex];
        char close = open == '{' ? '}' : open == '[' ? ']' : '\0';
        if (close == '\0') return s[startIndex..];

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = startIndex; i < s.Length; i++)
        {
            char c = s[i];
            if (escaped) { escaped = false; continue; }
            if (inString)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == open) depth++;
            else if (c == close && --depth == 0)
                return s[startIndex..(i + 1)];
        }

        return s[startIndex..]; // unterminated — return what we have
    }

    protected static string FormatCategoryLabel(IReadOnlyList<PlaceCategory> categories)
    {
        var specific = categories.Where(c => c != PlaceCategory.All).ToList();
        if (specific.Count == 0) return "interesting places";

        var labels = specific.Select(c => c switch
        {
            PlaceCategory.Restaurant => "restaurants",
            PlaceCategory.Cafe => "cafes",
            PlaceCategory.TouristAttraction => "tourist attractions",
            PlaceCategory.Museum => "museums",
            PlaceCategory.Park => "parks",
            PlaceCategory.Bar => "bars",
            PlaceCategory.Hotel => "hotels",
            PlaceCategory.Shopping => "shopping venues",
            PlaceCategory.Entertainment => "entertainment venues",
            _ => c.ToString().ToLower()
        }).ToList();

        return labels.Count == 1
            ? labels[0]
            : string.Join(", ", labels[..^1]) + " and " + labels[^1];
    }

    private static double? TryGetDouble(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<double>(); }
        catch { return null; }
    }

    private static List<string> ParseStringArray(JsonNode? node)
    {
        var result = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var s = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
            }
        }
        return result;
    }

    protected static ConfidenceLevel ScoreToLevel(double score) => score switch
    {
        >= 0.9 => ConfidenceLevel.VeryHigh,
        >= 0.7 => ConfidenceLevel.High,
        >= 0.4 => ConfidenceLevel.Medium,
        _ => ConfidenceLevel.Low
    };
}
