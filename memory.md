# PlacesRecommendation — Memory

Quick-reference for Claude. Full detail in `CLAUDE.md`.

## Repos
- **Public**: https://github.com/guberm/PlacesRecommendation — source code, no secrets
- **Private (deploy)**: https://github.com/guberm/PlacesRecommendation-Deploy — PowerShell scripts with server credentials
- **Live site**: https://places.guber.dev (IIS on Windows Server, behind Cloudflare CDN)

## Stack at a glance
| Layer | Technology |
|---|---|
| Runtime | .NET 10 / ASP.NET Core Minimal APIs |
| Database | EF Core 9 + SQLite (`recommendations.db`) |
| AI providers | OpenAI, Anthropic, Gemini, Azure OpenAI, OpenRouter (5 total) |
| Places data | Google Places API v1 (optional) → Overpass/OSM fallback (free) |
| Geocoding | Photon (photon.komoot.io) — no key needed |
| CDN | Cloudflare — caches `.js`/`.css` at edge by full URL |
| Deployment | IIS via WinRM PowerShell (`deploy/deploy.ps1`) |

## Run locally
```bash
cd src/Recommendations.Api && dotnet run
# http://localhost:5145
```

## Deploy workflow (every time)
1. If FE changed → **bump `?v=YYYYMMDD`** in `wwwroot/index.html` (both app.js and app.css refs)
2. `cd src/Recommendations.Api && dotnet publish -c Release -o ../../publish`
3. `powershell -ExecutionPolicy Bypass -File deploy\deploy.ps1`
4. `powershell -ExecutionPolicy Bypass -File deploy\verify.ps1`

> `deploy/` is in `.gitignore` — clone from private deploy repo separately.

## Critical patterns

### Provider availability — user key bypass
```csharp
// ALL 5 providers must use this pattern:
public bool IsAvailable =>
    UserApiKeyContext.HasEffectiveKey("OpenRouter", _options.ApiKey)
    && (_options.Enabled || UserApiKeyContext.HasUserProvidedKey("OpenRouter"));
```

### JSON sanitization (runs before every JsonNode.Parse)
```csharp
// AiProviderBase.SanitizeJson() fixes:
// 1. number"string" → number  (e.g. 1.0"High" → 1.0)
// 2. trailing commas before } or ]
```

### Overpass fallback (DI factory in Program.cs)
```csharp
builder.Services.AddTransient<IPlacesProvider>(sp => {
    var google = sp.GetRequiredService<GooglePlacesProvider>();
    return google.IsAvailable ? google : sp.GetRequiredService<OverpassPlacesProvider>();
});
```

## File locations
| What | Where |
|---|---|
| All AI providers | `Infrastructure/AiProviders/` (extend `AiProviderBase`) |
| Places providers | `Infrastructure/PlacesProviders/` |
| Geocoding | `NominatimGeocodingProvider.cs` (uses Photon internally) |
| Pipeline steps | `Pipeline/Steps/` (8 steps) |
| API endpoints | `Api/Endpoints/` |
| UI | `wwwroot/index.html`, `wwwroot/css/app.css`, `wwwroot/js/app.js` |
| Config | `appsettings.json` (all API keys empty — use env vars or user-secrets) |

## Cloudflare CDN — cache busting
- Static assets are cached by **full URL including query string**
- `index.html` is **not** cached by Cloudflare (HTML bypasses CDN on free plan)
- After a FE deploy, old `app.js`/`app.css` will be served until:
  - New version query string used (e.g. `?v=20260221`) — **use this**
  - Or Cloudflare cache expires / is manually purged

## OpenRouter model notes
- **Recommended**: `meta-llama/llama-3.3-70b-instruct:free`
- **Avoid**: any reasoning/thinking model (DeepSeek R1, arcee-ai/trinity-mini:free, etc.) — they emit `delta.reasoning` only, no `delta.content` JSON
- Free models list: https://openrouter.ai/models?max_price=0

## .gitignore exclusions (sensitive locals)
```
deploy/     # server credentials — clone from private repo
publish/    # build output
logs/       # runtime logs
bin/ obj/   # build artifacts
.claude/    # Claude working files
```

## Known bugs / fixed
- **WinRM `Copy-Item -Recurse -ToSession wildcard\*`** silently drops nested subdirs → fixed in `deploy.ps1` by iterating `Get-ChildItem -Recurse` file-by-file
- **Cloudflare edge cache** serves stale FE after deploy → fixed by version query string in `index.html`
- **AI `number"string"` malformed JSON** (e.g. arcee-ai/trinity-mini) → fixed by `AiProviderBase.SanitizeJson()`
- **`Enabled: false` blocks user-supplied keys** → fixed via `HasUserProvidedKey()` pattern
