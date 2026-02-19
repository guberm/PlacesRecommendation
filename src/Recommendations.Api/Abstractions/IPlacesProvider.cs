using Recommendations.Api.Domain;
using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Abstractions;

public interface IPlacesProvider
{
    string Name { get; }
    bool IsAvailable { get; }

    Task<IReadOnlyList<Place>> SearchNearbyAsync(
        double latitude,
        double longitude,
        PlaceCategory category,
        int radiusMeters = 1000,
        int maxResults = 20,
        CancellationToken ct = default);
}
