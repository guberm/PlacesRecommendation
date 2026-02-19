namespace Recommendations.Api.Abstractions;

public interface IGeocodingProvider
{
    Task<(double Latitude, double Longitude, string DisplayName)?> GeocodeAsync(
        string address,
        CancellationToken ct = default);

    Task<string?> ReverseGeocodeAsync(
        double latitude,
        double longitude,
        CancellationToken ct = default);
}
