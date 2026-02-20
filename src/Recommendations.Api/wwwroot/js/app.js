'use strict';

let activeTab = 'address';
let geolocatedLat = null;
let geolocatedLng = null;

// Multi-select category state
let selectedCategories = new Set(['All']);

// Autocomplete state
let autocompleteTimeout = null;
let suppressAutocomplete = false;

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

function switchTab(tab, btn) {
  activeTab = tab;
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
  document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
  document.getElementById(tab + 'Tab').classList.add('active');
  const activeBtn = btn || document.querySelector(`.tab-btn[data-tab="${tab}"]`);
  if (activeBtn) activeBtn.classList.add('active');
}

async function loadProviderStatus() {
  try {
    const res = await fetch('/api/providers/status');
    if (!res.ok) return;
    const data = await res.json();
    const container = document.getElementById('providerStatus');

    // Load user-saved keys so we can mark providers green if user has a key
    const savedSettings = loadSettings();

    const allProviders = [
      ...data.providers,
      { name: 'Google Places', available: data.googlePlacesConfigured }
    ];

    container.innerHTML = allProviders.map(p => {
      // If server says unavailable, check if user has provided a key
      const available = p.available || hasUserKeyForProvider(p.name, savedSettings);
      return `
        <div class="provider-dot ${available ? 'active' : 'inactive'}">
          <span class="dot"></span>
          <span>${p.name}${!p.available && available ? ' ✓' : ''}</span>
        </div>`;
    }).join('');
  } catch (e) {
    console.warn('Could not load provider status:', e);
  }
}

function hasUserKeyForProvider(providerName, settings) {
  const n = providerName.toLowerCase();
  if (n.includes('openrouter'))  return !!settings['OpenRouter'];
  if (n.includes('claude') || n.includes('anthropic')) return !!settings['Anthropic'];
  if (n.includes('gemini'))      return !!settings['Gemini'];
  if (n.includes('azure'))       return !!(settings['AzureOpenAI'] && settings['AzureOpenAIEndpoint']);
  if (n.includes('openai') || n.includes('gpt')) return !!settings['OpenAI'];
  if (n.includes('google places')) return !!settings['GooglePlaces'];
  return false;
}

// ─── Address autocomplete ─────────────────────────────────────────────────────

async function fetchSuggestions(query) {
  if (!query || query.length < 2) { hideAutocomplete(); return; }
  try {
    const res = await fetch(`/api/geocode/suggest?q=${encodeURIComponent(query)}`);
    if (!res.ok) return;
    const items = await res.json();
    renderAutocomplete(items);
  } catch (e) {
    console.warn('Autocomplete error:', e);
  }
}

function renderAutocomplete(items) {
  const dropdown = document.getElementById('autocompleteDropdown');
  if (!items || items.length === 0) { dropdown.classList.add('hidden'); return; }
  dropdown.innerHTML = items.map(item =>
    `<div class="autocomplete-item"
          data-name="${escAttr(item.displayName)}"
          data-lat="${item.latitude}"
          data-lng="${item.longitude}"
          onmousedown="selectSuggestion(this)">
       <span class="autocomplete-icon">&#128205;</span>
       <span class="autocomplete-text">${escHtml(item.displayName)}</span>
     </div>`
  ).join('');
  dropdown.classList.remove('hidden');
}

function selectSuggestion(el) {
  suppressAutocomplete = true;
  document.getElementById('address').value = el.dataset.name;
  hideAutocomplete();
  setTimeout(() => { suppressAutocomplete = false; }, 300);
}

function hideAutocomplete() {
  const dropdown = document.getElementById('autocompleteDropdown');
  if (dropdown) dropdown.classList.add('hidden');
}

// ─── Geolocation ──────────────────────────────────────────────────────────────

function detectLocation() {
  const btn = document.getElementById('detectLocationBtn');
  if (!navigator.geolocation) {
    alert('Geolocation is not supported by your browser.');
    return;
  }
  btn.disabled = true;
  btn.innerHTML = '⏳ Detecting…';

  navigator.geolocation.getCurrentPosition(
    async (pos) => {
      const { latitude, longitude } = pos.coords;
      // Store for direct submit (skip geocoding round-trip)
      geolocatedLat = latitude;
      geolocatedLng = longitude;
      // Fill coords tab fields
      document.getElementById('lat').value = latitude.toFixed(6);
      document.getElementById('lng').value = longitude.toFixed(6);

      const addressEl = document.getElementById('address');
      addressEl.dataset.geolocated = 'true';
      addressEl.value = `${latitude.toFixed(5)}, ${longitude.toFixed(5)}`;

      // Try to reverse-geocode to a readable address via photon
      try {
        const url = `https://photon.komoot.io/reverse?lon=${longitude}&lat=${latitude}`;
        const res = await fetch(url);
        if (res.ok) {
          const data = await res.json();
          const f = data?.features?.[0];
          if (f) {
            const p = f.properties;
            const parts = [
              p?.name, p?.street ? (p.housenumber ? `${p.housenumber} ${p.street}` : p.street) : null,
              p?.city ?? p?.town ?? p?.village, p?.state, p?.country
            ].filter(Boolean);
            addressEl.value = parts.join(', ') || `${latitude.toFixed(5)}, ${longitude.toFixed(5)}`;
          } else {
            addressEl.value = `${latitude.toFixed(5)}, ${longitude.toFixed(5)}`;
          }
        } else {
          addressEl.value = `${latitude.toFixed(5)}, ${longitude.toFixed(5)}`;
        }
      } catch {
        addressEl.value = `${latitude.toFixed(5)}, ${longitude.toFixed(5)}`;
      }

      btn.disabled = false;
      btn.innerHTML = '&#128205; My Location';
    },
    (err) => {
      btn.disabled = false;
      btn.innerHTML = '&#128205; My Location';
      const messages = {
        1: 'Location access denied. Please allow location permission in your browser.',
        2: 'Location unavailable. Try again.',
        3: 'Location request timed out.'
      };
      alert(messages[err.code] || 'Could not get your location.');
    },
    { timeout: 10000, enableHighAccuracy: true }
  );
}

// ─── Settings ─────────────────────────────────────────────────────────────────

const SETTINGS_STORAGE_KEY = 'recommendations_settings';

// Maps HTML input IDs (without "settings-" prefix) → backend dictionary keys
// Model overrides follow the convention: model-{Provider} → {Provider}Model
const SETTINGS_KEY_MAP = {
  'key-OpenRouter':       'OpenRouter',
  'model-OpenRouter':     'OpenRouterModel',
  'key-OpenAI':           'OpenAI',
  'model-OpenAI':         'OpenAIModel',
  'key-Anthropic':        'Anthropic',
  'model-Anthropic':      'AnthropicModel',
  'key-Gemini':           'Gemini',
  'model-Gemini':         'GeminiModel',
  'key-AzureOpenAI':      'AzureOpenAI',
  'endpoint-AzureOpenAI': 'AzureOpenAIEndpoint',
  'model-AzureOpenAI':    'AzureOpenAIModel',
  'key-GooglePlaces':     'GooglePlaces'
};

function openSettings() {
  loadSettingsIntoModal();
  document.getElementById('settingsModal').classList.remove('hidden');
  document.body.classList.add('modal-open');
}

function closeSettings() {
  document.getElementById('settingsModal').classList.add('hidden');
  document.body.classList.remove('modal-open');
}

function handleModalOverlayClick(e) {
  if (e.target === e.currentTarget) closeSettings();
}

function loadSettings() {
  try { return JSON.parse(localStorage.getItem(SETTINGS_STORAGE_KEY) || '{}'); }
  catch { return {}; }
}

function loadSettingsIntoModal() {
  const settings = loadSettings();
  for (const [fieldId, backendKey] of Object.entries(SETTINGS_KEY_MAP)) {
    const el = document.getElementById('settings-' + fieldId);
    if (!el) continue;
    const savedVal = settings[backendKey] || '';
    if (el.tagName === 'SELECT' && savedVal) {
      // If the saved model isn't yet an option, add it so it can be shown
      if (!selectHasValue(el, savedVal)) {
        const opt = document.createElement('option');
        opt.value = savedVal;
        opt.textContent = savedVal + ' (saved)';
        el.appendChild(opt);
      }
    }
    el.value = savedVal;
  }
}

function selectHasValue(selectEl, val) {
  return Array.from(selectEl.options).some(o => o.value === val);
}

function saveSettings() {
  const settings = {};
  for (const [fieldId, backendKey] of Object.entries(SETTINGS_KEY_MAP)) {
    const el = document.getElementById('settings-' + fieldId);
    if (el && el.value.trim()) settings[backendKey] = el.value.trim();
  }
  localStorage.setItem(SETTINGS_STORAGE_KEY, JSON.stringify(settings));
  updateSettingsIndicator(settings);
  loadProviderStatus(); // refresh indicators with new keys
  closeSettings();
  const count = Object.keys(settings).length;
  showToast(count > 0
    ? `Settings saved (${count} override${count !== 1 ? 's' : ''})`
    : 'Settings saved — using server defaults for all keys');
}

function clearSettings() {
  localStorage.removeItem(SETTINGS_STORAGE_KEY);
  for (const fieldId of Object.keys(SETTINGS_KEY_MAP)) {
    const el = document.getElementById('settings-' + fieldId);
    if (!el) continue;
    if (el.tagName === 'SELECT') {
      // Reset to only the default option
      while (el.options.length > 1) el.remove(1);
      el.selectedIndex = 0;
    } else {
      el.value = '';
    }
  }
  updateSettingsIndicator({});
  loadProviderStatus(); // refresh indicators after clear
  showToast('All settings cleared — server defaults will be used');
}

function updateSettingsIndicator(settings) {
  const btn = document.getElementById('settingsBtn');
  if (!btn) return;
  const count = Object.values(settings).filter(Boolean).length;
  if (count > 0) {
    btn.classList.add('has-keys');
    btn.title = `Settings (${count} override${count !== 1 ? 's' : ''})`;
  } else {
    btn.classList.remove('has-keys');
    btn.title = 'Settings';
  }
}

function buildUserApiKeys() {
  const settings = loadSettings();
  const keys = {};
  for (const backendKey of Object.values(SETTINGS_KEY_MAP)) {
    if (settings[backendKey]) keys[backendKey] = settings[backendKey];
  }
  return Object.keys(keys).length > 0 ? keys : null;
}

// ─── Model fetching ───────────────────────────────────────────────────────────

async function fetchModels(provider) {
  const keyEl    = document.getElementById(`settings-key-${provider}`);
  const selectEl = document.getElementById(`settings-model-${provider}`);
  const statusEl = document.getElementById(`model-status-${provider}`);
  if (!selectEl || !statusEl) return;

  const apiKey = keyEl?.value.trim() || '';
  const currentVal = selectEl.value;

  statusEl.textContent = 'Loading models…';
  statusEl.className = 'model-status loading';

  try {
    const params = new URLSearchParams({ provider });
    if (apiKey) params.set('apiKey', apiKey);
    if (provider.toLowerCase() === 'azureopenai') {
      const endpointEl = document.getElementById('settings-endpoint-AzureOpenAI');
      const ep = endpointEl?.value.trim();
      if (ep) params.set('endpoint', ep);
    }

    const res = await fetch(`/api/providers/models?${params}`);
    if (!res.ok) throw new Error(`Server returned ${res.status}`);
    const data = await res.json();

    if (!data.models || data.models.length === 0) {
      statusEl.textContent = data.warning || 'No models returned. Enter an API key and try again.';
      statusEl.className = 'model-status warning';
      return;
    }

    populateModelSelect(selectEl, data.models, currentVal);

    if (data.warning) {
      statusEl.textContent = data.warning;
      statusEl.className = 'model-status warning';
    } else {
      statusEl.textContent = `${data.models.length} model${data.models.length !== 1 ? 's' : ''} loaded`;
      statusEl.className = 'model-status success';
      setTimeout(() => { statusEl.textContent = ''; statusEl.className = 'model-status'; }, 3000);
    }
  } catch (e) {
    statusEl.textContent = `Error: ${e.message}`;
    statusEl.className = 'model-status error';
  }
}

function populateModelSelect(selectEl, models, preserveVal) {
  selectEl.innerHTML = '<option value="">(server default)</option>';
  for (const m of models) {
    const opt = document.createElement('option');
    opt.value = m.id;
    opt.textContent = m.name || m.id;
    selectEl.appendChild(opt);
  }
  // Restore previous selection or the currently saved one
  if (preserveVal) {
    selectEl.value = preserveVal;
    if (!selectEl.value) {
      // The saved value wasn't in the loaded list → add it as custom entry
      const opt = document.createElement('option');
      opt.value = preserveVal;
      opt.textContent = preserveVal + ' (custom)';
      selectEl.appendChild(opt);
      selectEl.value = preserveVal;
    }
  }
}

function showToast(message) {
  const toast = document.getElementById('toast');
  if (!toast) return;
  toast.textContent = message;
  toast.classList.remove('hidden', 'toast-hide');
  toast.classList.add('toast-show');
  setTimeout(() => {
    toast.classList.remove('toast-show');
    toast.classList.add('toast-hide');
    setTimeout(() => { toast.classList.add('hidden'); toast.classList.remove('toast-hide'); }, 400);
  }, 2500);
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
  hideAutocomplete();
  const searchBtn = document.getElementById('searchBtn');
  searchBtn.disabled = true;
  const origBtnText = searchBtn.textContent;
  searchBtn.innerHTML = '<span class="btn-spinner"></span> Processing…';

  const maxResults = parseInt(document.getElementById('maxResults').value, 10);
  const radiusMeters = parseInt(document.getElementById('radius').value, 10);
  const forceRefresh = document.getElementById('forceRefresh').checked;
  const userApiKeys = buildUserApiKeys();

  // Build category params: single "All" or multi-select array
  const isAll = selectedCategories.size === 1 && selectedCategories.has('All');
  const request = {
    ...(isAll ? { category: 'All' } : { categories: Array.from(selectedCategories) }),
    maxResults,
    radiusMeters,
    forceRefresh,
    ...(userApiKeys ? { userApiKeys } : {})
  };

  if (activeTab === 'coords') {
    const lat = parseFloat(document.getElementById('lat').value);
    const lng = parseFloat(document.getElementById('lng').value);
    if (isNaN(lat) || isNaN(lng)) {
      showError('Please enter valid latitude and longitude.');
      searchBtn.disabled = false;
      searchBtn.textContent = origBtnText;
      return;
    }
    request.latitude = lat;
    request.longitude = lng;
  } else {
    const addressEl = document.getElementById('address');
    const addr = addressEl.value.trim();
    if (!addr) {
      showError('Please enter an address or place name.');
      searchBtn.disabled = false;
      searchBtn.textContent = origBtnText;
      return;
    }
    // If address was filled by geolocation, send coordinates directly
    if (addressEl.dataset.geolocated === 'true' && geolocatedLat !== null && geolocatedLng !== null) {
      request.latitude = geolocatedLat;
      request.longitude = geolocatedLng;
    } else {
      request.address = addr;
    }
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
    searchBtn.disabled = false;
    searchBtn.textContent = origBtnText;

    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: res.statusText }));
      // ProblemDetails uses "detail", custom errors use "error" or "errors"
      showError(err.detail || err.error || err.errors?.join(', ') || res.statusText || 'Request failed.');
      return;
    }

    const data = await res.json();
    renderResults(data);
  } catch (e) {
    stopProgress();
    searchBtn.disabled = false;
    searchBtn.textContent = origBtnText;
    showError('Network error: ' + e.message);
  } finally {
    searchBtn.disabled = false;
    searchBtn.textContent = origBtnText;
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

// Initialization
document.addEventListener('DOMContentLoaded', () => {
  loadProviderStatus();
  updateSettingsIndicator(loadSettings());

  const addressInput = document.getElementById('address');
  addressInput.addEventListener('keydown', e => {
    if (e.key === 'Enter') { hideAutocomplete(); search(); }
    if (e.key === 'Escape') hideAutocomplete();
  });
  addressInput.addEventListener('input', () => {
    // Clear geolocated flag when user manually types
    addressInput.dataset.geolocated = '';
    geolocatedLat = null;
    geolocatedLng = null;
    if (suppressAutocomplete) return;
    clearTimeout(autocompleteTimeout);
    const val = addressInput.value.trim();
    if (val.length < 2) { hideAutocomplete(); return; }
    autocompleteTimeout = setTimeout(() => fetchSuggestions(val), 350);
  });
  addressInput.addEventListener('blur', () => {
    setTimeout(hideAutocomplete, 200);
  });

  document.getElementById('lat').addEventListener('keydown', e => {
    if (e.key === 'Enter') search();
  });
  document.getElementById('lng').addEventListener('keydown', e => {
    if (e.key === 'Enter') search();
  });

  // Close settings modal on Escape
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') closeSettings();
  });
});
