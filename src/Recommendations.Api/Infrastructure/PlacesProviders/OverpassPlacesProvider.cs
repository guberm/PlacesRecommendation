using System.Text.Json;
using System.Text.Json.Nodes;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Infrastructure.PlacesProviders;

/// <summary>
/// Places provider backed by the Overpass API (OpenStreetMap data).
/// Free, no API key required. Used as fallback when Google Places is unavailable.
/// </summary>
public class OverpassPlacesProvider : IPlacesProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<OverpassPlacesProvider> _logger;

    public string Name => "Overpass (OpenStreetMap)";
    public bool IsAvailable => true;

    public OverpassPlacesProvider(HttpClient http, ILogger<OverpassPlacesProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Place>> SearchNearbyAsync(
        double latitude, double longitude,
        PlaceCategory category,
        int radiusMeters = 1000,
        int maxResults = 20,
        CancellationToken ct = default)
    {
        try
        {
            var filters = GetOsmFilters(category);
            var query = BuildQuery(filters, latitude, longitude, radiusMeters, maxResults);

            var content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("data", query)
            ]);

            var response = await _http.PostAsync("interpreter", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Overpass API returned {Status}: {Error}", response.StatusCode, error);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParsePlaces(json, latitude, longitude, category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Overpass places search failed for ({Lat},{Lng})", latitude, longitude);
            return [];
        }
    }

    private static string BuildQuery(
        IReadOnlyList<(string Key, string Value)> filters,
        double lat, double lon, int radius, int maxResults)
    {
        var lines = new List<string>();
        foreach (var (key, value) in filters)
        {
            // Use regex filter (~) when value has alternatives (|), otherwise exact match (=)
            var valueFilter = value.Contains('|') ? $"~\"{value}\"" : $"=\"{value}\"";
            lines.Add($"  node[\"{key}\"{valueFilter}](around:{radius},{lat},{lon});");
            lines.Add($"  way[\"{key}\"{valueFilter}](around:{radius},{lat},{lon});");
        }

        var unionBody = string.Join("\n", lines);
        return $"[out:json][timeout:15];\n(\n{unionBody}\n);\nout body center qt {maxResults};";
    }

    // Maps PlaceCategory to OSM tag filters. Pipe-separated values use Overpass regex matching.
    private static IReadOnlyList<(string Key, string Value)> GetOsmFilters(PlaceCategory category) =>
        category switch
        {
            PlaceCategory.Restaurant      => [("amenity", "restaurant")],
            PlaceCategory.Cafe            => [("amenity", "cafe")],
            PlaceCategory.Bar             => [("amenity", "bar")],
            PlaceCategory.Museum          => [("tourism", "museum")],
            PlaceCategory.Park            => [("leisure", "park")],
            PlaceCategory.Hotel           => [("tourism", "hotel")],
            PlaceCategory.TouristAttraction => [("tourism", "attraction")],
            PlaceCategory.Shopping        => [("shop", "mall|supermarket|department_store"), ("amenity", "marketplace")],
            PlaceCategory.Entertainment   => [("amenity", "cinema|nightclub|theatre"), ("leisure", "amusement_park")],
            _                             =>
            [
                ("amenity", "restaurant|cafe|bar"),
                ("tourism", "attraction|museum"),
                ("leisure", "park"),
            ],
        };

    private IReadOnlyList<Place> ParsePlaces(
        string json, double originLat, double originLng, PlaceCategory category)
    {
        var places = new List<Place>();
        try
        {
            var root = JsonNode.Parse(json);
            var elements = root?["elements"]?.AsArray();
            if (elements is null) return places;

            foreach (var el in elements)
            {
                if (el is null) continue;
                var tags = el["tags"];
                if (tags is null) continue;

                var name = tags["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Nodes have lat/lon directly; ways have a "center" object from `out center`
                double lat, lng;
                var center = el["center"];
                if (center is not null)
                {
                    lat = center["lat"]?.GetValue<double>() ?? originLat;
                    lng = center["lon"]?.GetValue<double>() ?? originLng;
                }
                else
                {
                    lat = el["lat"]?.GetValue<double>() ?? originLat;
                    lng = el["lon"]?.GetValue<double>() ?? originLng;
                }

                places.Add(new Place
                {
                    Name = name,
                    Address = BuildAddress(tags),
                    Latitude = lat,
                    Longitude = lng,
                    Category = category,
                    Rating = ParseStars(tags),
                    PhoneNumber = tags["phone"]?.GetValue<string>()
                               ?? tags["contact:phone"]?.GetValue<string>(),
                    Website = tags["website"]?.GetValue<string>()
                            ?? tags["contact:website"]?.GetValue<string>(),
                    DistanceMeters = CalculateDistance(originLat, originLng, lat, lng),
                    IsVerifiedRealPlace = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Overpass API response");
        }
        return places;
    }

    private static string? BuildAddress(JsonNode tags)
    {
        var street      = tags["addr:street"]?.GetValue<string>();
        var housenumber = tags["addr:housenumber"]?.GetValue<string>();
        var city        = tags["addr:city"]?.GetValue<string>();
        var postcode    = tags["addr:postcode"]?.GetValue<string>();

        if (street is not null)
        {
            var line1 = housenumber is not null ? $"{housenumber} {street}" : street;
            var parts = new[] { line1, city, postcode }.Where(p => p is not null);
            return string.Join(", ", parts);
        }

        return tags["addr:full"]?.GetValue<string>();
    }

    // OSM "stars" tag is occasionally set on hotels/attractions (integer value 1-5)
    private static double? ParseStars(JsonNode tags)
    {
        var raw = tags["stars"]?.GetValue<string>();
        if (raw is not null && double.TryParse(raw, out var v))
            return Math.Clamp(v, 1.0, 5.0);
        return null;
    }

    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
