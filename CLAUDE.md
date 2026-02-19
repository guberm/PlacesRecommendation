# Recommendations — Project Overview for Claude

## What This Is
A C# ASP.NET Core Web API + Minimal HTML/JS UI for recommending nearby places (restaurants, tourist attractions, cafes, etc.) by coordinates or address. Uses a multi-AI consensus pipeline with SQLite caching.

## GitHub

`https://github.com/guberm/Recommendations`

## Tech Stack
- **.NET 10** — `src/Recommendations.Api/`
- **ASP.NET Core** — Minimal APIs (no controllers)
- **Entity Framework Core 9 + SQLite** — `recommendations.db`
- **AI Providers**: OpenAI GPT-4o, Anthropic Claude, Google Gemini, Azure OpenAI, OpenRouter (5 total)
- **Places Data**: Google Places API v1 (Nearby Search) + Nominatim (geocoding)
- **Logging**: Serilog → Console + File (logs/)

## How to Run

```bash
cd src/Recommendations.Api
dotnet run
# Open http://localhost:5145 (port from launchSettings.json)
```

## API Keys Setup
Edit `src/Recommendations.Api/appsettings.json` or set environment variables:
```
AiProviders__OpenAi__ApiKey=sk-...
AiProviders__Anthropic__ApiKey=sk-ant-...
AiProviders__Gemini__ApiKey=AIza...
AiProviders__AzureOpenAi__ApiKey=...
AiProviders__AzureOpenAi__Endpoint=https://...
AiProviders__OpenRouter__ApiKey=sk-or-...
GooglePlaces__ApiKey=AIza...
Nominatim__UserAgent=YourApp/1.0 (your@email.com)
```
Only one AI provider key is needed (others degrade gracefully).
OpenRouter has free models — set `AiProviders:OpenRouter:Model` to e.g. `arcee-ai/trinity-mini:free`.

## Project Structure
```
src/Recommendations.Api/
  Domain/              # Pure domain records: Place, PlaceRecommendation, etc.
  Domain/Enums/        # PlaceCategory, ConfidenceLevel
  Abstractions/        # IAiProvider, IPlacesProvider, IGeocodingProvider, ICacheService
  Configuration/       # Strongly-typed options classes
  Infrastructure/
    AiProviders/       # OpenAiProvider, AnthropicProvider, GeminiProvider, AzureOpenAiProvider, OpenRouterProvider
    PlacesProviders/   # GooglePlacesProvider, NominatimGeocodingProvider
    Cache/             # SqliteCacheService, CacheKeyBuilder
    Persistence/       # RecommendationsDbContext, CachedRecommendation entity
  Pipeline/
    Steps/             # 8 pipeline steps (see below)
    PipelineContext.cs # Mutable context passed through steps
    RecommendationOrchestrator.cs # Chains all steps
  Api/
    Endpoints/         # RecommendationEndpoints, HealthEndpoints (Minimal API)
    Validators/        # FluentValidation for RecommendationRequest
  wwwroot/             # index.html, css/app.css, js/app.js (minimal UI)
  Program.cs           # DI registration and startup
  appsettings.json     # Configuration (no secrets — use env vars or user-secrets)
```

## 8-Step AI Pipeline

1. **GeocodeStep** — Address→lat/lng via Nominatim, or reverse-geocode coordinates. Gracefully degrades if Nominatim is blocked (403) — uses address string directly with AI.
2. **CacheCheckStep** — SQLite lookup (key: `rec:v1:{lat:F3}:{lng:F3}:{cat}` or `rec:v1:addr:{hash}:{cat}`)
3. **ParallelGenerationStep** — All AI providers generate independently via `Task.WhenAll`
4. **GooglePlacesEnrichmentStep** — Fetch real places, fuzzy-match with AI output (skipped if geocoding unavailable)
5. **CrossValidationStep** — Each AI validates every other AI's output
6. **ConsensusScoringStep** — Weighted formula: `FinalScore = BaseScore×0.4 + ValidationScore×0.35 + Bonuses - Penalties`
7. **SynthesisStep** — Fastest AI rewrites polished final descriptions
8. **CacheWriteStep** — Persist to SQLite (awaited, not fire-and-forget — scoped DbContext constraint)

## Cache Design

- Single category key: `rec:v1:{lat:F3}:{lng:F3}:{category}` — rounds to ~111m grid cells
- Multi-category key: `rec:v1:{lat:F3}:{lng:F3}:{Cat1}+{Cat2}` — sorted alphabetically
- Address fallback key: `rec:v1:addr:{sha256_16}:{category}`
- TTL: 24 hours (configurable via `Cache:DefaultTtlHours`)
- Expired entries purged on startup and randomly during writes

## Multi-Category Support

- `RecommendationRequest.Categories` (`List<PlaceCategory>`) takes priority over `Category`
- `EffectiveCategories` property resolves which to use
- AI prompt adapts: "Recommend restaurants and cafes near..."
- UI: multi-select chip buttons (click multiple categories at once)

## API Endpoints
- `POST /api/recommendations` — Main recommendation endpoint
- `GET /api/providers/status` — Show AI provider availability
- `GET /api/recommendations/cache/status` — Cache stats
- `DELETE /api/recommendations/cache` — Purge expired entries
- `GET /api/health` — Health check

## Key Design Decisions
- **Graceful degradation**: Any AI provider failure is caught; system works with remaining providers
- **Geocoding fallback**: If Nominatim returns 403 (wrong UserAgent), system continues with address-only mode
- **IOptions<T> pattern**: All config via `IOptions<AiProviderOptions>` etc.
- **Record types**: All domain models are immutable C# records with `with` expressions
- **CacheWriteStep awaited**: NOT fire-and-forget — scoped DbContext is disposed when request ends
- **OpenRouter streaming**: Uses SSE streaming (`stream:true`) with `HttpCompletionOption.ResponseHeadersRead`; captures both `delta.content` and `delta.reasoning_content` (thinking models)

## Adding a New AI Provider
1. Create `Infrastructure/AiProviders/MyProvider.cs` extending `AiProviderBase` and implementing `IAiProvider`
2. Register in `Program.cs`: `builder.Services.AddSingleton<IAiProvider, MyProvider>()`
3. Add options to `Configuration/AiProviderOptions.cs` and `appsettings.json`

## Common Issues
- **No providers available**: Check that at least one `ApiKey` is set in appsettings
- **Geocoding 403**: Nominatim blocks fake emails. Update `Nominatim:UserAgent` with a real email. App degrades gracefully.
- **EF Core SQLite DateTimeOffset error**: Use `DateTime` not `DateTimeOffset` for entity fields — SQLite provider can't translate DateTimeOffset comparisons
- **JSON enum deserialization**: `ConfigureHttpJsonOptions` with `JsonStringEnumConverter` required — enums sent as strings ("Restaurant") not integers
- **OpenRouter empty response**: Some free models use `delta.reasoning_content` instead of `delta.content`. Both are captured. Use `BalancedJson()` for extraction (handles trailing prose after JSON).
- **Build error with Anthropic.SDK v4**: `Message.Content` is `List<ContentBase>`, use `new TextContent { Text = prompt }`. Response: `(response.Content.FirstOrDefault() as TextContent)?.Text`
