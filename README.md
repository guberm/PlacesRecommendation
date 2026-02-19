# Place Recommendations

An ASP.NET Core Web API + minimal HTML/JS UI that recommends nearby places (restaurants, tourist attractions, cafes, etc.) by coordinates or address.

Uses a **multi-AI consensus pipeline**: multiple AI providers generate recommendations independently, cross-validate each other's output, then a consensus-scoring algorithm ranks the results and a synthesis step produces polished final descriptions.

Results are cached in **SQLite** to avoid redundant AI calls on repeated searches.

---

## Features

- **Multi-AI consensus** — OpenAI GPT-4o, Anthropic Claude, Google Gemini, Azure OpenAI, and OpenRouter all queried in parallel; cross-validation and consensus scoring pick the best results
- **Multi-category filter** — select one or multiple categories (Restaurants + Cafes, etc.)
- **Address or coordinates** — type any address or use lat/lng directly; geocoded via OpenStreetMap Nominatim
- **Google Places enrichment** — real-world ratings, distances, and "Verified ✓" badges when Google Places API key is provided
- **SQLite cache** — 24-hour TTL per location+category grid cell; instant second lookups
- **Minimal responsive UI** — dark/light aware, no CSS frameworks

---

## Quick Start

```bash
# 1. Clone
git clone https://github.com/guberm/Recommendations.git
cd Recommendations/src/Recommendations.Api

# 2. Add at least one AI API key (see Configuration below)
# Edit appsettings.json or set environment variables

# 3. Run
dotnet run

# 4. Open http://localhost:5145
```

---

## Configuration

Edit `src/Recommendations.Api/appsettings.json` or set environment variables.
**At least one AI provider key is required.** Others degrade gracefully when not configured.

| Setting | Environment variable | Description |
|---|---|---|
| `AiProviders:OpenAi:ApiKey` | `AiProviders__OpenAi__ApiKey` | OpenAI API key |
| `AiProviders:Anthropic:ApiKey` | `AiProviders__Anthropic__ApiKey` | Anthropic Claude key |
| `AiProviders:Gemini:ApiKey` | `AiProviders__Gemini__ApiKey` | Google Gemini key |
| `AiProviders:AzureOpenAi:ApiKey` + `Endpoint` | `AiProviders__AzureOpenAi__*` | Azure OpenAI |
| `AiProviders:OpenRouter:ApiKey` | `AiProviders__OpenRouter__ApiKey` | OpenRouter key (supports free models) |
| `GooglePlaces:ApiKey` | `GooglePlaces__ApiKey` | Google Places API v1 (optional) |
| `Nominatim:UserAgent` | — | Set to `YourApp/1.0 (your@email.com)` |

### Free models via OpenRouter

Set `AiProviders:OpenRouter:Model` to any free model slug, for example:
- `arcee-ai/trinity-mini:free`
- `stepfun/step-3.5-flash:free`
- `nvidia/nemotron-3-nano-30b-a3b:free`

Get a free API key at [openrouter.ai](https://openrouter.ai).

---

## API

### `POST /api/recommendations`

```json
{
  "latitude": 43.4769,
  "longitude": -79.7596,
  "address": "Oakville, Ontario, Canada",
  "categories": ["Restaurant", "Cafe"],
  "maxResults": 10,
  "radiusMeters": 1000,
  "forceRefresh": false
}
```

Provide either `latitude`+`longitude` **or** `address` (not both required).
`categories` accepts an array for multi-select; use `"category": "All"` for all types.

**Available categories:** `All`, `Restaurant`, `Cafe`, `TouristAttraction`, `Museum`, `Park`, `Bar`, `Hotel`, `Shopping`, `Entertainment`

### Other endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/providers/status` | Which AI providers are configured |
| `GET` | `/api/recommendations/cache/status` | Cache stats |
| `DELETE` | `/api/recommendations/cache` | Purge expired cache entries |
| `GET` | `/api/health` | Liveness check |

---

## Architecture

### 8-Step AI Pipeline

```
Request → Geocode → Cache? → Parallel AI Generation → Google Places Enrich
        → Cross-Validate → Consensus Score → Synthesize → Cache Write → Response
```

1. **GeocodeStep** — address → lat/lng via Nominatim; coordinates → display name
2. **CacheCheckStep** — SQLite lookup; short-circuits if hit
3. **ParallelGenerationStep** — all configured AI providers queried simultaneously
4. **GooglePlacesEnrichmentStep** — real ratings/distances attached to matched places
5. **CrossValidationStep** — each AI validates every other AI's output
6. **ConsensusScoringStep** — weighted formula: `FinalScore = BaseScore×0.4 + ValidationScore×0.35 + AgreementBonus + RealPlaceBonus - Penalties`
7. **SynthesisStep** — fastest provider writes polished descriptions + highlights
8. **CacheWriteStep** — persists to SQLite with 24h TTL

### Project Structure

```
src/Recommendations.Api/
  Domain/              # Immutable record types
  Abstractions/        # IAiProvider, IPlacesProvider, ICacheService interfaces
  Configuration/       # Strongly-typed options
  Infrastructure/
    AiProviders/       # 5 provider implementations (all extend AiProviderBase)
    PlacesProviders/   # Google Places + Nominatim
    Cache/             # SQLite cache + CacheKeyBuilder
    Persistence/       # EF Core DbContext
  Pipeline/
    Steps/             # 8 pipeline steps
    RecommendationOrchestrator.cs
  Api/
    Endpoints/         # Minimal API endpoints
    Validators/        # FluentValidation
  wwwroot/             # index.html, app.css, app.js
```

---

## Tech Stack

- **.NET 10** / ASP.NET Core Minimal APIs
- **Entity Framework Core 9** + SQLite
- **Azure.AI.OpenAI 2.x** (OpenAI + Azure)
- **Anthropic.SDK 4.x**
- **Mscc.GenerativeAI** (Gemini)
- **OpenRouter** via raw HttpClient (SSE streaming)
- **Nominatim** (OpenStreetMap) for geocoding — free, no key needed
- **FluentValidation**, **Serilog**

---

## License

MIT
