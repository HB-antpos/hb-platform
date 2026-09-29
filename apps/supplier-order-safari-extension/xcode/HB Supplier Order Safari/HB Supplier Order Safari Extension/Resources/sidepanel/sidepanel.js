// 侧栏：网站会话连接、门店选择、供应商授权、供应商分类采集、采购周期混排/筛选/分页、zh/en 切换
import { categoryErrorMessageKey, resolveInitialLocale, t } from '../lib/i18n.js';
import { BUILD_TARGET } from '../config.js';
import { summarizeProgress } from '../lib/category-crawl.js';
import { normalizeApiOrigin, toApiHostPattern } from '../lib/api-origin.js';
import { createGenerationGuard } from '../lib/dats-state.js';
import { selectVisibleSupplierEntries } from '../lib/supplier-list.js';
import { getPendingLocateChange, matchesStorageArea } from '../lib/storage-compat.js';
import { createSingleFlight } from '../lib/session-handoff.js';
import {
  beginRankingLoad,
  buildProductImageCandidates,
  formatAverageSellingPrice,
  formatSalesRankBand,
  normalizeRankingDays,
  normalizeRankingPageSize,
  normalizeSupplierOptions,
  normalizeStoreOptions,
  normalizeTopSalesPage,
  resolveRankingRetryTarget,
  resolveRankingViewState,
  restoreRankingLoad,
  shouldPreserveManualSupplier,
  transitionRankingPagination,
} from '../lib/ranking.js';
import {
  filterStoreSales,
  normalizeStoreSalesResponse,
} from '../lib/store-sales.js';
import {
  normalizeCycles,
  buildTimeline,
  filterTimeline,
  paginate,
} from '../lib/pagination.js';

const PAGE_SIZE = 20;

function getPreferredLanguages() {
  const languages = globalThis.navigator?.languages;
  if (languages?.length) {
    return Array.from(languages);
  }

  const language = globalThis.navigator?.language;
  return typeof language === 'string' ? [language] : [];
}

let locale = resolveInitialLocale(null, getPreferredLanguages());
let user = null;
let authState = 'checking';
let authReason = null;
let profiles = [];
let storeOptions = [];
let selectedStoreCode = null;
let timeline = [];
let filter = 'all';
let page = 1;
let currentItem = null;
let currentSupplier = null;
let activeView = 'ranking';
let rankingDays = 60;
let rankingPage = 1;
let rankingPageSize = 50;
let rankingData = null;
let rankingLegacyItems = null;
let rankingApiOrigin = null;
let rankingLoading = false;
let rankingError = null;
let rankingRetryTarget = null;
let storeSalesSelection = null;
let storeSalesData = null;
let storeSalesLoading = false;
let storeSalesError = null;
let storeSalesErrorCode = null;
let storeSalesQuery = '';
let rankingScrollTop = 0;
let apiOrigin = null;
let defaultApiOrigin = null;
let localApiOrigin = null;
let supplierListExpanded = false;
let manuallySelectedSupplierCode = null;
let lastDetectedSupplierCode = null;
let activeSupplierRefreshTimer = null;
// 分类采集：以当前标签页识别出的供应商为准（与排行榜的手动选择互不影响）。
let activeTabSupplier = null;
let categoryJob = null;
let categoryHistory = {};
let categorySettings = {};
let categoryError = null;
let categoryBusy = false;
const itemRequestGeneration = createGenerationGuard(0);
const rankingRequestGeneration = createGenerationGuard(0);
const storeSalesRequestGeneration = createGenerationGuard(0);
const activeSupplierRequestGeneration = createGenerationGuard(0);

const el = (id) => document.getElementById(id);
const send = (msg) => chrome.runtime.sendMessage(msg);

function setStatus(text) {
  el('status').textContent = text || '';
}

function formatMessage(key, values = {}) {
  return Object.entries(values).reduce(
    (message, [name, value]) => message.replaceAll(`{${name}}`, String(value)),
    t(locale, key),
  );
}

function renderRankingPercentLabels() {
  const percent = rankingData?.topPercent === 10 ? 10 : 30;
  el('rankingTab').textContent = formatMessage('rankingTab', { percent });
  el('rankingTitle').textContent = formatMessage('rankingTitle', { percent });
  el('rankingBadge').textContent = `TOP ${percent}%`;
}

function scrollRankingToTop() {
  const prefersReducedMotion = globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  el('rankingTitle').scrollIntoView({
    block: 'start',
    behavior: prefersReducedMotion ? 'auto' : 'smooth',
  });
}

function announceRankingPage() {
  el('rankingAnnouncement').textContent = formatMessage('rankingPageChanged', {
    page: rankingData?.page ?? rankingPage,
    totalPages: rankingData?.totalPages ?? 0,
  });
}

function applyI18n() {
  document.documentElement.lang = locale === 'zh' ? 'zh-CN' : 'en';
  document.title = t(locale, 'title');
  el('pageTitle').textContent = t(locale, 'title');
  el('openShopBtn').textContent = t(locale, 'openShop');
  el('recheckBtn').textContent = t(locale, 'recheckSession');
  el('connectedTitle').textContent = t(locale, 'sessionConnectedTitle');
  el('disconnectBtn').textContent = t(locale, 'disconnectExtension');
  el('storeSaveBtn').textContent = t(locale, 'save');
  el('apiTitle').textContent = t(locale, 'apiTitle');
  el('apiRemoteBtn').textContent = t(locale, 'apiRemote');
  el('apiLocalBtn').textContent = t(locale, 'apiLocal');
  el('apiSaveBtn').textContent = t(locale, 'apiApply');
  el('apiOriginInput').placeholder = t(locale, 'apiPlaceholder');
  el('apiOriginInput').setAttribute('aria-label', t(locale, 'apiTitle'));
  el('apiOriginHint').textContent = t(locale, 'apiHint');
  el('storeLabel').textContent = t(locale, 'store');
  el('storeEmpty').textContent = t(locale, 'noPosStore');
  el('supplierTitle').textContent = t(locale, 'supplier');
  el('categoryTitle').textContent = t(locale, 'categoryTitle');
  el('categoryPassiveLabel').textContent = t(locale, 'categoryPassiveLabel');
  el('categoryPassiveHint').textContent = t(locale, 'categoryPassiveHint');
  el('categoryCrawlBtn').textContent = t(locale, 'categoryCrawlAll');
  el('categoryAbortBtn').textContent = t(locale, 'categoryAbort');
  el('categoryResumeBtn').textContent = t(locale, 'categoryResume');
  el('categoryRetryBtn').textContent = t(locale, 'categoryRetryFailed');
  el('categorySafariHint').textContent = t(locale, 'categorySafariHint');
  el('historyTab').textContent = t(locale, 'historyTab');
  renderRankingPercentLabels();
  el('rankingSupplierLabel').textContent = t(locale, 'rankingSupplier');
  el('rankingPeriodLabel').textContent = t(locale, 'rankingPeriod');
  el('rankingPageSizeLabel').textContent = t(locale, 'rankingPageSize');
  el('rankingRetryBtn').textContent = t(locale, 'rankingRetry');
  el('rankingLegacyHint').textContent = t(locale, 'rankingLegacyHint');
  el('storeSalesBackBtn').textContent = `‹ ${t(locale, 'storeSalesBack')}`;
  el('storeSalesTitle').textContent = t(locale, 'storeSalesTitle');
  el('storeSalesTotalLabel').textContent = t(locale, 'storeSalesTotal');
  el('storeSalesSearchLabel').textContent = t(locale, 'storeSalesSearch');
  el('storeSalesSearch').placeholder = t(locale, 'storeSalesSearch');
  el('storeSalesSearch').setAttribute('aria-label', t(locale, 'storeSalesSearch'));
  el('storeSalesStoreHeading').textContent = t(locale, 'storeSalesStore');
  el('storeSalesQuantityHeading').textContent = `${t(locale, 'storeSalesQuantity')} ↓`;
  el('storeSalesRetryBtn').textContent = t(locale, 'rankingRetry');
  document.querySelectorAll('[data-ranking-days]').forEach((button) => {
    button.textContent = `${button.dataset.rankingDays} ${t(locale, 'days')}`;
  });
  el('typeHeading').textContent = t(locale, 'type');
  el('dateHeading').textContent = t(locale, 'date');
  el('orderNoHeading').textContent = t(locale, 'orderNo');
  el('quantityHeading').textContent = t(locale, 'quantity');
  el('priceHeading').textContent = t(locale, 'price');
  el('prevBtn').textContent = t(locale, 'prev');
  el('nextBtn').textContent = t(locale, 'next');
  el('rankingPrevBtn').textContent = t(locale, 'prev');
  el('rankingNextBtn').textContent = t(locale, 'next');
  document.querySelector('.filters [data-filter="all"]').textContent = t(locale, 'all');
  document.querySelector('.filters [data-filter="order"]').textContent = t(locale, 'order');
  document.querySelector('.filters [data-filter="sales"]').textContent = t(locale, 'sales');
  el('localeBtn').textContent = locale === 'zh' ? 'EN' : '中文';
}

function renderApiSettings() {
  el('apiOriginInput').value = apiOrigin || defaultApiOrigin || '';
  el('apiRemoteBtn').classList.toggle('active', !!apiOrigin && apiOrigin === defaultApiOrigin);
  el('apiLocalBtn').classList.toggle('active', !!apiOrigin && apiOrigin === localApiOrigin);
}

function renderAuth() {
  const connected = authState === 'connected' && !!user;
  const showConnectedShell = connected && !storeSalesSelection;
  const authSection = el('authSection');
  authSection.hidden = connected;
  authSection.classList.toggle('checking', authState === 'checking');
  authSection.classList.toggle('needs-website', authState === 'needsWebsite');
  el('userSection').hidden = !showConnectedShell;
  el('storeSection').hidden = !showConnectedShell;
  el('supplierSection').hidden = !showConnectedShell;
  el('openShopBtn').disabled = authState === 'checking';
  el('recheckBtn').disabled = authState === 'checking';

  if (authState === 'checking') {
    el('authStatusTitle').textContent = t(locale, 'sessionCheckingTitle');
    el('authStatusDescription').textContent = t(locale, 'sessionCheckingDescription');
  } else {
    el('authStatusTitle').textContent = t(locale, 'sessionNeedsWebsiteTitle');
    el('authStatusDescription').textContent = authReason === 'API_ORIGIN_MISMATCH'
      ? t(locale, 'apiOriginMismatch')
      : t(locale, 'sessionNeedsWebsiteDescription');
  }

  if (connected) {
    el('userInfo').textContent = (
      user.fullName
      || user.username
      || user.name
      || user.displayName
    ) || t(locale, 'title');
  }
}

function renderStore() {
  const hasStores = storeOptions.length > 0;
  el('storeSelect').hidden = !hasStores;
  el('storeEmpty').hidden = hasStores;
  el('storeSaveBtn').disabled = !hasStores;
  if (hasStores) {
    el('storeSelect').replaceChildren();
    for (const s of storeOptions) {
      const opt = document.createElement('option');
      opt.value = s.code;
      opt.textContent = s.name ? `${s.name} (${s.code})` : s.code;
      el('storeSelect').appendChild(opt);
    }
    if (!storeOptions.some((store) => store.code === selectedStoreCode)) {
      selectedStoreCode = storeOptions[0].code;
    }
    el('storeSelect').value = selectedStoreCode;
  } else {
    selectedStoreCode = null;
  }
}

async function renderSuppliers() {
  const list = el('supplierList');
  list.replaceChildren();
  const entries = [];
  for (const p of profiles) {
    for (const pattern of p.origins || []) {
      let granted = false;
      try {
        granted = await chrome.permissions.contains({ origins: [pattern] });
      } catch {
        granted = false;
      }
      entries.push({ profile: p, pattern, granted });
    }
  }

  const {
    visibleEntries,
    grantedCount,
    hiddenGrantedCount,
  } = selectVisibleSupplierEntries(entries, supplierListExpanded);
  const toggle = el('supplierToggleBtn');
  const hint = el('supplierCollapseHint');
  toggle.hidden = grantedCount === 0;
  toggle.setAttribute('aria-expanded', String(supplierListExpanded));
  toggle.setAttribute('aria-controls', 'supplierList');
  toggle.textContent = supplierListExpanded
    ? t(locale, 'supplierCollapse')
    : formatMessage('supplierExpand', { count: grantedCount });
  hint.hidden = hiddenGrantedCount === 0;
  hint.textContent = formatMessage('supplierCollapsedHint', { count: hiddenGrantedCount });

  for (const { profile: p, pattern, granted } of visibleEntries) {
    const li = document.createElement('li');
    const meta = document.createElement('div');
    meta.className = 'supplier-meta';
    const name = document.createElement('span');
    name.textContent = `${p.displayName} (${p.supplierCode})`;
    const origin = document.createElement('small');
    origin.textContent = pattern;
    meta.append(name, origin);
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.textContent = granted ? t(locale, 'granted') : t(locale, 'grant');
    btn.disabled = granted;
    if (!granted) {
      btn.addEventListener('click', () => grantOrigin(pattern));
    }
    li.append(meta, btn);
    list.appendChild(li);
  }
}

async function grantOrigin(pattern) {
  if (!pattern) return;
  try {
    const granted = await chrome.permissions.request({ origins: [pattern] });
    if (granted) {
      const res = await send({ type: 'REGISTER_ORIGIN', originPattern: pattern });
      setStatus(res && res.ok ? `${t(locale, 'grantSuccess')}: ${pattern}` : (res && res.error) || t(locale, 'grantFailed'));
    } else {
      setStatus(t(locale, 'grantDenied'));
    }
    void renderSuppliers();
  } catch (e) {
    setStatus(`${t(locale, 'grantFailed')}: ${String((e && e.message) || e)}`);
  }
}

const CATEGORY_STATUS_KEYS = {
  running: 'categoryStatusRunning',
  completed: 'categoryStatusCompleted',
  aborted: 'categoryStatusAborted',
  interrupted: 'categoryStatusInterrupted',
  failed: 'categoryStatusFailed',
};

function formatCategoryError(code) {
  return formatMessage(categoryErrorMessageKey(code), { code: code || '—' });
}

function formatCategoryTime(value) {
  const date = new Date(value || '');
  if (!Number.isFinite(date.getTime())) return '—';
  try {
    return date.toLocaleString(locale === 'zh' ? 'zh-CN' : 'en-AU', {
      dateStyle: 'short',
      timeStyle: 'short',
    });
  } catch {
    return date.toISOString();
  }
}

function isCategoryPassiveOn(supplierCode, category) {
  return !!category?.passiveEnabled && categorySettings?.[supplierCode]?.passiveEnabled !== false;
}

// 分类采集区：状态全部来自 storage（会话中的任务 + 本地摘要），刷新侧栏不丢进度。
function renderCategory() {
  const section = el('categorySection');
  const visible = !!user && authState === 'connected' && !storeSalesSelection;
  section.hidden = !visible;
  if (!visible) return;

  const supplier = activeTabSupplier;
  const profile = supplier ? profiles.find((item) => item.supplierCode === supplier.supplierCode) : null;
  const category = profile?.category;
  const enabled = !!category?.enabled && supplier?.supplierCode !== '200';
  el('categorySupplier').textContent = supplier
    ? `${supplier.displayName || supplier.supplierCode} (${supplier.supplierCode})`
    : '';
  const unavailable = el('categoryUnavailable');
  unavailable.hidden = enabled;
  unavailable.textContent = !supplier ? t(locale, 'categoryNoSupplier') : t(locale, 'categoryNotEnabled');
  el('categoryControls').hidden = !enabled;
  if (!enabled) return;

  const toggle = el('categoryPassiveToggle');
  toggle.checked = isCategoryPassiveOn(supplier.supplierCode, category);
  toggle.disabled = !category.passiveEnabled;

  const job = categoryJob?.supplierCode === supplier.supplierCode ? categoryJob : null;
  const otherRunning = categoryJob?.status === 'running' && !job ? categoryJob : null;
  const history = categoryHistory?.[supplier.supplierCode] || null;
  const running = job?.status === 'running';
  const latest = job && job.status !== 'running' ? job : history;
  const crawlAllowed = !!category.crawlEnabled;
  const resumable = !!latest
    && ['interrupted', 'aborted', 'failed'].includes(latest.status)
    && Array.isArray(latest.completedKeys)
    && latest.completedKeys.length > 0
    && (latest.done ?? 0) < (latest.total ?? 0);
  const retryable = !!latest && Array.isArray(latest.failedNodes) && latest.failedNodes.length > 0;

  el('categoryCrawlBtn').hidden = !crawlAllowed || running;
  el('categoryCrawlBtn').disabled = categoryBusy || !!otherRunning;
  el('categoryAbortBtn').hidden = !running;
  el('categoryAbortBtn').disabled = categoryBusy;
  el('categoryResumeBtn').hidden = !crawlAllowed || running || !resumable;
  el('categoryResumeBtn').disabled = categoryBusy || !!otherRunning;
  el('categoryRetryBtn').hidden = !crawlAllowed || running || !retryable;
  el('categoryRetryBtn').disabled = categoryBusy || !!otherRunning;

  const note = el('categoryCrawlNote');
  if (!crawlAllowed) note.textContent = t(locale, 'categoryCrawlUnavailable');
  else if (otherRunning) {
    note.textContent = formatMessage('categoryOtherJobRunning', { supplier: otherRunning.supplierCode });
  } else note.textContent = t(locale, 'categoryKeepTabOpen');
  note.hidden = false;

  const progressJob = job || null;
  const progressWrap = el('categoryProgressWrap');
  progressWrap.hidden = !progressJob;
  if (progressJob) {
    const summary = summarizeProgress(progressJob);
    const progress = el('categoryProgress');
    progress.max = Math.max(1, summary.total);
    progress.value = Math.min(summary.done, Math.max(1, summary.total));
    const parts = [formatMessage('categoryProgress', summary)];
    parts.push(formatMessage('categoryItemsSent', {
      items: progressJob.itemsSent ?? 0,
      pages: progressJob.pages ?? 0,
    }));
    el('categoryProgressText').textContent = parts.join(' · ');
  }

  const status = el('categoryStatus');
  let statusText = '';
  let isError = false;
  if (categoryBusy) statusText = t(locale, 'categoryStarting');
  else if (categoryError) {
    statusText = formatCategoryError(categoryError);
    isError = true;
  } else if (running) {
    statusText = progressJob.current?.name
      ? formatMessage('categoryCurrent', { name: progressJob.current.name })
      : t(locale, 'categoryStatusRunning');
  } else if (job) {
    statusText = t(locale, CATEGORY_STATUS_KEYS[job.status] || 'categoryStatusFailed');
    if (job.errorCode) {
      statusText = `${statusText} · ${formatCategoryError(job.errorCode)}`;
      isError = true;
    }
  }
  status.textContent = statusText;
  status.classList.toggle('error', isError);

  el('categoryLastRun').textContent = history
    ? formatMessage('categoryLastRun', {
      time: formatCategoryTime(history.finishedAt || history.startedAt),
      status: t(locale, CATEGORY_STATUS_KEYS[history.status] || 'categoryStatusFailed'),
      done: history.done ?? 0,
      total: history.total ?? 0,
      failed: history.failed ?? 0,
    })
    : t(locale, 'categoryNever');
  // iOS Safari 会挂起后台标签页，主动采集可能中断，提示用户返回后“继续”。
  el('categorySafariHint').hidden = BUILD_TARGET !== 'safari' || !crawlAllowed;
}

async function startCategoryCrawl(mode) {
  if (!activeTabSupplier || categoryBusy) return;
  categoryBusy = true;
  categoryError = null;
  renderCategory();
  let response;
  try {
    response = await send({
      type: 'CATEGORY_CRAWL_START',
      supplierCode: activeTabSupplier.supplierCode,
      mode,
    });
  } catch {
    response = { ok: false, errorCode: 'CONTENT_SCRIPT_UNAVAILABLE' };
  }
  categoryBusy = false;
  if (response?.job) categoryJob = response.job;
  categoryError = response?.ok ? null : response?.errorCode || 'CONTENT_SCRIPT_UNAVAILABLE';
  renderCategory();
}

async function abortCategoryCrawl() {
  if (categoryBusy) return;
  categoryBusy = true;
  categoryError = null;
  renderCategory();
  let response;
  try {
    response = await send({ type: 'CATEGORY_CRAWL_ABORT' });
  } catch {
    response = null;
  }
  categoryBusy = false;
  if (response?.job) categoryJob = response.job;
  if (response && !response.ok) categoryError = response.errorCode || null;
  renderCategory();
}

async function loadCategoryState() {
  const [local, session] = await Promise.all([
    chrome.storage.local.get(['categoryCaptureSettings', 'categoryCrawlHistory']),
    chrome.storage.session.get('categoryCrawlJob'),
  ]);
  categorySettings = local.categoryCaptureSettings || {};
  categoryHistory = local.categoryCrawlHistory || {};
  categoryJob = session.categoryCrawlJob || null;
}

function renderDataTabs() {
  el('dataTabs').hidden = !user || !!storeSalesSelection;
  el('historyTab').disabled = false;
  el('rankingTab').disabled = false;
  el('historyTab').setAttribute('aria-selected', String(activeView === 'history'));
  el('rankingTab').setAttribute('aria-selected', String(activeView === 'ranking'));
}

async function copyText(value, button) {
  const text = String(value || '').trim();
  if (!text) return;
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const input = document.createElement('textarea');
    input.value = text;
    input.style.position = 'fixed';
    input.style.opacity = '0';
    document.body.appendChild(input);
    input.select();
    document.execCommand('copy');
    input.remove();
  }
  const previous = button.textContent;
  button.textContent = t(locale, 'copied');
  setTimeout(() => {
    button.textContent = previous;
  }, 1200);
}

function attachImageCandidates(image, placeholder, item) {
  const candidates = buildProductImageCandidates(item, rankingApiOrigin || apiOrigin);
  if (candidates.length === 0) {
    image.hidden = true;
    placeholder.hidden = false;
    return;
  }
  let index = 0;
  placeholder.hidden = true;
  image.src = candidates[index];
  image.addEventListener('error', () => {
    index += 1;
    if (index < candidates.length) image.src = candidates[index];
    else {
      image.hidden = true;
      placeholder.hidden = false;
    }
  });
}

function renderRanking() {
  const visible = !!user && activeView === 'ranking' && !storeSalesSelection;
  const section = el('rankingSection');
  section.hidden = !visible;
  if (!visible) return;
  section.setAttribute('aria-busy', String(rankingLoading));
  renderRankingPercentLabels();

  el('rankingSupplier').textContent = currentSupplier
    ? `${currentSupplier.displayName || currentSupplier.supplierCode} (${currentSupplier.supplierCode})`
    : t(locale, 'rankingNoSupplier');
  const supplierSelect = el('rankingSupplierSelect');
  const supplierOptions = normalizeSupplierOptions(profiles);
  supplierSelect.replaceChildren();
  if (!currentSupplier) {
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = t(locale, 'rankingChooseSupplier');
    placeholder.disabled = true;
    placeholder.selected = true;
    supplierSelect.appendChild(placeholder);
  }
  for (const supplier of supplierOptions) {
    const option = document.createElement('option');
    option.value = supplier.code;
    option.textContent = `${supplier.name} (${supplier.code})`;
    supplierSelect.appendChild(option);
  }
  supplierSelect.disabled = supplierOptions.length === 0;
  if (currentSupplier) supplierSelect.value = currentSupplier.supplierCode;
  document.querySelectorAll('[data-ranking-days]').forEach((button) => {
    const active = Number(button.dataset.rankingDays) === rankingDays;
    button.classList.toggle('active', active);
    button.setAttribute('aria-pressed', String(active));
  });
  const pageSizeSelect = el('rankingPageSizeSelect');
  pageSizeSelect.value = String(rankingPageSize);
  pageSizeSelect.disabled = rankingLoading;

  const list = el('rankingList');
  list.replaceChildren();
  const items = rankingData && Array.isArray(rankingData.items) ? rankingData.items : [];
  const hasSupplier = !!currentSupplier;
  const state = el('rankingState');
  const stateText = el('rankingStateText');
  const retry = el('rankingRetryBtn');
  let message = '';
  const viewState = resolveRankingViewState({
    hasSupplier,
    loading: rankingLoading,
    error: rankingError,
    totalRankedCount: rankingData?.totalRankedCount,
  });
  if (viewState === 'no-supplier') message = t(locale, 'rankingNoSupplier');
  else if (viewState === 'loading') message = t(locale, 'rankingLoading');
  else if (viewState === 'error') message = t(locale, 'rankingLoadFailed');
  else if (viewState === 'empty') message = t(locale, 'rankingNoData');
  state.hidden = !message;
  stateText.textContent = message;
  state.setAttribute('aria-live', rankingError ? 'assertive' : 'polite');
  state.title = rankingError || '';
  retry.hidden = !rankingError;
  el('rankingLegacyHint').hidden = rankingData?.mode !== 'legacy';

  if (rankingData) {
    const scope = formatMessage('rankingScope', {
      stores: rankingData.enabledStoreCount ?? 0,
      products: rankingData.totalProductCount ?? 0,
    });
    const dates = rankingData.startDate && rankingData.endDate
      ? ` · ${rankingData.startDate}–${rankingData.endDate}`
      : '';
    el('rankingScope').textContent = `${scope}${dates}`;
  } else {
    el('rankingScope').textContent = '';
  }

  for (const item of items) {
    const li = document.createElement('li');
    li.className = 'ranking-item';

    const rank = document.createElement('span');
    rank.className = 'ranking-rank';
    rank.textContent = `#${item.rank}`;

    const image = document.createElement('img');
    image.className = 'ranking-image';
    image.alt = item.productName || item.itemNumber || '';
    image.loading = 'lazy';
    const imageFrame = document.createElement('div');
    imageFrame.className = 'ranking-image-frame';
    const placeholder = document.createElement('span');
    placeholder.className = 'ranking-image-placeholder';
    placeholder.textContent = '—';
    placeholder.setAttribute('aria-hidden', 'true');
    imageFrame.append(image, placeholder);
    attachImageCandidates(image, placeholder, item);

    const product = document.createElement('div');
    product.className = 'ranking-product';
    const nameRow = document.createElement('div');
    nameRow.className = 'ranking-name-row';
    const name = document.createElement('strong');
    name.textContent = item.productName || item.itemNumber || item.productCode || '—';
    nameRow.appendChild(name);
    const rankBand = item.salesRankBand || (rankingData?.mode === 'legacy' ? 'top-10' : null);
    const rankBandLabel = formatSalesRankBand(rankBand);
    if (rankBandLabel) {
      const band = document.createElement('span');
      band.className = 'ranking-band';
      band.classList.add(`ranking-band-${rankBand}`);
      band.textContent = rankBandLabel;
      nameRow.appendChild(band);
    }
    const codeRow = document.createElement('div');
    codeRow.className = 'ranking-code-row';
    const code = document.createElement('span');
    code.className = 'ranking-code';
    code.title = item.itemNumber || item.productCode || '';
    code.textContent = item.itemNumber || item.productCode || '—';
    const copy = document.createElement('button');
    copy.type = 'button';
    copy.className = 'copy-button';
    copy.textContent = t(locale, 'copy');
    copy.setAttribute('aria-label', `${t(locale, 'copy')} ${code.textContent}`);
    copy.addEventListener('click', () => copyText(code.textContent, copy));
    codeRow.append(code, copy);
    product.append(nameRow, codeRow);

    const metrics = document.createElement('div');
    metrics.className = 'ranking-metrics';
    const sales = document.createElement('button');
    sales.type = 'button';
    sales.className = 'ranking-sales ranking-sales-button';
    const salesLabel = document.createElement('span');
    salesLabel.textContent = t(locale, 'sales');
    const salesValue = document.createElement('strong');
    const numericQuantity = Number(item.salesQuantity ?? 0);
    salesValue.textContent = Number.isFinite(numericQuantity)
      ? numericQuantity.toLocaleString(locale === 'zh' ? 'zh-CN' : 'en-AU')
      : String(item.salesQuantity ?? 0);
    const salesArrow = document.createElement('span');
    salesArrow.className = 'ranking-sales-arrow';
    salesArrow.textContent = '›';
    sales.append(salesLabel, salesValue, salesArrow);
    sales.setAttribute('aria-label', formatMessage('storeSalesOpen', {
      product: item.productName || item.itemNumber || item.productCode || '',
    }));
    sales.dataset.productCode = item.productCode || '';
    sales.dataset.rank = String(item.rank ?? '');
    sales.setAttribute('aria-controls', 'storeSalesSection');
    sales.disabled = !item.productCode
      || !rankingData?.startDate
      || !rankingData?.endDate
      || !rankingData?.snapshotVersion;
    sales.addEventListener('click', () => openStoreSales(item));

    const averagePrice = document.createElement('div');
    averagePrice.className = 'ranking-average-price';
    const averagePriceLabel = document.createElement('span');
    averagePriceLabel.textContent = t(locale, 'averageSellingPrice');
    const averagePriceValue = document.createElement('strong');
    averagePriceValue.textContent = formatAverageSellingPrice(item.averageSellingPrice, locale);
    averagePrice.append(averagePriceLabel, averagePriceValue);
    metrics.append(sales, averagePrice);

    li.append(rank, imageFrame, product, metrics);
    list.appendChild(li);
  }

  // 翻页失败时继续展示旧商品，但隐藏与新 pageSize 偏好不一致的旧分页元数据；只保留“重试”入口。
  const showPager = !rankingError && (rankingData?.totalRankedCount ?? 0) > 0;
  el('rankingPager').hidden = !showPager;
  el('rankingPageInfo').textContent = showPager
    ? formatMessage('rankingPageSummary', {
      page: rankingData.page,
      totalPages: rankingData.totalPages,
      total: rankingData.totalRankedCount,
    })
    : '';
  el('rankingPrevBtn').disabled = rankingLoading || !showPager || rankingData.page <= 1;
  el('rankingNextBtn').disabled = rankingLoading || !showPager || rankingData.page >= rankingData.totalPages;
}

function formatSalesQuantity(value) {
  const quantity = Number(value ?? 0);
  return Number.isFinite(quantity)
    ? quantity.toLocaleString(locale === 'zh' ? 'zh-CN' : 'en-AU', {
      maximumFractionDigits: 2,
    })
    : '0';
}

function resetStoreSalesState() {
  storeSalesRequestGeneration.advance();
  storeSalesSelection = null;
  storeSalesData = null;
  storeSalesLoading = false;
  storeSalesError = null;
  storeSalesErrorCode = null;
  storeSalesQuery = '';
}

function focusRankingSalesButton(target) {
  if (!target?.productCode) return false;
  const button = [...document.querySelectorAll('.ranking-sales-button')].find((candidate) => (
    candidate.dataset.productCode === target.productCode
    && candidate.dataset.rank === String(target.rank ?? '')
  ));
  if (!button) return false;
  button.focus();
  return true;
}

function renderStoreSalesProduct(item) {
  const product = el('storeSalesProduct');
  product.replaceChildren();
  if (!item) return;

  const imageFrame = document.createElement('div');
  imageFrame.className = 'ranking-image-frame store-sales-image-frame';
  const image = document.createElement('img');
  image.className = 'ranking-image';
  image.alt = item.productName || item.itemNumber || '';
  const placeholder = document.createElement('span');
  placeholder.className = 'ranking-image-placeholder';
  placeholder.textContent = '—';
  placeholder.setAttribute('aria-hidden', 'true');
  imageFrame.append(image, placeholder);
  attachImageCandidates(image, placeholder, item);

  const identity = document.createElement('div');
  identity.className = 'store-sales-product-identity';
  const name = document.createElement('strong');
  name.textContent = item.productName || item.itemNumber || item.productCode || '—';
  const code = document.createElement('span');
  code.textContent = `SKU: ${item.itemNumber || item.productCode || '—'}`;
  const supplier = document.createElement('span');
  supplier.textContent = currentSupplier
    ? `${currentSupplier.displayName || currentSupplier.supplierCode} (${currentSupplier.supplierCode})`
    : item.supplierCode || '';
  identity.append(name, code, supplier);
  product.append(imageFrame, identity);
}

function renderStoreSalesList() {
  const list = el('storeSalesList');
  list.replaceChildren();
  const state = el('storeSalesState');
  const stateText = el('storeSalesStateText');
  const retry = el('storeSalesRetryBtn');
  const stores = storeSalesData ? filterStoreSales(storeSalesData.stores, storeSalesQuery) : [];
  let message = '';
  if (storeSalesLoading) message = t(locale, 'storeSalesLoading');
  else if (storeSalesErrorCode === 'RANKING_SNAPSHOT_CHANGED') message = t(locale, 'storeSalesStale');
  else if (storeSalesError) message = t(locale, 'storeSalesLoadFailed');
  else if (storeSalesData && stores.length === 0) message = t(locale, 'storeSalesNoMatch');
  state.hidden = !message;
  stateText.textContent = message;
  state.title = storeSalesError || '';
  retry.hidden = !storeSalesError;
  retry.textContent = storeSalesErrorCode === 'RANKING_SNAPSHOT_CHANGED'
    ? t(locale, 'storeSalesRefreshRanking')
    : t(locale, 'rankingRetry');

  const maximum = Math.max(0, ...stores.map((store) => store.salesQuantity));
  for (const store of stores) {
    const row = document.createElement('li');
    row.className = 'store-sales-row';
    const identity = document.createElement('div');
    identity.className = 'store-sales-store-identity';
    const name = document.createElement('strong');
    name.textContent = store.storeName || store.storeCode;
    const code = document.createElement('span');
    code.textContent = store.storeCode;
    const bar = document.createElement('span');
    bar.className = 'store-sales-bar';
    const fill = document.createElement('span');
    fill.style.width = maximum > 0
      ? `${Math.max(0, store.salesQuantity) / maximum * 100}%`
      : '0%';
    bar.appendChild(fill);
    identity.append(name, code, bar);
    const quantity = document.createElement('strong');
    quantity.className = 'store-sales-row-quantity';
    quantity.textContent = formatSalesQuantity(store.salesQuantity);
    row.append(identity, quantity);
    list.appendChild(row);
  }
}

function renderStoreSales() {
  const visible = !!user && !!storeSalesSelection;
  const section = el('storeSalesSection');
  section.hidden = !visible;
  if (!visible) return;
  section.setAttribute('aria-busy', String(storeSalesLoading));
  renderStoreSalesProduct(storeSalesSelection.item);

  const startDate = storeSalesData?.startDate || storeSalesSelection.startDate;
  const endDate = storeSalesData?.endDate || storeSalesSelection.endDate;
  const dateRange = startDate && endDate ? ` · ${startDate}–${endDate}` : '';
  el('storeSalesPeriod').textContent = `${storeSalesSelection.days} ${t(locale, 'days')}${dateRange}`;
  const storeCount = storeSalesData?.enabledStoreCount
    ?? storeSalesSelection.enabledStoreCount
    ?? 0;
  const total = storeSalesData?.totalSalesQuantity
    ?? storeSalesSelection.item.salesQuantity
    ?? 0;
  el('storeSalesTotal').textContent = formatSalesQuantity(total);
  el('storeSalesStoreCount').textContent = formatMessage('storeSalesStoreCount', {
    count: storeCount,
  });
  const search = el('storeSalesSearch');
  if (document.activeElement !== search) search.value = storeSalesQuery;
  el('storeSalesHelper').hidden = !storeSalesData;
  el('storeSalesHelper').textContent = t(locale, 'storeSalesHelper');
  el('storeSalesFooter').hidden = !storeSalesData;
  el('storeSalesFooterLabel').textContent = formatMessage('storeSalesFooter', {
    count: storeCount,
  });
  el('storeSalesFooterTotal').textContent = formatSalesQuantity(total);
  renderStoreSalesList();
}

async function loadStoreSales() {
  if (!storeSalesSelection) return false;
  const selection = storeSalesSelection;
  const requestGeneration = storeSalesRequestGeneration.advance();
  storeSalesData = null;
  storeSalesLoading = true;
  storeSalesError = null;
  storeSalesErrorCode = null;
  setStatus('');
  render();
  let response;
  try {
    response = await send({
      type: 'SUPPLIER_PRODUCT_STORE_SALES',
      supplierCode: selection.supplierCode,
      productCode: selection.item.productCode,
      days: selection.days,
      startDate: selection.startDate,
      endDate: selection.endDate,
      totalSalesQuantity: selection.totalSalesQuantity,
      snapshotVersion: selection.snapshotVersion,
    });
  } catch (error) {
    response = { ok: false, error: String((error && error.message) || error) };
  }
  if (!storeSalesRequestGeneration.isCurrent(requestGeneration)) return false;
  storeSalesLoading = false;
  if (!response?.ok) {
    storeSalesError = response?.error || t(locale, 'storeSalesLoadFailed');
    storeSalesErrorCode = response?.errorCode || null;
    render();
    return false;
  }
  try {
    storeSalesData = normalizeStoreSalesResponse(response.data, {
      supplierCode: selection.supplierCode,
      productCode: selection.item.productCode,
      days: selection.days,
      startDate: selection.startDate,
      endDate: selection.endDate,
      totalSalesQuantity: selection.totalSalesQuantity,
      snapshotVersion: selection.snapshotVersion,
    });
  } catch (error) {
    storeSalesError = String((error && error.message) || error);
    storeSalesErrorCode = null;
    render();
    return false;
  }
  rankingApiOrigin = response.apiOrigin || apiOrigin;
  el('storeSalesAnnouncement').textContent = `${t(locale, 'storeSalesTitle')}: ${formatSalesQuantity(
    storeSalesData.totalSalesQuantity,
  )}`;
  render();
  return true;
}

function openStoreSales(item) {
  if (!item?.productCode || !currentSupplier) return;
  rankingScrollTop = globalThis.scrollY || 0;
  storeSalesSelection = {
    item,
    supplierCode: currentSupplier.supplierCode,
    days: rankingDays,
    startDate: rankingData?.startDate,
    endDate: rankingData?.endDate,
    enabledStoreCount: rankingData?.enabledStoreCount,
    totalSalesQuantity: Number(item.salesQuantity ?? 0),
    snapshotVersion: rankingData?.snapshotVersion,
  };
  storeSalesQuery = '';
  void loadStoreSales();
  globalThis.requestAnimationFrame?.(() => {
    globalThis.scrollTo({ top: 0, behavior: 'auto' });
    el('storeSalesBackBtn').focus();
  });
}

function closeStoreSales() {
  const scrollTop = rankingScrollTop;
  const focusTarget = storeSalesSelection
    ? { productCode: storeSalesSelection.item.productCode, rank: storeSalesSelection.item.rank }
    : null;
  resetStoreSalesState();
  render();
  globalThis.requestAnimationFrame?.(() => {
    globalThis.scrollTo({ top: scrollTop, behavior: 'auto' });
    if (!focusRankingSalesButton(focusTarget)) el('rankingSupplierSelect').focus();
  });
}

async function refreshRankingAfterStaleStoreSales() {
  const scrollTop = rankingScrollTop;
  const focusTarget = storeSalesSelection
    ? { productCode: storeSalesSelection.item.productCode, rank: storeSalesSelection.item.rank }
    : null;
  resetStoreSalesState();
  render();
  await loadRanking({ clear: true });
  globalThis.requestAnimationFrame?.(() => {
    globalThis.scrollTo({ top: scrollTop, behavior: 'auto' });
    if (!focusRankingSalesButton(focusTarget)) el('rankingSupplierSelect').focus();
  });
}

function renderItem() {
  document.querySelectorAll('.filters button').forEach((button) => {
    button.classList.toggle('active', button.dataset.filter === filter);
    button.disabled = !currentItem;
  });
  el('itemSection').hidden = !user || activeView !== 'history';
  if (!user || activeView !== 'history') return;

  const tbody = el('itemBody');
  tbody.replaceChildren();
  if (!currentItem) {
    el('itemTitle').textContent = t(locale, 'historyTab');
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.className = 'muted';
    td.textContent = t(locale, 'historyNoItem');
    tr.appendChild(td);
    tbody.appendChild(tr);
    el('pageInfo').textContent = '';
    el('prevBtn').disabled = true;
    el('nextBtn').disabled = true;
    return;
  }
  el('itemTitle').textContent = `${currentItem.supplierCode} / ${currentItem.itemNumber}`;

  const filtered = filterTimeline(timeline, filter);
  const p = paginate(filtered, page, PAGE_SIZE);

  for (const c of p.items) {
    const tr = document.createElement('tr');
    const tdType = document.createElement('td');
    const typeTag = document.createElement('span');
    typeTag.className = `type-tag ${c.type === 'order' ? 'type-order' : 'type-sales'}`;
    typeTag.textContent = t(locale, c.type === 'order' ? 'order' : 'sales');
    tdType.appendChild(typeTag);
    const tdDate = document.createElement('td');
    tdDate.textContent = c.dateRange || c.date || '—';
    const tdNo = document.createElement('td');
    tdNo.textContent = c.orderNo || '—';
    const tdQty = document.createElement('td');
    tdQty.textContent = c.quantity != null ? c.quantity : 0;
    tdQty.className = c.type === 'order' ? 'qty-order' : 'qty-sales';
    const tdPrice = document.createElement('td');
    tdPrice.textContent = c.price != null ? c.price : '—';
    tr.append(tdType, tdDate, tdNo, tdQty, tdPrice);
    tbody.appendChild(tr);
  }

  if (p.items.length === 0) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.className = 'muted';
    td.textContent = t(locale, 'noData');
    tr.appendChild(td);
    tbody.appendChild(tr);
  }

  el('pageInfo').textContent = `${t(locale, 'page')} ${p.page} / ${p.totalPages}`;
  el('prevBtn').disabled = p.page <= 1;
  el('nextBtn').disabled = p.page >= p.totalPages;
}

function render() {
  applyI18n();
  renderApiSettings();
  renderAuth();
  renderStore();
  void renderSuppliers();
  renderCategory();
  renderDataTabs();
  renderItem();
  renderRanking();
  renderStoreSales();
}

async function loadProfiles() {
  const response = await send({ type: 'GET_PROFILES' });
  profiles = response && response.ok ? response.profiles || [] : [];
}

async function loadStores() {
  const response = await send({ type: 'GET_STORES' });
  storeOptions = response && response.ok ? normalizeStoreOptions(response.data) : [];
  const previousStoreCode = selectedStoreCode;
  if (!storeOptions.some((store) => store.code === selectedStoreCode)) {
    selectedStoreCode = storeOptions[0]?.code || null;
  }
  if (selectedStoreCode !== previousStoreCode) {
    if (selectedStoreCode) await chrome.storage.local.set({ selectedStoreCode });
    else await chrome.storage.local.remove('selectedStoreCode');
  }
}

async function loadActiveSupplier() {
  const requestGeneration = activeSupplierRequestGeneration.advance();
  const response = await send({ type: 'ACTIVE_SUPPLIER' });
  if (!activeSupplierRequestGeneration.isCurrent(requestGeneration)) return false;
  const detectedSupplier = response && response.ok ? response.supplier || null : null;
  if (activeTabSupplier?.supplierCode !== detectedSupplier?.supplierCode) categoryError = null;
  activeTabSupplier = detectedSupplier;
  const previousDetectedSupplierCode = lastDetectedSupplierCode;
  const detectedSupplierCode = detectedSupplier?.supplierCode || null;
  // 离开供应商网站时保留最近一次自动识别结果，返回同一网站后仍尊重手动选择。
  if (detectedSupplierCode) lastDetectedSupplierCode = detectedSupplierCode;
  if (
    shouldPreserveManualSupplier({
      manualSupplierCode: manuallySelectedSupplierCode,
      detectedSupplierCode,
      previousDetectedSupplierCode,
    })
  ) {
    return false;
  }

  if (detectedSupplier) manuallySelectedSupplierCode = null;
  const previousCode = currentSupplier?.supplierCode || null;
  const nextCode = detectedSupplier?.supplierCode || null;
  if (previousCode === nextCode) return false;

  resetStoreSalesState();
  rankingRequestGeneration.advance();
  currentSupplier = detectedSupplier;
  rankingData = null;
  rankingLegacyItems = null;
  rankingLoading = false;
  rankingError = null;
  rankingRetryTarget = null;
  rankingPage = 1;
  return true;
}

function selectSupplier(supplierCode, { manual = false } = {}) {
  activeSupplierRequestGeneration.advance();
  const profile = profiles.find((item) => item.supplierCode === supplierCode);
  manuallySelectedSupplierCode = manual ? supplierCode : null;
  const changed = currentSupplier?.supplierCode !== supplierCode;
  currentSupplier = {
    supplierCode,
    displayName: profile?.displayName || supplierCode,
  };
  if (changed) {
    resetStoreSalesState();
    rankingRequestGeneration.advance();
    rankingData = null;
    rankingLegacyItems = null;
    rankingLoading = false;
    rankingError = null;
    rankingRetryTarget = null;
    rankingPage = 1;
  }
  return changed;
}

async function refreshActiveSupplierAndRanking() {
  if (!user) return;
  const changed = await loadActiveSupplier();
  // 排行榜可能保留手动选择，但分类采集区始终跟随当前标签页。
  renderCategory();
  if (!changed) return;
  render();
  if (activeView === 'ranking' && currentSupplier) await loadRanking();
}

function scheduleActiveSupplierRefresh() {
  if (activeSupplierRefreshTimer != null) clearTimeout(activeSupplierRefreshTimer);
  activeSupplierRefreshTimer = setTimeout(() => {
    activeSupplierRefreshTimer = null;
    void refreshActiveSupplierAndRanking().catch((error) => {
      setStatus(String((error && error.message) || error));
    });
  }, 120);
}

function renderLegacyRankingPage({ scrollOnSuccess = false } = {}) {
  if (!rankingLegacyItems) return false;
  const normalized = normalizeTopSalesPage(
    {
      topPercent: 10,
      supplierCode: rankingData?.supplierCode,
      days: rankingData?.days,
      totalProductCount: rankingData?.totalProductCount,
      totalRankedCount: rankingLegacyItems.length,
      items: rankingLegacyItems,
    },
    {
      requestedPage: rankingPage,
      requestedPageSize: rankingPageSize,
      requestedSupplierCode: currentSupplier?.supplierCode,
      requestedDays: rankingDays,
    },
  );
  rankingData = { ...rankingData, ...normalized };
  rankingPage = normalized.page;
  rankingPageSize = normalized.pageSize;
  rankingError = null;
  rankingRetryTarget = null;
  renderRanking();
  if (scrollOnSuccess) {
    scrollRankingToTop();
    announceRankingPage();
  }
  return true;
}

async function loadRanking({ clear = rankingData == null, scrollOnSuccess = false } = {}) {
  if (!currentSupplier) await loadActiveSupplier();
  if (!currentSupplier) {
    rankingData = null;
    rankingLegacyItems = null;
    rankingLoading = false;
    rankingError = null;
    rankingRetryTarget = null;
    setStatus('');
    render();
    return false;
  }

  const requestGeneration = rankingRequestGeneration.advance();
  const requestedSupplierCode = currentSupplier.supplierCode;
  const requestedPage = rankingPage;
  const requestedPageSize = rankingPageSize;
  const requestedRankingDays = rankingDays;
  const requestTarget = {
    supplierCode: requestedSupplierCode,
    days: requestedRankingDays,
    page: requestedPage,
    pageSize: requestedPageSize,
  };
  rankingRetryTarget = null;
  const rankingLoad = beginRankingLoad({
    page: requestedPage,
    pageSize: requestedPageSize,
    data: rankingData,
    legacyItems: rankingLegacyItems,
  }, { clear });
  const previousRankingState = rankingLoad.checkpoint;
  rankingPage = rankingLoad.state.page;
  rankingPageSize = rankingLoad.state.pageSize;
  rankingData = rankingLoad.state.data;
  rankingLegacyItems = rankingLoad.state.legacyItems;
  rankingLoading = rankingLoad.state.loading;
  rankingError = rankingLoad.state.error;
  if (clear) el('rankingAnnouncement').textContent = '';
  setStatus('');
  renderRanking();
  let response;
  try {
    response = await send({
      type: 'SUPPLIER_TOP_SALES',
      supplierCode: requestedSupplierCode,
      days: requestedRankingDays,
      topPercent: 30,
      page: requestedPage,
      pageSize: requestedPageSize,
    });
  } catch (error) {
    response = { ok: false, error: String((error && error.message) || error) };
  }
  if (!rankingRequestGeneration.isCurrent(requestGeneration)) return false;
  rankingLoading = false;
  if (!response || !response.ok) {
    const error = (response && response.error) || t(locale, 'rankingLoadFailed');
    if (clear) {
      rankingData = null;
      rankingLegacyItems = null;
      rankingError = error;
    } else {
      const restored = restoreRankingLoad(previousRankingState, error);
      rankingPage = restored.page;
      rankingPageSize = restored.pageSize;
      rankingData = restored.data;
      rankingLegacyItems = restored.legacyItems;
      rankingLoading = restored.loading;
      rankingError = restored.error;
    }
    rankingRetryTarget = requestTarget;
    renderRanking();
    return false;
  }
  try {
    const rawData = response.data || {};
    const normalized = normalizeTopSalesPage(rawData, {
      requestedPage,
      requestedPageSize,
      requestedSupplierCode,
      requestedDays: requestedRankingDays,
    });
    rankingData = { ...rawData, ...normalized };
    rankingLegacyItems = normalized.mode === 'legacy' && Array.isArray(rawData.items)
      ? rawData.items
      : null;
    rankingPage = normalized.page;
    rankingPageSize = normalized.pageSize;
  } catch (error) {
    if (clear) {
      rankingData = null;
      rankingLegacyItems = null;
      rankingError = String((error && error.message) || error);
    } else {
      const restored = restoreRankingLoad(previousRankingState, error);
      rankingPage = restored.page;
      rankingPageSize = restored.pageSize;
      rankingData = restored.data;
      rankingLegacyItems = restored.legacyItems;
      rankingLoading = restored.loading;
      rankingError = restored.error;
    }
    rankingRetryTarget = requestTarget;
    renderRanking();
    return false;
  }
  rankingApiOrigin = response.apiOrigin || apiOrigin;
  rankingRetryTarget = null;
  setStatus('');
  renderRanking();
  if (scrollOnSuccess) {
    scrollRankingToTop();
    announceRankingPage();
  }
  return true;
}

async function loadItem(item) {
  const requestGeneration = itemRequestGeneration.advance();
  currentItem = item;
  selectSupplier(item.supplierCode);
  activeView = 'history';
  timeline = [];
  filter = 'all';
  page = 1;
  // 只使用服务端筛选后的当前门店，避免沿用内容脚本或旧缓存中的停用 POS 门店。
  const storeCode = selectedStoreCode;
  if (!storeCode) {
    setStatus(t(locale, 'noStore'));
    renderItem();
    return;
  }
  setStatus(t(locale, 'loading'));
  const resp = await send({
    type: 'PURCHASE_CYCLES',
    storeCode,
    supplierCode: item.supplierCode,
    itemNumber: item.itemNumber,
  });
  if (!itemRequestGeneration.isCurrent(requestGeneration)) return;
  if (!resp || !resp.ok) {
    setStatus((resp && resp.error) || t(locale, 'error'));
    renderItem();
    return;
  }
  const { orders, sales } = normalizeCycles(resp.data);
  timeline = buildTimeline({ orders, sales });
  setStatus('');
  renderItem();
}

function resetAuthenticatedData() {
  itemRequestGeneration.advance();
  rankingRequestGeneration.advance();
  resetStoreSalesState();
  activeSupplierRequestGeneration.advance();
  user = null;
  profiles = [];
  storeOptions = [];
  timeline = [];
  rankingData = null;
  rankingLegacyItems = null;
  currentSupplier = null;
  activeTabSupplier = null;
  categoryError = null;
  manuallySelectedSupplierCode = null;
  lastDetectedSupplierCode = null;
  rankingPage = 1;
  rankingLoading = false;
  rankingError = null;
  rankingRetryTarget = null;
  if (activeSupplierRefreshTimer != null) {
    clearTimeout(activeSupplierRefreshTimer);
    activeSupplierRefreshTimer = null;
  }
}

const connectFromWebsiteSession = createSingleFlight(async ({ loadView = false } = {}) => {
  authState = 'checking';
  authReason = null;
  setStatus('');
  render();

  const response = await send({ type: 'CURRENT' });
  if (!response?.ok || !response.user) {
    resetAuthenticatedData();
    authState = 'needsWebsite';
    authReason = response?.reason || 'WEBSITE_SESSION_REQUIRED';
    setStatus(authReason === 'API_ORIGIN_MISMATCH'
      ? t(locale, 'apiOriginMismatch')
      : response?.error || '');
    render();
    return false;
  }

  user = response.user;
  authState = 'connected';
  await loadProfiles();
  await loadStores();
  await loadActiveSupplier();
  setStatus('');
  render();

  if (loadView) {
    if (currentItem) await loadItem(currentItem);
    else if (activeView === 'ranking') await loadRanking();
  }
  return true;
});

async function init() {
  const stored = await chrome.storage.local.get([
    'locale',
    'selectedStoreCode',
    'salesRankingDays',
    'salesRankingPageSize',
  ]);
  locale = resolveInitialLocale(stored.locale, getPreferredLanguages());
  rankingDays = normalizeRankingDays(stored.salesRankingDays);
  rankingPageSize = normalizeRankingPageSize(stored.salesRankingPageSize);
  const normalizedPreferences = {};
  if (stored.locale !== locale) normalizedPreferences.locale = locale;
  if (stored.salesRankingDays !== rankingDays) {
    normalizedPreferences.salesRankingDays = rankingDays;
  }
  if (stored.salesRankingPageSize !== rankingPageSize) {
    normalizedPreferences.salesRankingPageSize = rankingPageSize;
  }
  if (Object.keys(normalizedPreferences).length > 0) {
    await chrome.storage.local.set(normalizedPreferences);
  }
  applyI18n();
  selectedStoreCode = stored.selectedStoreCode || null;
  try {
    await loadCategoryState();
  } catch {
    // 分类采集状态读取失败不影响侧栏其余功能。
  }

  const apiConfig = await send({ type: 'GET_API_ORIGIN' });
  if (apiConfig && apiConfig.ok) {
    apiOrigin = apiConfig.apiOrigin;
    defaultApiOrigin = apiConfig.defaultApiOrigin;
    localApiOrigin = apiConfig.localApiOrigin;
  }

  const connected = await connectFromWebsiteSession();

  const { pendingLocate } = await chrome.storage.session.get('pendingLocate');
  if (connected && pendingLocate) {
    await chrome.storage.session.remove('pendingLocate');
    await loadItem(pendingLocate);
  } else if (connected && activeView === 'ranking') {
    await loadRanking();
  }

  render();
}

async function applyApiOrigin(value) {
  const normalized = normalizeApiOrigin(value, defaultApiOrigin);
  if (!normalized) {
    setStatus(t(locale, 'apiInvalid'));
    return;
  }

  const pattern = toApiHostPattern(normalized);
  let granted = false;
  try {
    granted = pattern ? await chrome.permissions.contains({ origins: [pattern] }) : false;
    if (!granted && pattern) {
      granted = await chrome.permissions.request({ origins: [pattern] });
    }
  } catch {
    setStatus(t(locale, 'apiPermissionDenied'));
    return;
  }
  if (!granted) {
    setStatus(t(locale, 'apiPermissionDenied'));
    return;
  }

  const response = await send({ type: 'SET_API_ORIGIN', apiOrigin: normalized });
  if (!response || !response.ok) {
    setStatus((response && response.error) || t(locale, 'error'));
    return;
  }

  apiOrigin = response.apiOrigin;
  if (response.changed) {
    resetAuthenticatedData();
    authState = 'checking';
    authReason = null;
    setStatus(t(locale, 'apiSwitched'));
    render();
    await connectFromWebsiteSession({ loadView: true });
  } else {
    setStatus(t(locale, 'apiSaved'));
    render();
  }
}

el('localeBtn').addEventListener('click', async () => {
  locale = locale === 'zh' ? 'en' : 'zh';
  await chrome.storage.local.set({ locale });
  render();
});

el('apiRemoteBtn').addEventListener('click', () => {
  void applyApiOrigin('/');
});

el('apiLocalBtn').addEventListener('click', () => {
  void applyApiOrigin(localApiOrigin);
});

el('apiSaveBtn').addEventListener('click', () => {
  void applyApiOrigin(el('apiOriginInput').value);
});

el('apiOriginInput').addEventListener('keydown', (event) => {
  if (event.key !== 'Enter') return;
  event.preventDefault();
  void applyApiOrigin(el('apiOriginInput').value);
});

el('openShopBtn').addEventListener('click', async () => {
  authState = 'checking';
  authReason = null;
  setStatus('');
  render();
  const response = await send({ type: 'OPEN_HB_SHOP' });
  if (response?.connected) {
    await connectFromWebsiteSession({ loadView: true });
    return;
  }
  authState = 'needsWebsite';
  authReason = response?.reason || 'WEBSITE_TAB_REQUIRED';
  setStatus(response?.ok ? '' : response?.error || t(locale, 'error'));
  render();
});

el('recheckBtn').addEventListener('click', () => {
  void connectFromWebsiteSession({ loadView: true });
});

el('disconnectBtn').addEventListener('click', async () => {
  await send({ type: 'DISCONNECT' });
  resetAuthenticatedData();
  authState = 'needsWebsite';
  authReason = 'WEBSITE_SESSION_REQUIRED';
  setStatus('');
  render();
});

el('storeSaveBtn').addEventListener('click', async () => {
  if (storeOptions.length === 0) {
    setStatus(t(locale, 'noPosStore'));
    return;
  }
  selectedStoreCode = el('storeSelect').value;
  await chrome.storage.local.set({ selectedStoreCode });
  setStatus(selectedStoreCode ? `${t(locale, 'storeSaved')}: ${selectedStoreCode}` : '');
  if (currentItem && selectedStoreCode) {
    currentItem = { ...currentItem, storeCode: selectedStoreCode };
    await loadItem(currentItem);
  }
});

el('supplierToggleBtn').addEventListener('click', () => {
  supplierListExpanded = !supplierListExpanded;
  void renderSuppliers();
});

el('categoryPassiveToggle').addEventListener('change', async (event) => {
  const supplierCode = activeTabSupplier?.supplierCode;
  if (!supplierCode) return;
  const next = {
    ...categorySettings,
    [supplierCode]: {
      ...(categorySettings?.[supplierCode] || {}),
      passiveEnabled: event.currentTarget.checked,
    },
  };
  categorySettings = next;
  // 内容脚本监听该键热更新，无需刷新供应商页面。
  await chrome.storage.local.set({ categoryCaptureSettings: next });
  renderCategory();
});

el('categoryCrawlBtn').addEventListener('click', () => {
  void startCategoryCrawl('full');
});

el('categoryResumeBtn').addEventListener('click', () => {
  void startCategoryCrawl('resume');
});

el('categoryRetryBtn').addEventListener('click', () => {
  void startCategoryCrawl('retry');
});

el('categoryAbortBtn').addEventListener('click', () => {
  void abortCategoryCrawl();
});

el('historyTab').addEventListener('click', () => {
  activeView = 'history';
  setStatus('');
  render();
});

el('rankingTab').addEventListener('click', async () => {
  activeView = 'ranking';
  await loadActiveSupplier();
  render();
  await loadRanking();
});

el('rankingSupplierSelect').addEventListener('change', async () => {
  const supplierCode = el('rankingSupplierSelect').value;
  if (!supplierCode) return;
  selectSupplier(supplierCode, { manual: true });
  activeView = 'ranking';
  render();
  await loadRanking();
});

document.querySelectorAll('[data-ranking-days]').forEach((button) => {
  button.addEventListener('click', async () => {
    const nextDays = normalizeRankingDays(button.dataset.rankingDays);
    if (nextDays === rankingDays && rankingData) return;
    rankingRequestGeneration.advance();
    rankingDays = nextDays;
    rankingRetryTarget = null;
    ({ page: rankingPage, pageSize: rankingPageSize } = transitionRankingPagination(
      { page: rankingPage, pageSize: rankingPageSize },
      { type: 'context' },
    ));
    await chrome.storage.local.set({ salesRankingDays: rankingDays });
    await loadRanking({ clear: true });
  });
});

el('rankingPageSizeSelect').addEventListener('change', async () => {
  const nextPageSize = normalizeRankingPageSize(el('rankingPageSizeSelect').value);
  if (nextPageSize === rankingPageSize) return;
  ({ page: rankingPage, pageSize: rankingPageSize } = transitionRankingPagination(
    { page: rankingPage, pageSize: rankingPageSize },
    { type: 'page-size', pageSize: nextPageSize },
  ));
  // 页大小是用户偏好，不应因为本次网络请求失败而在侧栏重开后丢失。
  await chrome.storage.local.set({ salesRankingPageSize: rankingPageSize });
  if (!renderLegacyRankingPage({ scrollOnSuccess: true })) {
    // pageSize 改变会重定义页码边界，按上下文切换处理，避免复用旧分页元数据。
    await loadRanking({ clear: true, scrollOnSuccess: true });
  }
});

el('rankingRetryBtn').addEventListener('click', async () => {
  const retryTarget = resolveRankingRetryTarget(rankingRetryTarget, {
    supplierCode: currentSupplier?.supplierCode,
    days: rankingDays,
  });
  if (retryTarget) {
    rankingPage = retryTarget.page;
    rankingPageSize = retryTarget.pageSize;
  }
  const loaded = await loadRanking({
    clear: rankingData == null,
    scrollOnSuccess: rankingData != null,
  });
  if (loaded && retryTarget) {
    await chrome.storage.local.set({ salesRankingPageSize: rankingPageSize });
  }
});

el('storeSalesBackBtn').addEventListener('click', () => {
  closeStoreSales();
});

el('storeSalesSearch').addEventListener('input', (event) => {
  storeSalesQuery = event.currentTarget.value;
  renderStoreSalesList();
});

el('storeSalesRetryBtn').addEventListener('click', () => {
  if (storeSalesErrorCode === 'RANKING_SNAPSHOT_CHANGED') {
    void refreshRankingAfterStaleStoreSales();
    return;
  }
  void loadStoreSales();
});

el('rankingPrevBtn').addEventListener('click', async () => {
  if (rankingPage > 1) {
    ({ page: rankingPage, pageSize: rankingPageSize } = transitionRankingPagination(
      { page: rankingPage, pageSize: rankingPageSize },
      { type: 'page', page: rankingPage - 1 },
    ));
    if (!renderLegacyRankingPage({ scrollOnSuccess: true })) {
      await loadRanking({ clear: false, scrollOnSuccess: true });
    }
  }
});

el('rankingNextBtn').addEventListener('click', async () => {
  if (rankingData && rankingPage < rankingData.totalPages) {
    ({ page: rankingPage, pageSize: rankingPageSize } = transitionRankingPagination(
      { page: rankingPage, pageSize: rankingPageSize },
      { type: 'page', page: rankingPage + 1 },
    ));
    if (!renderLegacyRankingPage({ scrollOnSuccess: true })) {
      await loadRanking({ clear: false, scrollOnSuccess: true });
    }
  }
});

document.querySelectorAll('.filters button').forEach((btn) => {
  btn.addEventListener('click', () => {
    filter = btn.dataset.filter;
    page = 1;
    document.querySelectorAll('.filters button').forEach((b) => b.classList.toggle('active', b === btn));
    renderItem();
  });
});

el('prevBtn').addEventListener('click', () => {
  if (page > 1) {
    page--;
    renderItem();
  }
});

el('nextBtn').addEventListener('click', () => {
  page++;
  renderItem();
});

chrome.storage.onChanged.addListener((changes, areaName) => {
  // 分类采集任务进度由 service worker 写入存储，侧栏只负责按存储渲染。
  let categoryChanged = false;
  if (matchesStorageArea(areaName, 'session') && changes.categoryCrawlJob) {
    categoryJob = changes.categoryCrawlJob.newValue || null;
    categoryChanged = true;
  }
  if (matchesStorageArea(areaName, 'local')) {
    if (changes.categoryCrawlHistory) {
      categoryHistory = changes.categoryCrawlHistory.newValue || {};
      categoryChanged = true;
    }
    if (changes.categoryCaptureSettings) {
      categorySettings = changes.categoryCaptureSettings.newValue || {};
      categoryChanged = true;
    }
  }
  if (categoryChanged) renderCategory();

  if (matchesStorageArea(areaName, 'local')) {
    let rankingContextChanged = false;
    let rankingPageSizeChanged = false;
    if (changes.salesRankingDays) {
      const nextDays = normalizeRankingDays(changes.salesRankingDays.newValue);
      if (nextDays !== rankingDays) {
        rankingDays = nextDays;
        rankingContextChanged = true;
      }
    }
    if (changes.salesRankingPageSize) {
      const nextPageSize = normalizeRankingPageSize(changes.salesRankingPageSize.newValue);
      if (nextPageSize !== rankingPageSize) {
        rankingPageSize = nextPageSize;
        rankingPageSizeChanged = true;
      }
    }
    if (rankingContextChanged || rankingPageSizeChanged) {
      rankingRequestGeneration.advance();
      resetStoreSalesState();
      rankingPage = 1;
      rankingError = null;
      if (rankingContextChanged) {
        rankingData = null;
        rankingLegacyItems = null;
        rankingRetryTarget = null;
      }
      if (user && activeView === 'ranking' && currentSupplier) {
        const renderedLegacyPage = !rankingContextChanged && renderLegacyRankingPage();
        if (!renderedLegacyPage) {
          void loadRanking({
            clear: rankingContextChanged || rankingPageSizeChanged || rankingData == null,
          });
        }
      }
      else render();
    }
  }

  const tokenChange = areaName === 'session' ? changes.websiteAccessToken : null;
  if (tokenChange?.newValue && authState !== 'connected') {
    void connectFromWebsiteSession({ loadView: true });
  } else if (tokenChange?.oldValue && !tokenChange.newValue) {
    resetAuthenticatedData();
    authState = 'needsWebsite';
    authReason = 'WEBSITE_SESSION_REQUIRED';
    setStatus('');
    render();
  }

  const item = getPendingLocateChange(changes, areaName);
  if (!item) return;
  void chrome.storage.session
    .remove('pendingLocate')
    .then(() => loadItem(item))
    .catch((error) => setStatus(String((error && error.message) || error)));
});

chrome.tabs.onActivated.addListener(() => {
  scheduleActiveSupplierRefresh();
});

chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (!tab.active || (!changeInfo.url && changeInfo.status !== 'complete')) return;
  scheduleActiveSupplierRefresh();
});

void init().catch((error) => {
  setStatus(String((error && error.message) || error));
  render();
});
