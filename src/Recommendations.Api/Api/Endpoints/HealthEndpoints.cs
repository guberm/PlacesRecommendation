using Recommendations.Api.Abstractions;

namespace Recommendations.Api.Api.Endpoints;

public static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTimeOffset.UtcNow
        })).WithTags("Health");

        app.MapGet("/api/providers/status", (IEnumerable<IAiProvider> providers, IPlacesProvider placesProvider) =>
        {
            return Results.Ok(new
            {
                providers = providers.Select(p => new
                {
                    name = p.Name,
                    available = p.IsAvailable,
                    configured = p.IsAvailable
                }),
                googlePlacesConfigured = placesProvider.IsAvailable
            });
        }).WithTags("Health");
    }
}
