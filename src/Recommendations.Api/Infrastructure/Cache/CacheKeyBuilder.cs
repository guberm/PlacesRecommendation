using System.Security.Cryptography;
using System.Text;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Infrastructure.Cache;

public static class CacheKeyBuilder
{
    private const string Version = "v1";

    public static string Build(double latitude, double longitude, PlaceCategory category, int decimalPlaces = 3)
    {
        var lat = Math.Round(latitude, decimalPlaces, MidpointRounding.AwayFromZero);
        var lng = Math.Round(longitude, decimalPlaces, MidpointRounding.AwayFromZero);
        return $"rec:{Version}:{lat:F3}:{lng:F3}:{category}";
    }

    public static string Build(double latitude, double longitude, IReadOnlyList<PlaceCategory> categories, int decimalPlaces = 3)
    {
        var lat = Math.Round(latitude, decimalPlaces, MidpointRounding.AwayFromZero);
        var lng = Math.Round(longitude, decimalPlaces, MidpointRounding.AwayFromZero);
        var catPart = categories.Count == 1
            ? categories[0].ToString()
            : string.Join("+", categories.OrderBy(c => c.ToString()));
        return $"rec:{Version}:{lat:F3}:{lng:F3}:{catPart}";
    }

    public static string BuildFromAddress(string address, PlaceCategory category)
    {
        var normalized = address.ToLowerInvariant().Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"rec:{Version}:addr:{hash}:{category}";
    }
}
