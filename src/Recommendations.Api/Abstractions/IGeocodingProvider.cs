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

    /// <summary>Returns up to <paramref name="limit"/> address suggestions for <paramref name="query"/>.</summary>
    Task<IReadOnlyList<(string DisplayName, double Latitude, double Longitude)>> SearchSuggestionsAsync(
        string query,
        int limit = 5,
        CancellationToken ct = default);
}
