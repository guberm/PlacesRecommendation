# Recommendations  Project Overview for Claude

## What This Is
A C# ASP.NET Core Web API + Minimal HTML/JS UI for recommending nearby places (restaurants, tourist attractions, cafes, etc.) by coordinates or address. Uses a multi-AI consensus pipeline with SQLite caching.

**Live demo**: https://places.guber.dev

## GitHub

`https://github.com/guberm/PlacesRecommendation`

Deploy scripts (contain server credentials) are in a **separate private repo**: `https://github.com/guberm/PlacesRecommendation-Deploy`

## Tech Stack
- **.NET 10**  `src/Recommendations.Api/`
- **ASP.NET Core**  Minimal APIs (no controllers)
- **Entity Framework Core 9 + SQLite**  `recommendations.db`
- **AI Providers**: OpenAI GPT-4o, Anthropic Claude, Google Gemini, Azure OpenAI, OpenRouter (5 total)
- **Places Data**: Google Places API v1 (Nearby Search) — optional; **Overpass API** (OpenStreetMap) used as free fallback
- **Geocoding**: [Photon](https://photon.komoot.io/) (OpenStreetMap)  free, no API key required
- **Logging**: Serilog  Console + File (logs/)
- **CDN**: Cloudflare  sits in front of https://places.guber.dev; caches static assets (.js, .css) at edge keyed by full URL including query string

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
```
Only one AI provider key is needed (others degrade gracefully).
OpenRouter has free models  set `AiProviders:OpenRouter:Model` to e.g. `meta-llama/llama-3.3-70b-instruct:free`.

**No Nominatim/UserAgent needed**  geocoding now uses Photon (photon.komoot.io) which requires no authentication.

## Project Structure
```
src/Recommendations.Api/
  Domain/              # Pure domain records: Place, PlaceRecommendation, etc.
  Domain/Enums/        # PlaceCategory, ConfidenceLevel
  Abstractions/        # IAiProvider, IPlacesProvider, IGeocodingProvider, ICacheService
  Configuration/       # Strongly-typed options classes
  Infrastructure/
    AiProviders/       # OpenAiProvider, AnthropicProvider, GeminiProvider, AzureOpenAiProvider, OpenRouterProvider
    PlacesProviders/   # GooglePlacesProvider, NominatimGeocodingProvider (uses Photon internally), OverpassPlacesProvider (free OSM fallback)
    Cache/             # SqliteCacheService, CacheKeyBuilder
    Persistence/       # RecommendationsDbContext, CachedRecommendation entity
  Pipeline/
    Steps/             # 8 pipeline steps (see below)
    PipelineContext.cs # Mutable context passed through steps
    RecommendationOrchestrator.cs # Chains all steps
  Api/
    Endpoints/         # RecommendationEndpoints, HealthEndpoints, GeocodeEndpoints, ModelsEndpoints (Minimal API)
    Validators/        # FluentValidation for RecommendationRequest
  wwwroot/             # index.html, css/app.css, js/app.js (minimal UI)
  Program.cs           # DI registration and startup
  appsettings.json     # Configuration (no secrets  use env vars or user-secrets)
```

## 8-Step AI Pipeline

1. **GeocodeStep**  Addresslat/lng via Photon (photon.komoot.io). Also does reverse geocoding (lat/lngaddress). Falls back to address-only mode if Photon is unreachable.
2. **CacheCheckStep**  SQLite lookup (key: `rec:v1:{lat:F3}:{lng:F3}:{cat}` or `rec:v1:addr:{hash}:{cat}`)
3. **ParallelGenerationStep**  All AI providers generate independently via `Task.WhenAll`
4. **GooglePlacesEnrichmentStep**  Fetch real places, fuzzy-match with AI output (uses Overpass/OSM free fallback when no Google Places key; skipped if geocoding unavailable)
5. **CrossValidationStep**  Each AI validates every other AI's output
6. **ConsensusScoringStep**  Weighted formula: `FinalScore = BaseScore0.4 + ValidationScore0.35 + Bonuses - Penalties`
7. **SynthesisStep**  Fastest AI rewrites polished final descriptions
8. **CacheWriteStep**  Persist to SQLite (awaited, not fire-and-forget  scoped DbContext constraint)

## Geocoding: Photon (photon.komoot.io)

Photon replaced Nominatim entirely. It is OpenStreetMap-based but requires **no API key and no User-Agent header**. Used for:
- Forward geocoding: `GET https://photon.komoot.io/api/?q={address}&limit=1`
- Reverse geocoding: `GET https://photon.komoot.io/reverse?lat={lat}&lon={lng}`
- Autocomplete suggestions in the UI: same `/api/` endpoint, limit 5

Response format: GeoJSON `FeatureCollection` with `geometry.coordinates: [lon, lat]` and `properties.name/city/country`.

The provider class is still named `NominatimGeocodingProvider` (registered as `IGeocodingProvider`).

## Cache Design

- Single category key: `rec:v1:{lat:F3}:{lng:F3}:{category}`  rounds to ~111m grid cells
- Multi-category key: `rec:v1:{lat:F3}:{lng:F3}:{Cat1}+{Cat2}`  sorted alphabetically
- Address fallback key: `rec:v1:addr:{sha256_16}:{category}`
- TTL: 24 hours (configurable via `Cache:DefaultTtlHours`)
- Expired entries purged on startup and randomly during writes

## Multi-Category Support

- `RecommendationRequest.Categories` (`List<PlaceCategory>`) takes priority over `Category`
- `EffectiveCategories` property resolves which to use
- AI prompt adapts: "Recommend restaurants and cafes near..."
- UI: multi-select chip buttons (click multiple categories at once)

## API Endpoints
- `POST /api/recommendations`  Main recommendation endpoint
- `GET /api/providers/status`  Show AI provider availability
- `GET /api/providers/models`  List available models per provider (calls provider APIs dynamically)
- `GET /api/geocode/autocomplete?q=...`  Address suggestions via Photon
- `GET /api/geocode/reverse?lat=...&lng=...`  Reverse geocode via Photon
- `GET /api/recommendations/cache/status`  Cache stats
- `DELETE /api/recommendations/cache`  Purge expired entries
- `GET /api/health`  Health check

## Key Design Decisions
- **Graceful degradation**: Any AI provider failure is caught; system works with remaining providers
- **Photon geocoding**: No auth required, no rate-limiting concerns for normal use
- **Overpass fallback**: `IPlacesProvider` DI factory in `Program.cs` returns `GooglePlacesProvider` if its key is set, otherwise `OverpassPlacesProvider` — zero changes needed in pipeline steps
- **User-supplied API keys bypass `Enabled`**: `UserApiKeyContext.HasUserProvidedKey(providerKey)` lets per-request user keys activate a provider even when `Enabled: false` in appsettings. All 5 `IsAvailable` checks use `(_options.Enabled || HasUserProvidedKey(...))`.
- **IOptions<T> pattern**: All config via `IOptions<AiProviderOptions>` etc.
- **Record types**: All domain models are immutable C# records with `with` expressions
- **CacheWriteStep awaited**: NOT fire-and-forget  scoped DbContext is disposed when request ends
- **OpenRouter streaming**: Uses SSE streaming (`stream:true`) with `HttpCompletionOption.ResponseHeadersRead`; captures `delta.content`, `delta.reasoning_content` AND `delta.reasoning` (different models use different field names)
- **SanitizeJson**: `AiProviderBase.SanitizeJson()` runs before `JsonNode.Parse` on every AI response — strips stray quoted strings after numbers (e.g. `1.0"High"`) and trailing commas that some models generate

## OpenRouter Model Selection

Recommended free models (use instruct/chat variants, not reasoning/thinking models):
- `meta-llama/llama-3.3-70b-instruct:free`  default, reliable JSON output
- `google/gemma-3-27b-it:free`
- `mistralai/mistral-small-3.1-24b-instruct:free`

**Avoid reasoning-only models** like `arcee-ai/trinity-mini:free` or any DeepSeek R1 variant  they emit reasoning traces in `delta.reasoning` only and produce no JSON `delta.content`, causing 0 recommendations parsed.

Browse all free models: https://openrouter.ai/models?max_price=0

## Adding a New AI Provider
1. Create `Infrastructure/AiProviders/MyProvider.cs` extending `AiProviderBase` and implementing `IAiProvider`
2. Register in `Program.cs`: `builder.Services.AddSingleton<IAiProvider, MyProvider>()`
3. Add options to `Configuration/AiProviderOptions.cs` and `appsettings.json`

## MCP Servers

Project ships two MCP servers configured in `.mcp.json` at the repo root. Claude Code auto-loads them on startup.

### recommendations (primary — `mcp/server.mjs`)

Zero-dependency Node.js server (no `npm install`). Wraps the REST API as MCP tools.

**Tools:**
| Tool | Maps to | Description |
|---|---|---|
| `get_recommendations` | `POST /api/recommendations` | Full AI pipeline — address or lat/lng → ranked places |
| `get_providers_status` | `GET /api/providers/status` | Which AI providers are live |
| `geocode_address` | `GET /api/geocode/suggest` | Address → coordinates (Photon) |
| `get_cache_status` | `GET /api/recommendations/cache/status` | SQLite cache stats |

**Config env var:** `RECOMMENDATIONS_API_URL` (default: `http://localhost:5145`)

Requires the .NET service to be running. Uses Node.js 18+ built-in `fetch` — no external packages.

### github (auxiliary)

GitHub MCP server (`@modelcontextprotocol/server-github`). Enables repo management tools (issues, PRs, files, branches).

**Setup:** Set env var `GITHUB_PERSONAL_ACCESS_TOKEN` with a token that has `repo` scope before launching Claude Code.

## Deployment

Site is deployed to IIS at https://places.guber.dev via PowerShell scripts.
Deploy scripts are kept in a **private** repo (`guberm/PlacesRecommendation-Deploy`) and excluded from this repo via `.gitignore`.

### Full deploy workflow

1. **If FE changed** (app.js or app.css): bump the `?v=YYYYMMDD` query string in `wwwroot/index.html` for both `app.css` and `app.js` references — this busts the Cloudflare CDN cache for the new URLs (Cloudflare caches per full URL including query string)
2. Build and publish:
```bash
cd src/Recommendations.Api
dotnet publish -c Release -o ../../publish
```
3. Deploy + configure IIS:
```powershell
powershell -ExecutionPolicy Bypass -File deploy\deploy.ps1
```
4. Verify health:
```powershell
powershell -ExecutionPolicy Bypass -File deploy\verify.ps1
```

The `deploy/` folder is in `.gitignore` — credentials never enter the public repo.

### Cloudflare CDN caching

Static assets (`app.js`, `app.css`) are cached at Cloudflare's edge with `Cache-Control: max-age=60` from the origin but edge TTL may be longer. After deploying new FE files the old version will be served until:
- The URL changes (version query string `?v=YYYYMMDD`) — **preferred approach**
- Or Cloudflare cache expires naturally
- Or cache is manually purged via Cloudflare dashboard/API

## Common Issues
- **FE changes not showing after deploy (Cloudflare)**: Cloudflare caches `app.js`/`app.css` at edge. Bump `?v=YYYYMMDD` in `index.html` before publishing — the new URL is a Cloudflare cache miss and serves fresh content immediately.
- **WinRM recursive copy drops subdirectory contents**: `Copy-Item -Recurse -ToSession wildcard\*` silently skips nested dirs (e.g. `wwwroot/js/`, `wwwroot/css/`). Fixed in `deploy.ps1` — now iterates `Get-ChildItem -Recurse` explicitly and copies file-by-file.
- **No providers available with user key**: Check `IsAvailable` — it must use `(_options.Enabled || HasUserProvidedKey(...))` pattern so user-supplied keys work even when `Enabled: false` in appsettings.
- **No providers available**: Check that at least one `ApiKey` is set in appsettings or passed via `UserApiKeys` in the request
- **AI returns malformed JSON (number+"string" in same field)**: `AiProviderBase.SanitizeJson()` handles this automatically — strips `"High"` suffix from `1.0"High"` and trailing commas before `}` / `]`
- **EF Core SQLite DateTimeOffset error**: Use `DateTime` not `DateTimeOffset` for entity fields  SQLite provider can't translate DateTimeOffset comparisons
- **JSON enum deserialization**: `ConfigureHttpJsonOptions` with `JsonStringEnumConverter` required  enums sent as strings ("Restaurant") not integers
- **OpenRouter empty response**: Check `delta.content` first, then `delta.reasoning_content`, then `delta.reasoning`. Use `BalancedJson()` for extraction (handles trailing prose after JSON). If parse yields 0 results, a diagnostic log entry is written with the raw accumulated text.
- **OpenRouter 404**: Free model was retired. Check https://openrouter.ai/models?max_price=0 for current slugs.
- **Build error with Anthropic.SDK v4**: `Message.Content` is `List<ContentBase>`, use `new TextContent { Text = prompt }`. Response: `(response.Content.FirstOrDefault() as TextContent)?.Text`
