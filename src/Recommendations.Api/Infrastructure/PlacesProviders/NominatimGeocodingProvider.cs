using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Configuration;

namespace Recommendations.Api.Infrastructure.PlacesProviders;

public class NominatimGeocodingProvider : IGeocodingProvider
{
    private readonly HttpClient _http;
    private readonly NominatimOptions _options;
    private readonly ILogger<NominatimGeocodingProvider> _logger;

    public NominatimGeocodingProvider(
        HttpClient http,
        IOptions<NominatimOptions> options,
        ILogger<NominatimGeocodingProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(double Latitude, double Longitude, string DisplayName)?> GeocodeAsync(
        string address, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(address);
            var url = $"/search?q={encoded}&format=json&limit=1&addressdetails=1";
            var response = await _http.GetStringAsync(url, ct);

            var arr = JsonNode.Parse(response)?.AsArray();
            var first = arr?.FirstOrDefault();
            if (first is null)
            {
                _logger.LogWarning("Nominatim found no results for address: {Address}", address);
                return null;
            }

            var lat = double.Parse(first["lat"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            var lon = double.Parse(first["lon"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            var display = first["display_name"]?.GetValue<string>() ?? address;

            _logger.LogInformation("Geocoded '{Address}' → ({Lat}, {Lon})", address, lat, lon);
            return (lat, lon, display);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nominatim geocoding failed for: {Address}", address);
            return null;
        }
    }

    public async Task<string?> ReverseGeocodeAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        try
        {
            var lat = latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon = longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var url = $"/reverse?lat={lat}&lon={lon}&format=json";
            var response = await _http.GetStringAsync(url, ct);

            var node = JsonNode.Parse(response);
            return node?["display_name"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nominatim reverse geocoding failed for ({Lat}, {Lon})", latitude, longitude);
            return null;
        }
    }
}
