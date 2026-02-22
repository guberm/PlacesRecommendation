#!/usr/bin/env node
/**
 * Place Recommendations — MCP Server
 *
 * Exposes the Recommendations REST API as MCP tools so any MCP client
 * (Claude Code, Claude Desktop, etc.) can get place recommendations,
 * check provider status, and geocode addresses — without a browser.
 *
 * Transport : stdio (newline-delimited JSON-RPC 2.0)
 * Deps      : none — uses Node.js 18+ built-in fetch
 *
 * Config env vars:
 *   RECOMMENDATIONS_API_URL  Base URL of the running .NET API (default: http://localhost:5145)
 */

const API_BASE = (process.env.RECOMMENDATIONS_API_URL ?? 'http://localhost:5145').replace(/\/$/, '');

// ─── Tool definitions ────────────────────────────────────────────────────────

const TOOLS = [
  {
    name: 'get_recommendations',
    description:
      'Get AI-consensus place recommendations near a location. ' +
      'Pass either an address OR latitude+longitude. ' +
      'Returns ranked places with names, descriptions, ratings, distances and confidence scores.',
    inputSchema: {
      type: 'object',
      properties: {
        address: {
          type: 'string',
          description: 'Address or place name to search near (e.g. "Eiffel Tower, Paris")',
        },
        latitude: { type: 'number', description: 'Latitude (required if no address)' },
        longitude: { type: 'number', description: 'Longitude (required if no address)' },
        categories: {
          type: 'array',
          items: {
            type: 'string',
            enum: [
              'All', 'Restaurant', 'Cafe', 'TouristAttraction',
              'Museum', 'Park', 'Bar', 'Hotel', 'Shopping', 'Entertainment',
            ],
          },
          description: 'Place categories to search for (default: ["All"])',
        },
        radiusMeters: {
          type: 'integer',
          description: 'Search radius in metres, 500–5000 (default: 1000)',
          minimum: 500,
          maximum: 5000,
        },
        maxResults: {
          type: 'integer',
          description: 'Maximum results to return, 5–20 (default: 10)',
          minimum: 5,
          maximum: 20,
        },
        forceRefresh: {
          type: 'boolean',
          description: 'Bypass the 24-hour cache (default: false)',
        },
      },
    },
  },
  {
    name: 'get_providers_status',
    description:
      'Check which AI providers (OpenAI, Claude, Gemini, Azure OpenAI, OpenRouter) ' +
      'and the Google Places API are currently available on the server.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'geocode_address',
    description:
      'Search for address suggestions and get their coordinates. ' +
      'Returns up to `limit` results with display name, latitude and longitude.',
    inputSchema: {
      type: 'object',
      required: ['query'],
      properties: {
        query: { type: 'string', description: 'Address or place name to search for' },
        limit: {
          type: 'integer',
          description: 'Max suggestions to return, 1–10 (default: 5)',
          minimum: 1,
          maximum: 10,
        },
      },
    },
  },
  {
    name: 'get_cache_status',
    description: 'Get SQLite recommendation cache statistics (total entries, hit counts, TTL info).',
    inputSchema: { type: 'object', properties: {} },
  },
];

// ─── Tool handlers ───────────────────────────────────────────────────────────

async function callTool(name, args) {
  switch (name) {
    case 'get_recommendations': {
      const body = {
        address:      args.address      ?? null,
        latitude:     args.latitude     ?? null,
        longitude:    args.longitude    ?? null,
        categories:   args.categories   ?? [],
        radiusMeters: args.radiusMeters ?? 1000,
        maxResults:   args.maxResults   ?? 10,
        forceRefresh: args.forceRefresh ?? false,
      };
      const res  = await fetch(`${API_BASE}/api/recommendations`, {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify(body),
      });
      const data = await res.json();
      if (!res.ok) {
        return { isError: true, content: [{ type: 'text', text: `API error ${res.status}: ${JSON.stringify(data)}` }] };
      }
      // Summarise the top results for readability, then attach raw JSON
      const recs    = data.recommendations ?? [];
      const lines   = recs.map((r, i) =>
        `${i + 1}. ${r.name}${r.rating ? ` ★${r.rating}` : ''}${r.distanceMeters ? ` — ${(r.distanceMeters / 1000).toFixed(1)} km` : ''}\n   ${r.description ?? ''}`
      );
      const summary = lines.length
        ? `Found ${lines.length} recommendations near ${data.resolvedAddress ?? 'the location'}:\n\n${lines.join('\n\n')}`
        : 'No recommendations found.';
      return {
        content: [
          { type: 'text', text: summary },
          { type: 'text', text: '\n\n--- Full JSON ---\n' + JSON.stringify(data, null, 2) },
        ],
      };
    }

    case 'get_providers_status': {
      const res  = await fetch(`${API_BASE}/api/providers/status`);
      const data = await res.json();
      const providers = (data.providers ?? [])
        .map(p => `${p.available ? '✓' : '✗'} ${p.name}`)
        .join('\n');
      const places = data.googlePlacesConfigured ? '✓ Google Places' : '✗ Google Places (using OSM/Overpass fallback)';
      return {
        content: [{ type: 'text', text: `AI Providers:\n${providers}\n\nPlaces:\n${places}` }],
      };
    }

    case 'geocode_address': {
      const params = new URLSearchParams({ q: args.query, limit: String(args.limit ?? 5) });
      const res    = await fetch(`${API_BASE}/api/geocode/suggest?${params}`);
      const data   = await res.json();
      if (!Array.isArray(data) || data.length === 0) {
        return { content: [{ type: 'text', text: 'No results found.' }] };
      }
      const lines = data.map((r, i) => `${i + 1}. ${r.displayName}  (${r.latitude}, ${r.longitude})`);
      return { content: [{ type: 'text', text: lines.join('\n') }] };
    }

    case 'get_cache_status': {
      const res  = await fetch(`${API_BASE}/api/recommendations/cache/status`);
      const data = await res.json();
      return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
    }

    default:
      return { isError: true, content: [{ type: 'text', text: `Unknown tool: ${name}` }] };
  }
}

// ─── JSON-RPC / MCP stdio transport ─────────────────────────────────────────

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

async function handle(msg) {
  if (msg.method === 'initialize') {
    send({
      jsonrpc: '2.0',
      id: msg.id,
      result: {
        protocolVersion: '2024-11-05',
        capabilities:    { tools: {} },
        serverInfo:      { name: 'recommendations', version: '1.0.0' },
      },
    });
    return;
  }

  if (msg.method === 'notifications/initialized') return; // no response

  if (msg.method === 'tools/list') {
    send({ jsonrpc: '2.0', id: msg.id, result: { tools: TOOLS } });
    return;
  }

  if (msg.method === 'tools/call') {
    let result;
    try {
      result = await callTool(msg.params.name, msg.params.arguments ?? {});
    } catch (err) {
      result = { isError: true, content: [{ type: 'text', text: `Error: ${err.message}` }] };
    }
    send({ jsonrpc: '2.0', id: msg.id, result });
    return;
  }

  // Unknown method with an id → return error
  if (msg.id !== undefined) {
    send({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: 'Method not found' } });
  }
}

// Read newline-delimited JSON from stdin
let buf = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buf += chunk;
  let nl;
  while ((nl = buf.indexOf('\n')) !== -1) {
    const line = buf.slice(0, nl).trim();
    buf = buf.slice(nl + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    handle(msg).catch((err) => process.stderr.write(`[mcp] unhandled: ${err.message}\n`));
  }
});

process.stderr.write(`[mcp] Place Recommendations server started. API: ${API_BASE}\n`);
