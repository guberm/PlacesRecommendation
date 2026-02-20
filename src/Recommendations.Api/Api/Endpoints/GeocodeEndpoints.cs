using System.Text.Json.Nodes;

namespace Recommendations.Api.Api.Endpoints;

public static class GeocodeEndpoints
{
    // Uses photon.komoot.io — OSM-based, no API key or User-Agent registration required.
    private const string PhotonBaseUrl = "https://photon.komoot.io/api/";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/geocode/suggest", GetSuggestions)
            .WithTags("Geocoding")
            .WithSummary("Address autocomplete suggestions via photon.komoot.io");
    }

    private static async Task<IResult> GetSuggestions(
        string q,
        IHttpClientFactory httpFactory,
        int limit = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Results.Ok(Array.Empty<object>());

        try
        {
            var clampedLimit = Math.Clamp(limit, 1, 10);
            var url = $"{PhotonBaseUrl}?q={Uri.EscapeDataString(q)}&limit={clampedLimit}";

            using var http = httpFactory.CreateClient();
            var json = await http.GetStringAsync(url, ct);

            var root = JsonNode.Parse(json);
            var features = root?["features"]?.AsArray();
            if (features is null || features.Count == 0)
                return Results.Ok(Array.Empty<object>());

            var results = features
                .Where(f => f is not null)
                .Select(f =>
                {
                    var coords = f!["geometry"]?["coordinates"]?.AsArray();
                    if (coords is null || coords.Count < 2) return null;

                    var lng = coords[0]?.GetValue<double>() ?? 0;
                    var lat = coords[1]?.GetValue<double>() ?? 0;

                    var props = f["properties"];
                    var name    = props?["name"]?.GetValue<string>();
                    var street  = props?["street"]?.GetValue<string>();
                    var housenr = props?["housenumber"]?.GetValue<string>();
                    var city    = props?["city"]?.GetValue<string>()
                               ?? props?["town"]?.GetValue<string>()
                               ?? props?["village"]?.GetValue<string>();
                    var state   = props?["state"]?.GetValue<string>();
                    var country = props?["country"]?.GetValue<string>();

                    // Build a readable display name
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(name))   parts.Add(name);
                    if (!string.IsNullOrEmpty(street))
                    {
                        var streetPart = string.IsNullOrEmpty(housenr) ? street : $"{housenr} {street}";
                        if (string.IsNullOrEmpty(name) || !name.Equals(streetPart, StringComparison.OrdinalIgnoreCase))
                            parts.Add(streetPart);
                    }
                    if (!string.IsNullOrEmpty(city))    parts.Add(city);
                    if (!string.IsNullOrEmpty(state))   parts.Add(state);
                    if (!string.IsNullOrEmpty(country)) parts.Add(country);

                    var displayName = parts.Count > 0
                        ? string.Join(", ", parts)
                        : $"{lat:F4}, {lng:F4}";

                    return new { displayName, latitude = lat, longitude = lng };
                })
                .Where(r => r is not null)
                .ToList();

            return Results.Ok(results);
        }
        catch (Exception)
        {
            // Fail silently — autocomplete is a nice-to-have
            return Results.Ok(Array.Empty<object>());
        }
    }
}
