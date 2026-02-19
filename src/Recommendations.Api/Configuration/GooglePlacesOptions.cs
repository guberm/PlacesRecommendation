namespace Recommendations.Api.Configuration;

public class GooglePlacesOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://places.googleapis.com/v1";
    public int DefaultRadiusMeters { get; set; } = 1000;
    public int MaxResults { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 10;
}
