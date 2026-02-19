using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Configuration;
using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Infrastructure.PlacesProviders;

public class GooglePlacesProvider : IPlacesProvider
{
    private readonly HttpClient _http;
    private readonly GooglePlacesOptions _options;
    private readonly ILogger<GooglePlacesProvider> _logger;

    public string Name => "Google Places";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public GooglePlacesProvider(
        HttpClient http,
        IOptions<GooglePlacesOptions> options,
        ILogger<GooglePlacesProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Place>> SearchNearbyAsync(
        double latitude, double longitude,
        PlaceCategory category,
        int radiusMeters = 1000,
        int maxResults = 20,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return Array.Empty<Place>();

        try
        {
            var includedTypes = MapCategoryToTypes(category);
            var body = new
            {
                includedTypes,
                maxResultCount = Math.Min(maxResults, 20),
                locationRestriction = new
                {
                    circle = new
                    {
                        center = new { latitude, longitude },
                        radius = (double)radiusMeters
                    }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "places:searchNearby")
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
            request.Headers.Add("X-Goog-FieldMask",
                "places.id,places.displayName,places.formattedAddress,places.location,places.rating,places.userRatingCount,places.websiteUri,places.nationalPhoneNumber,places.primaryTypeDisplayName");

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Google Places API returned {Status}: {Error}", response.StatusCode, error);
                return Array.Empty<Place>();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParsePlaces(json, latitude, longitude, category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google Places search failed for ({Lat},{Lng})", latitude, longitude);
            return Array.Empty<Place>();
        }
    }

    private IReadOnlyList<Place> ParsePlaces(string json, double originLat, double originLng, PlaceCategory category)
    {
        var places = new List<Place>();
        try
        {
            var node = JsonNode.Parse(json);
            var arr = node?["places"]?.AsArray();
            if (arr is null) return places;

            foreach (var item in arr)
            {
                if (item is null) continue;
                var name = item["displayName"]?["text"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var lat = item["location"]?["latitude"]?.GetValue<double>() ?? originLat;
                var lng = item["location"]?["longitude"]?.GetValue<double>() ?? originLng;
                var distance = CalculateDistance(originLat, originLng, lat, lng);

                places.Add(new Place
                {
                    Name = name,
                    Address = item["formattedAddress"]?.GetValue<string>(),
                    Latitude = lat,
                    Longitude = lng,
                    Category = category,
                    Rating = TryGetDouble(item, "rating"),
                    UserRatingsTotal = TryGetInt(item, "userRatingCount"),
                    GooglePlaceId = item["id"]?.GetValue<string>(),
                    PhoneNumber = item["nationalPhoneNumber"]?.GetValue<string>(),
                    Website = item["websiteUri"]?.GetValue<string>(),
                    DistanceMeters = distance,
                    IsVerifiedRealPlace = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Google Places response");
        }
        return places;
    }

    private static double? TryGetDouble(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<double>(); }
        catch { return null; }
    }

    private static int? TryGetInt(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<int>(); }
        catch { return null; }
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

    private static string[] MapCategoryToTypes(PlaceCategory category) => category switch
    {
        PlaceCategory.Restaurant => new[] { "restaurant" },
        PlaceCategory.Cafe => new[] { "cafe" },
        PlaceCategory.TouristAttraction => new[] { "tourist_attraction" },
        PlaceCategory.Museum => new[] { "museum" },
        PlaceCategory.Park => new[] { "park" },
        PlaceCategory.Bar => new[] { "bar" },
        PlaceCategory.Hotel => new[] { "lodging" },
        PlaceCategory.Shopping => new[] { "shopping_mall", "store" },
        PlaceCategory.Entertainment => new[] { "amusement_park", "movie_theater", "night_club" },
        _ => new[] { "restaurant", "tourist_attraction", "museum", "park" }
    };
}
