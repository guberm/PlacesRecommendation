using Recommendations.Api.Domain.Enums;

namespace Recommendations.Api.Domain;

public record Place
{
    public string Name { get; init; } = string.Empty;
    public string? Address { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public PlaceCategory Category { get; init; }
    public double? Rating { get; init; }
    public int? UserRatingsTotal { get; init; }
    public string? GooglePlaceId { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Website { get; init; }
    public double DistanceMeters { get; init; }
    public bool IsVerifiedRealPlace { get; init; }
}
