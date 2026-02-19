'use strict';

let activeTab = 'coords';

// Multi-select category state
let selectedCategories = new Set(['All']);

function toggleCategory(btn) {
  const value = btn.dataset.value;
  if (value === 'All') {
    selectedCategories = new Set(['All']);
  } else {
    selectedCategories.delete('All');
    if (selectedCategories.has(value)) {
      selectedCategories.delete(value);
      if (selectedCategories.size === 0) selectedCategories.add('All');
    } else {
      selectedCategories.add(value);
    }
  }
  document.querySelectorAll('.chip').forEach(c => {
    c.classList.toggle('active', selectedCategories.has(c.dataset.value));
  });
}

function switchTab(tab) {
  activeTab = tab;
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
  document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
  document.getElementById(tab + 'Tab').classList.add('active');
  event.target.classList.add('active');
}

async function loadProviderStatus() {
  try {
    const res = await fetch('/api/providers/status');
    if (!res.ok) return;
    const data = await res.json();
    const container = document.getElementById('providerStatus');

    const allProviders = [
      ...data.providers,
      { name: 'Google Places', available: data.googlePlacesConfigured }
    ];

    container.innerHTML = allProviders.map(p => `
      <div class="provider-dot ${p.available ? 'active' : 'inactive'}">
        <span class="dot"></span>
        <span>${p.name}</span>
      </div>
    `).join('');
  } catch (e) {
    console.warn('Could not load provider status:', e);
  }
}

const pipelineSteps = [
  { key: 'geocode', label: 'Geocoding location' },
  { key: 'cache', label: 'Checking cache' },
  { key: 'generate', label: 'Querying AI providers (parallel)' },
  { key: 'enrich', label: 'Enriching with Google Places' },
  { key: 'validate', label: 'Cross-validating recommendations' },
  { key: 'score', label: 'Building consensus score' },
  { key: 'synthesize', label: 'Synthesizing final recommendations' },
  { key: 'cache_write', label: 'Saving to cache' }
];

function showProgress() {
  const section = document.getElementById('progressSection');
  const stepsEl = document.getElementById('progressSteps');
  section.classList.remove('hidden');

  stepsEl.innerHTML = pipelineSteps.map((s, i) =>
    `<div class="progress-step ${i === 0 ? 'running' : 'pending'}" id="step-${s.key}">
      <span class="step-icon">${i === 0 ? '<span class="spinner"></span>' : '○'}</span>
      <span>${s.label}</span>
    </div>`
  ).join('');

  // Animate through steps to simulate progress
  let idx = 0;
  const interval = setInterval(() => {
    if (idx < pipelineSteps.length) {
      const prev = document.getElementById('step-' + pipelineSteps[idx]?.key);
      if (prev) {
        prev.classList.remove('running');
        prev.classList.add('done');
        prev.querySelector('.step-icon').innerHTML = '✓';
      }
      idx++;
      const next = document.getElementById('step-' + pipelineSteps[idx]?.key);
      if (next) {
        next.classList.remove('pending');
        next.classList.add('running');
        next.querySelector('.step-icon').innerHTML = '<span class="spinner"></span>';
      }
    } else {
      clearInterval(interval);
    }
  }, 1500);

  return () => clearInterval(interval);
}

function showError(msg) {
  document.getElementById('progressSection').classList.add('hidden');
  const sec = document.getElementById('errorSection');
  sec.classList.remove('hidden');
  document.getElementById('errorMsg').textContent = msg;
}

function hideAll() {
  document.getElementById('progressSection').classList.add('hidden');
  document.getElementById('errorSection').classList.add('hidden');
  document.getElementById('resultsSection').classList.add('hidden');
}

async function search() {
  hideAll();
  document.getElementById('searchBtn').disabled = true;

  const maxResults = parseInt(document.getElementById('maxResults').value, 10);
  const radiusMeters = parseInt(document.getElementById('radius').value, 10);
  const forceRefresh = document.getElementById('forceRefresh').checked;

  // Build category params: single "All" or multi-select array
  const isAll = selectedCategories.size === 1 && selectedCategories.has('All');
  const request = {
    ...(isAll ? { category: 'All' } : { categories: Array.from(selectedCategories) }),
    maxResults,
    radiusMeters,
    forceRefresh
  };

  if (activeTab === 'coords') {
    const lat = parseFloat(document.getElementById('lat').value);
    const lng = parseFloat(document.getElementById('lng').value);
    if (isNaN(lat) || isNaN(lng)) {
      showError('Please enter valid latitude and longitude.');
      document.getElementById('searchBtn').disabled = false;
      return;
    }
    request.latitude = lat;
    request.longitude = lng;
  } else {
    const addr = document.getElementById('address').value.trim();
    if (!addr) {
      showError('Please enter an address or place name.');
      document.getElementById('searchBtn').disabled = false;
      return;
    }
    request.address = addr;
  }

  const stopProgress = showProgress();

  try {
    const res = await fetch('/api/recommendations', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request)
    });

    stopProgress();
    document.getElementById('progressSection').classList.add('hidden');

    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: res.statusText }));
      showError(err.error || err.errors?.join(', ') || 'Request failed.');
      return;
    }

    const data = await res.json();
    renderResults(data);
  } catch (e) {
    stopProgress();
    showError('Network error: ' + e.message);
  } finally {
    document.getElementById('searchBtn').disabled = false;
  }
}

function renderResults(data) {
  const section = document.getElementById('resultsSection');
  section.classList.remove('hidden');

  // Header
  document.getElementById('resultsLocation').textContent =
    data.resolvedAddress || `(${data.latitude.toFixed(4)}, ${data.longitude.toFixed(4)})`;

  const genAt = new Date(data.generatedAt).toLocaleTimeString();
  const catLabel = (data.categories?.length > 1)
    ? data.categories.join(', ')
    : (data.category || 'All');
  document.getElementById('resultsMeta').textContent =
    `${data.recommendations.length} results · ${catLabel} · ${data.metadata.totalElapsed} · Generated ${genAt}`;

  const cacheEl = document.getElementById('cacheIndicator');
  if (data.fromCache) {
    cacheEl.textContent = 'From Cache';
    cacheEl.className = 'badge cached';
  } else {
    cacheEl.textContent = 'Fresh';
    cacheEl.className = 'badge fresh';
  }

  // Cards
  const grid = document.getElementById('resultsGrid');
  grid.innerHTML = data.recommendations.map((rec, i) => renderCard(rec, i + 1)).join('');

  // Metadata
  const meta = data.metadata;
  document.getElementById('metadataContent').innerHTML = `
    <dl class="metadata-grid">
      <dt>Providers Used</dt>
      <dd>${meta.providersUsed.join(', ') || 'None'}</dd>
      <dt>Providers Failed</dt>
      <dd>${meta.providersFailed.length ? meta.providersFailed.join(', ') : 'None'}</dd>
      <dt>Google Places Enriched</dt>
      <dd>${meta.googlePlacesEnriched ? 'Yes' : 'No'}</dd>
      <dt>Total Candidates</dt>
      <dd>${meta.totalCandidatesEvaluated}</dd>
      <dt>Total Elapsed</dt>
      <dd>${meta.totalElapsed}</dd>
      <dt>Synthesized By</dt>
      <dd>${meta.synthesizedBy || '—'}</dd>
    </dl>
  `;
}

function renderCard(rec, rank) {
  const enriched = rec.enrichedPlaceData;
  const confPct = Math.round(rec.confidenceScore * 100);
  const level = rec.confidenceLevel || 'Medium';

  const ratingHtml = enriched?.rating
    ? `<span class="star-rating">★ ${enriched.rating.toFixed(1)}</span>
       <span>(${enriched.userRatingsTotal?.toLocaleString() ?? '?'} reviews)</span>`
    : '';

  const distHtml = enriched?.distanceMeters
    ? `<span>${formatDistance(enriched.distanceMeters)}</span>` : '';

  const verifiedHtml = enriched?.isVerifiedRealPlace
    ? `<span class="verified-badge">✓ Verified</span>` : '';

  const highlightsHtml = rec.highlights?.length
    ? `<div class="highlights">${rec.highlights.map(h =>
        `<span class="highlight-tag">${escHtml(h)}</span>`).join('')}</div>` : '';

  const whyHtml = rec.whyRecommended
    ? `<div class="why-text">${escHtml(rec.whyRecommended)}</div>` : '';

  const mapsUrl = `https://www.google.com/maps/search/${encodeURIComponent(rec.name)}`;
  const mapsUrlCoords = (enriched?.latitude && enriched?.longitude)
    ? `https://www.google.com/maps/search/?api=1&query=${enriched.latitude},${enriched.longitude}`
    : mapsUrl;

  const addressForCopy = rec.address || rec.name;

  return `
    <div class="rec-card">
      <div class="rec-card-header">
        <span class="rec-rank">#${rank}</span>
        <span class="rec-name">${escHtml(rec.name)}</span>
      </div>

      <div class="rec-meta">
        ${verifiedHtml}
        ${ratingHtml}
        ${distHtml}
        ${rec.address ? `<span>${escHtml(rec.address)}</span>` : ''}
      </div>

      <p class="rec-description">${escHtml(rec.description)}</p>

      ${highlightsHtml}

      <div class="confidence-row conf-${level}">
        <div class="conf-bar-bg">
          <div class="conf-bar-fill" style="width:${confPct}%"></div>
        </div>
        <span class="conf-label">${confPct}% ${level}</span>
      </div>

      <div class="agreement-text">
        ${rec.agreementCount} AI${rec.agreementCount !== 1 ? 's' : ''} agreed
        ${enriched?.rating ? ` · Google ${enriched.rating.toFixed(1)}★` : ''}
      </div>

      ${whyHtml}

      <div class="rec-actions">
        <a class="btn-sm" href="${mapsUrlCoords}" target="_blank" rel="noopener">View on Maps</a>
        <button class="btn-sm" onclick="copyToClipboard('${escAttr(addressForCopy)}', this)">Copy Address</button>
      </div>
    </div>
  `;
}

function formatDistance(meters) {
  return meters >= 1000
    ? (meters / 1000).toFixed(1) + ' km'
    : Math.round(meters) + ' m';
}

function escHtml(str) {
  if (!str) return '';
  return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function escAttr(str) {
  if (!str) return '';
  return str.replace(/'/g, "\\'").replace(/"/g, '\\"');
}

async function copyToClipboard(text, btn) {
  try {
    await navigator.clipboard.writeText(text);
    const orig = btn.textContent;
    btn.textContent = 'Copied!';
    setTimeout(() => { btn.textContent = orig; }, 1500);
  } catch {
    btn.textContent = 'Failed';
    setTimeout(() => { btn.textContent = 'Copy Address'; }, 1500);
  }
}

// Handle Enter key
document.addEventListener('DOMContentLoaded', () => {
  loadProviderStatus();

  document.getElementById('address').addEventListener('keydown', e => {
    if (e.key === 'Enter') search();
  });
  document.getElementById('lat').addEventListener('keydown', e => {
    if (e.key === 'Enter') search();
  });
});
