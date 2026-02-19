namespace Recommendations.Api.Configuration;

public class NominatimOptions
{
    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org";
    public string UserAgent { get; set; } = "RecommendationsApp/1.0";
    public int TimeoutSeconds { get; set; } = 10;
}
