namespace Recommendations.Api.Configuration;

public class CacheOptions
{
    public int DefaultTtlHours { get; set; } = 24;
    public int GridPrecisionDecimalPlaces { get; set; } = 3;
    public bool PurgeOnStartup { get; set; } = true;
}
