using Recommendations.Api.Abstractions;

namespace Recommendations.Api.Pipeline.Steps;

public class GeocodeStep
{
    private readonly IGeocodingProvider _geocoder;
    private readonly ILogger<GeocodeStep> _logger;

    public GeocodeStep(IGeocodingProvider geocoder, ILogger<GeocodeStep> logger)
    {
        _geocoder = geocoder;
        _logger = logger;
    }

    public async Task ExecuteAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        var req = ctx.Request;

        if (req.Latitude.HasValue && req.Longitude.HasValue)
        {
            ctx.Latitude = req.Latitude.Value;
            ctx.Longitude = req.Longitude.Value;

            // Reverse-geocode for display name; failure is non-fatal
            var displayName = await _geocoder.ReverseGeocodeAsync(ctx.Latitude, ctx.Longitude, ct);
            ctx.ResolvedAddress = displayName ?? $"({ctx.Latitude:F5}, {ctx.Longitude:F5})";
            _logger.LogInformation("Using coordinates ({Lat}, {Lng}) → {Name}",
                ctx.Latitude, ctx.Longitude, ctx.ResolvedAddress);
            return;
        }

        if (!string.IsNullOrWhiteSpace(req.Address))
        {
            var result = await _geocoder.GeocodeAsync(req.Address, ct);
            if (result is not null)
            {
                ctx.Latitude = result.Value.Latitude;
                ctx.Longitude = result.Value.Longitude;
                ctx.ResolvedAddress = result.Value.DisplayName;
                ctx.GeocodingAvailable = true;
                _logger.LogInformation("Geocoded '{Address}' → ({Lat}, {Lng})",
                    req.Address, ctx.Latitude, ctx.Longitude);
                return;
            }

            // Geocoding failed — degrade gracefully: AI can still recommend by address
            ctx.GeocodingAvailable = false;
            ctx.ResolvedAddress = req.Address;
            ctx.Latitude = 0;
            ctx.Longitude = 0;
            _logger.LogWarning(
                "Geocoding failed for '{Address}'. AI will use address string directly. " +
                "To fix: update Nominatim:UserAgent in appsettings.json to include your real email address " +
                "(e.g. \"MyApp/1.0 (yourname@youremail.com)\"). " +
                "Google Places enrichment and coordinate-based caching are disabled for this request.",
                req.Address);
            return;
        }

        throw new ArgumentException("Request must provide either coordinates (latitude/longitude) or an address.");
    }
}
