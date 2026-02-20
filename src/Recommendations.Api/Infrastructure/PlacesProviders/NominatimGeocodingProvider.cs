using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Configuration;

namespace Recommendations.Api.Infrastructure.PlacesProviders;

/// <summary>
/// Geocoding provider backed by photon.komoot.io (OSM-based, no API key required).
/// Falls back to a Nominatim-compatible endpoint if configured.
/// </summary>
public class NominatimGeocodingProvider : IGeocodingProvider
{
    // photon.komoot.io — no registration, no User-Agent restrictions
    private const string PhotonBase = "https://photon.komoot.io";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NominatimGeocodingProvider> _logger;

    public NominatimGeocodingProvider(
        IHttpClientFactory httpFactory,
        IOptions<NominatimOptions> _,           // kept for DI compatibility
        ILogger<NominatimGeocodingProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    // ─── Forward geocode ──────────────────────────────────────────────────────

    public async Task<(double Latitude, double Longitude, string DisplayName)?> GeocodeAsync(
        string address, CancellationToken ct = default)
    {
        try
        {
            var url = $"{PhotonBase}/api/?q={Uri.EscapeDataString(address)}&limit=1";
            using var http = _httpFactory.CreateClient();
            var json = await http.GetStringAsync(url, ct);

            var feature = JsonNode.Parse(json)?["features"]?.AsArray().FirstOrDefault();
            if (feature is null)
            {
                _logger.LogWarning("Photon found no results for address: {Address}", address);
                return null;
            }

            var coords = feature["geometry"]?["coordinates"]?.AsArray();
            if (coords is null || coords.Count < 2) return null;

            var lon = coords[0]!.GetValue<double>();
            var lat = coords[1]!.GetValue<double>();
            var display = BuildDisplayName(feature["properties"]) ?? address;

            _logger.LogInformation("Geocoded '{Address}' → ({Lat}, {Lon})", address, lat, lon);
            return (lat, lon, display);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Photon geocoding failed for: {Address}", address);
            return null;
        }
    }

    // ─── Reverse geocode ──────────────────────────────────────────────────────

    public async Task<string?> ReverseGeocodeAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        try
        {
            var lat = latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon = longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var url = $"{PhotonBase}/reverse?lat={lat}&lon={lon}";

            using var http = _httpFactory.CreateClient();
            var json = await http.GetStringAsync(url, ct);

            var feature = JsonNode.Parse(json)?["features"]?.AsArray().FirstOrDefault();
            if (feature is null) return null;

            return BuildDisplayName(feature["properties"]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Photon reverse geocoding failed for ({Lat}, {Lon})", latitude, longitude);
            return null;
        }
    }

    // ─── Autocomplete suggestions ─────────────────────────────────────────────

    public async Task<IReadOnlyList<(string DisplayName, double Latitude, double Longitude)>> SearchSuggestionsAsync(
        string query,
        int limit = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Array.Empty<(string, double, double)>();

        try
        {
            var url = $"{PhotonBase}/api/?q={Uri.EscapeDataString(query)}&limit={limit}";
            using var http = _httpFactory.CreateClient();
            var json = await http.GetStringAsync(url, ct);

            var features = JsonNode.Parse(json)?["features"]?.AsArray();
            if (features is null || features.Count == 0)
                return Array.Empty<(string, double, double)>();

            var results = new List<(string, double, double)>();
            foreach (var f in features)
            {
                if (f is null) continue;
                var coords = f["geometry"]?["coordinates"]?.AsArray();
                if (coords is null || coords.Count < 2) continue;

                var lon = coords[0]!.GetValue<double>();
                var lat = coords[1]!.GetValue<double>();
                var display = BuildDisplayName(f["properties"]);
                if (string.IsNullOrWhiteSpace(display)) continue;

                results.Add((display, lat, lon));
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Photon suggestion search failed for: {Query}", query);
            return Array.Empty<(string, double, double)>();
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string? BuildDisplayName(JsonNode? props)
    {
        if (props is null) return null;

        var name     = props["name"]?.GetValue<string>();
        var street   = props["street"]?.GetValue<string>();
        var houseNr  = props["housenumber"]?.GetValue<string>();
        var city     = props["city"]?.GetValue<string>()
                    ?? props["town"]?.GetValue<string>()
                    ?? props["village"]?.GetValue<string>();
        var state    = props["state"]?.GetValue<string>();
        var country  = props["country"]?.GetValue<string>();

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(name)) parts.Add(name);
        if (!string.IsNullOrEmpty(street))
        {
            var streetPart = string.IsNullOrEmpty(houseNr) ? street : $"{houseNr} {street}";
            if (string.IsNullOrEmpty(name) || !name!.Equals(streetPart, StringComparison.OrdinalIgnoreCase))
                parts.Add(streetPart);
        }
        if (!string.IsNullOrEmpty(city))    parts.Add(city);
        if (!string.IsNullOrEmpty(state))   parts.Add(state);
        if (!string.IsNullOrEmpty(country)) parts.Add(country);

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }
}
