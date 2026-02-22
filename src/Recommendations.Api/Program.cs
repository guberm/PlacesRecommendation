using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;
using Recommendations.Api.Abstractions;
using Recommendations.Api.Api.Endpoints;
using Recommendations.Api.Api.Validators;
using Recommendations.Api.Configuration;
using Recommendations.Api.Infrastructure.AiProviders;
using Recommendations.Api.Infrastructure.Cache;
using Recommendations.Api.Infrastructure.Persistence;
using Recommendations.Api.Infrastructure.PlacesProviders;
using Recommendations.Api.Pipeline;
using Recommendations.Api.Pipeline.Steps;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((ctx, config) =>
    config.ReadFrom.Configuration(ctx.Configuration)
          .Enrich.FromLogContext()
          .WriteTo.Console()
          .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day));

// Configuration
builder.Services.Configure<AiProviderOptions>(builder.Configuration.GetSection("AiProviders"));
builder.Services.Configure<GooglePlacesOptions>(builder.Configuration.GetSection("GooglePlaces"));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
builder.Services.Configure<NominatimOptions>(builder.Configuration.GetSection("Nominatim"));

// EF Core + SQLite
builder.Services.AddDbContext<RecommendationsDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=recommendations.db"));

// HTTP Clients
// NominatimGeocodingProvider uses IHttpClientFactory internally (photon.komoot.io)

builder.Services.AddHttpClient<GooglePlacesProvider>(client =>
{
    var baseUrl = builder.Configuration["GooglePlaces:BaseUrl"] ?? "https://places.googleapis.com/v1";
    client.BaseAddress = new Uri(baseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue<int>("GooglePlaces:TimeoutSeconds", 10));
}).AddTransientHttpErrorPolicy(p => p.WaitAndRetryAsync(2, _ => TimeSpan.FromSeconds(1)));

// AI Providers - all registered, each checks its own IsAvailable
builder.Services.AddSingleton<IAiProvider, OpenAiProvider>();
builder.Services.AddSingleton<IAiProvider, AnthropicProvider>();
builder.Services.AddSingleton<IAiProvider, GeminiProvider>();
builder.Services.AddSingleton<IAiProvider, AzureOpenAiProvider>();
builder.Services.AddSingleton<IAiProvider, OpenRouterProvider>();
// Timeout.InfiniteTimeSpan — let OpenRouterProvider's CancellationTokenSource control the cutoff
builder.Services.AddHttpClient("openrouter", client =>
    client.Timeout = Timeout.InfiniteTimeSpan);

// Places & Geocoding
builder.Services.AddScoped<IGeocodingProvider, NominatimGeocodingProvider>();

// Overpass (OpenStreetMap) - free, no API key required, always available
builder.Services.AddHttpClient<OverpassPlacesProvider>(client =>
{
    client.BaseAddress = new Uri("https://overpass-api.de/api/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

// IPlacesProvider: prefer Google Places if API key is configured, otherwise use Overpass
builder.Services.AddTransient<IPlacesProvider>(sp =>
{
    var google = sp.GetRequiredService<GooglePlacesProvider>();
    if (google.IsAvailable) return google;
    return sp.GetRequiredService<OverpassPlacesProvider>();
});

// Cache
builder.Services.AddScoped<ICacheService, SqliteCacheService>();

// Pipeline Steps (scoped - new instance per request)
builder.Services.AddScoped<GeocodeStep>();
builder.Services.AddScoped<CacheCheckStep>();
builder.Services.AddScoped<ParallelGenerationStep>();
builder.Services.AddScoped<GooglePlacesEnrichmentStep>();
builder.Services.AddScoped<CrossValidationStep>();
builder.Services.AddScoped<ConsensusScoringStep>();
builder.Services.AddScoped<SynthesisStep>();
builder.Services.AddScoped<CacheWriteStep>();
builder.Services.AddScoped<RecommendationOrchestrator>();

// Validation
builder.Services.AddValidatorsFromAssemblyContaining<RecommendationRequestValidator>();

// JSON: accept enum names as strings (e.g. "Restaurant" instead of 1)
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// CORS
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Ensure DB is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RecommendationsDbContext>();
    await db.Database.EnsureCreatedAsync();

    var cacheOpts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CacheOptions>>();
    if (cacheOpts.Value.PurgeOnStartup)
    {
        var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
        await cache.PurgeExpiredAsync();
    }
}

app.UseSerilogRequestLogging();
app.UseCors();
app.UseStaticFiles();

RecommendationEndpoints.Map(app);
HealthEndpoints.Map(app);
GeocodeEndpoints.Map(app);
ModelsEndpoint.Map(app);

app.MapFallbackToFile("index.html");

app.Run();
