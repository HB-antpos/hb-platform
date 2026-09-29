// 分类采集回传的纯逻辑：去重、稳定检测、分块、载荷构建与校验、失败分类、重试策略、被动采集控制器。
// 所有时间、定时器、存储与网络均由调用方注入，便于 node --test 覆盖。
import {
  MAX_CATEGORY_KEY_LENGTH,
  MAX_CATEGORY_NAME_LENGTH,
  MAX_CATEGORY_PATH_DEPTH,
  MAX_CATEGORY_URL_LENGTH,
  detectPageNumber,
  isPromotionalPath,
  matchesCategoryPage,
  resolveCategoryPath,
} from './category-path.js';
import { normalizeCaptureItemNumbers } from './item-number.js';

// 与后端 NormalizeItemNumbers 的 MaximumBatchSize=100 保持一致。
export const CAPTURE_CHUNK_SIZE = 100;
export const CAPTURE_DEDUPE_TTL_MS = 6 * 60 * 60 * 1000;
export const CAPTURE_DEDUPE_MAX_ENTRIES = 500;
export const CAPTURE_QUIET_MS = 1500;
export const CAPTURE_MODES = Object.freeze(['passive', 'crawl']);
export const MAX_TREE_SNAPSHOT_NODES = 2000;
export const MAX_RETRY_AFTER_MS = 60_000;
export const MAX_SUPPLIER_CODE_LENGTH = 50;

// 这些错误说明服务端或会话层面不允许继续采集：被动采集熔断、主动采集整体停止。
const FATAL_ERROR_CODES = new Set([
  'FEATURE_DISABLED',
  'NOT_FOUND',
  'SUPPLIER_NOT_CAPTURABLE',
  'CATEGORY_CAPTURE_DISABLED',
  'WEBSITE_SESSION_REQUIRED',
  'WEBSITE_TAB_REQUIRED',
  'WEBSITE_BRIDGE_UNAVAILABLE',
  'API_ORIGIN_MISMATCH',
  'INVALID_TOKEN_RESPONSE',
  'CRAWL_JOB_MISMATCH',
]);
const RETRYABLE_ERROR_CODES = new Set(['SUPPLIER_CATEGORY_BUSY', 'LOCAL_RATE_LIMITED', 'NETWORK_ERROR']);

// FNV-1a 32 位哈希：只用于去重键，不承担安全用途。
export function fnv1a(value) {
  let hash = 0x811c9dc5;
  const text = String(value ?? '');
  for (let index = 0; index < text.length; index += 1) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash.toString(16).padStart(8, '0');
}

// 货号集合哈希与顺序无关：同一批货号换排序不会重复回传。
export function hashItemNumbers(itemNumbers) {
  const unique = [...new Set(normalizeCaptureItemNumbers(itemNumbers))].sort();
  return fnv1a(unique.join('\n'));
}

export function buildCaptureDedupeKey({ supplierCode, categoryPath, itemNumbers }) {
  const keys = (Array.isArray(categoryPath) ? categoryPath : []).map((node) => node?.key || '');
  const items = normalizeCaptureItemNumbers(itemNumbers);
  return `${String(supplierCode || '')}|${fnv1a(keys.join('>'))}|${hashItemNumbers(items)}:${items.length}`;
}

// 本地去重存储（service worker 用 storage.local 持久化）：TTL 6 小时、最多 500 条、串行读写。
export function createCaptureDedupeStore({
  read,
  write,
  now = () => Date.now(),
  ttlMs = CAPTURE_DEDUPE_TTL_MS,
  maxEntries = CAPTURE_DEDUPE_MAX_ENTRIES,
}) {
  let queue = Promise.resolve();
  const serial = (task) => {
    const run = queue.then(task, task);
    queue = run.catch(() => undefined);
    return run;
  };

  async function load() {
    let raw;
    try {
      raw = await read();
    } catch {
      raw = [];
    }
    const current = now();
    return (Array.isArray(raw) ? raw : []).filter((entry) => (
      Array.isArray(entry)
      && typeof entry[0] === 'string'
      && Number.isFinite(entry[1])
      && current - entry[1] < ttlMs
      && entry[1] <= current + 60_000
    ));
  }

  return {
    has(key) {
      return serial(async () => (await load()).some(([entryKey]) => entryKey === key));
    },
    add(key) {
      return serial(async () => {
        const entries = (await load()).filter(([entryKey]) => entryKey !== key);
        entries.push([key, now()]);
        entries.sort((a, b) => a[1] - b[1]);
        const trimmed = entries.length > maxEntries ? entries.slice(entries.length - maxEntries) : entries;
        await write(trimmed);
        return trimmed.length;
      });
    },
  };
}

// 页面稳定检测：签名变化即重新计时；同一签名至少出现两次且静默满 quietMs 才触发一次。
export function createCaptureScheduler({
  quietMs = CAPTURE_QUIET_MS,
  now = () => Date.now(),
  setTimer = (fn, ms) => setTimeout(fn, ms),
  clearTimer = (id) => clearTimeout(id),
  onStable,
}) {
  let state = null;
  let timer = null;

  function clear() {
    if (timer != null) clearTimer(timer);
    timer = null;
  }

  function fire() {
    if (!state || state.fired || state.count < 2) return;
    state.fired = true;
    clear();
    onStable(state.payload);
  }

  return {
    notify(signature, payload) {
      const current = now();
      if (!state || state.signature !== signature) {
        clear();
        state = { signature, payload, firstSeenAt: current, count: 1, fired: false };
        timer = setTimer(() => {
          timer = null;
          fire();
        }, quietMs);
        return;
      }
      state.payload = payload;
      state.count += 1;
      if (!state.fired && current - state.firstSeenAt >= quietMs) fire();
    },
    reset() {
      clear();
      state = null;
    },
    get pendingSignature() {
      return state && !state.fired ? state.signature : null;
    },
  };
}

export function diffNewItems(itemNumbers, sentSet) {
  const seen = new Set();
  const out = [];
  for (const item of itemNumbers || []) {
    if (!item || seen.has(item) || sentSet?.has(item)) continue;
    seen.add(item);
    out.push(item);
  }
  return out;
}

export function splitIntoChunks(items, size = CAPTURE_CHUNK_SIZE) {
  const chunkSize = Number.isInteger(size) && size > 0 ? size : CAPTURE_CHUNK_SIZE;
  const list = Array.isArray(items) ? items : [];
  const out = [];
  for (let index = 0; index < list.length; index += chunkSize) {
    out.push(list.slice(index, index + chunkSize));
  }
  return out;
}

function parseUrl(value) {
  if (typeof value !== 'string' || !value) return null;
  try {
    return new URL(value);
  } catch {
    return null;
  }
}

// 后端 PageUrl 上限 1000：依次去掉片段、查询串，最后截断。
export function truncatePageUrl(value) {
  const url = parseUrl(value);
  if (!url) return typeof value === 'string' ? value.slice(0, MAX_CATEGORY_URL_LENGTH) : '';
  if (url.href.length <= MAX_CATEGORY_URL_LENGTH) return url.href;
  url.hash = '';
  if (url.href.length <= MAX_CATEGORY_URL_LENGTH) return url.href;
  const base = `${url.origin}${url.pathname}`;
  return base.slice(0, MAX_CATEGORY_URL_LENGTH);
}

function toIsoString(value, now) {
  const date = value instanceof Date ? value : new Date(value ?? now());
  return Number.isFinite(date.getTime()) ? date.toISOString() : new Date(now()).toISOString();
}

export function buildCapturePayload({
  supplierCode,
  pageUrl,
  categoryPath,
  itemNumbers,
  capturedAt,
  mode,
  pageNumber,
  now = () => Date.now(),
}) {
  return {
    supplierCode: String(supplierCode || ''),
    pageUrl: truncatePageUrl(pageUrl),
    categoryPath: (Array.isArray(categoryPath) ? categoryPath : []).map((node) => ({
      name: node.name,
      key: node.key,
      url: node.url || null,
    })),
    itemNumbers: normalizeCaptureItemNumbers(itemNumbers).slice(0, CAPTURE_CHUNK_SIZE),
    capturedAt: toIsoString(capturedAt, now),
    mode,
    ...(Number.isInteger(pageNumber) && pageNumber >= 1 ? { pageNumber } : {}),
  };
}

function isValidKey(value) {
  return typeof value === 'string'
    && value.startsWith('/')
    && value.length <= MAX_CATEGORY_KEY_LENGTH
    && !/[\s\u0000-\u001f\u007f]/u.test(value.split('?')[0]);
}

function sanitizeName(value) {
  if (typeof value !== 'string') return null;
  const name = value.replace(/\s+/gu, ' ').trim();
  return name && name.length <= MAX_CATEGORY_NAME_LENGTH ? name : null;
}

function sanitizeSameOriginUrl(value, origin) {
  if (value == null || value === '') return { ok: true, url: null };
  const url = parseUrl(value);
  if (!url || !/^https?:$/u.test(url.protocol) || url.origin !== origin) return { ok: false };
  if (url.href.length > MAX_CATEGORY_URL_LENGTH) return { ok: true, url: null };
  return { ok: true, url: url.href };
}

function invalid(error) {
  return { ok: false, errorCode: 'INVALID_CAPTURE', error };
}

function checkSupplier(supplierCode, expectedSupplierCode) {
  if (typeof supplierCode !== 'string' || !supplierCode || supplierCode.length > MAX_SUPPLIER_CODE_LENGTH) {
    return 'supplierCode 非法';
  }
  if (expectedSupplierCode != null && supplierCode !== expectedSupplierCode) return 'supplierCode 与来源页面不一致';
  return null;
}

// service worker 侧的形状校验：页面 URL 与每个路径节点 URL 都必须与来源标签页同源。
export function validateCapturePayload(payload, { senderOrigin, expectedSupplierCode, now = () => Date.now() } = {}) {
  if (!payload || typeof payload !== 'object') return invalid('载荷缺失');
  const supplierError = checkSupplier(payload.supplierCode, expectedSupplierCode);
  if (supplierError) return invalid(supplierError);
  if (!CAPTURE_MODES.includes(payload.mode)) return invalid('mode 非法');

  const page = parseUrl(payload.pageUrl);
  if (!page || !/^https?:$/u.test(page.protocol) || page.origin !== senderOrigin) {
    return invalid('pageUrl 必须与来源页面同源');
  }

  const path = payload.categoryPath;
  if (!Array.isArray(path) || path.length < 1 || path.length > MAX_CATEGORY_PATH_DEPTH) {
    return invalid(`categoryPath 必须为 1..${MAX_CATEGORY_PATH_DEPTH} 个节点`);
  }
  const categoryPath = [];
  for (const node of path) {
    const name = sanitizeName(node?.name);
    if (!name || !isValidKey(node?.key)) return invalid('categoryPath 节点非法');
    const url = sanitizeSameOriginUrl(node.url, senderOrigin);
    if (!url.ok) return invalid('categoryPath 节点 URL 必须与来源页面同源');
    categoryPath.push({ name, key: node.key, url: url.url });
  }

  if (!Array.isArray(payload.itemNumbers)) return invalid('itemNumbers 必须为数组');
  const itemNumbers = normalizeCaptureItemNumbers(payload.itemNumbers);
  if (itemNumbers.length < 1 || itemNumbers.length > CAPTURE_CHUNK_SIZE || payload.itemNumbers.length > CAPTURE_CHUNK_SIZE) {
    return invalid(`itemNumbers 必须为 1..${CAPTURE_CHUNK_SIZE} 个有效货号`);
  }

  const pageNumber = Number.isInteger(payload.pageNumber) && payload.pageNumber >= 1 && payload.pageNumber <= 10000
    ? payload.pageNumber
    : undefined;
  return {
    ok: true,
    payload: {
      supplierCode: payload.supplierCode,
      pageUrl: truncatePageUrl(page.href),
      categoryPath,
      itemNumbers,
      capturedAt: toIsoString(payload.capturedAt, now),
      mode: payload.mode,
      ...(pageNumber ? { pageNumber } : {}),
    },
  };
}

export function validateTreeSnapshotPayload(payload, { senderOrigin, expectedSupplierCode } = {}) {
  if (!payload || typeof payload !== 'object') return invalid('载荷缺失');
  const supplierError = checkSupplier(payload.supplierCode, expectedSupplierCode);
  if (supplierError) return invalid(supplierError);
  const source = sanitizeSameOriginUrl(payload.sourceUrl, senderOrigin);
  if (!source.ok || !payload.sourceUrl) return invalid('sourceUrl 必须与来源页面同源');
  const sourceUrl = source.url || truncatePageUrl(payload.sourceUrl);
  if (!Array.isArray(payload.nodes) || payload.nodes.length < 1 || payload.nodes.length > MAX_TREE_SNAPSHOT_NODES) {
    return invalid(`nodes 必须为 1..${MAX_TREE_SNAPSHOT_NODES} 个节点`);
  }
  const nodes = [];
  const keys = new Set();
  for (const node of payload.nodes) {
    const name = sanitizeName(node?.name);
    if (!name || !isValidKey(node?.key) || keys.has(node.key)) return invalid('nodes 节点非法或重复');
    if (node.parentKey != null && (!isValidKey(node.parentKey) || node.parentKey === node.key)) {
      return invalid('nodes.parentKey 非法');
    }
    const url = sanitizeSameOriginUrl(node.url, senderOrigin);
    if (!url.ok) return invalid('nodes 节点 URL 必须与来源页面同源');
    keys.add(node.key);
    nodes.push({
      key: node.key,
      name,
      parentKey: node.parentKey ?? null,
      url: url.url,
      sortOrder: Number.isInteger(node.sortOrder) ? node.sortOrder : null,
    });
  }
  return { ok: true, payload: { supplierCode: payload.supplierCode, sourceUrl, nodes } };
}

function toCount(value) {
  const number = Number(value);
  return Number.isFinite(number) && number >= 0 ? Math.trunc(number) : 0;
}

export function normalizeCaptureResponse(data) {
  const source = data && typeof data === 'object' ? data : {};
  return {
    categoryGuid: typeof source.categoryGuid === 'string' ? source.categoryGuid : '',
    fullPath: typeof source.fullPath === 'string' ? source.fullPath : '',
    depth: toCount(source.depth),
    isPromotional: source.isPromotional === true,
    categoriesCreated: toCount(source.categoriesCreated),
    matchedProducts: toCount(source.matchedProducts),
    assignedProducts: toCount(source.assignedProducts),
    unchangedProducts: toCount(source.unchangedProducts),
    skippedManual: toCount(source.skippedManual),
    unmatchedItemNumberCount: toCount(source.unmatchedItemNumberCount),
    unmatchedSamples: Array.isArray(source.unmatchedSamples)
      ? source.unmatchedSamples.filter((item) => typeof item === 'string').slice(0, 10)
      : [],
  };
}

export function normalizeTreeSnapshotResponse(data) {
  const source = data && typeof data === 'object' ? data : {};
  return {
    created: toCount(source.created),
    updated: toCount(source.updated),
    unchanged: toCount(source.unchanged),
    orphanCount: toCount(source.orphanCount),
    promotionalCount: toCount(source.promotionalCount),
  };
}

// 失败分类：429/409/408/5xx/网络错误可重试；401/403/404 与服务端停用类错误为致命；其余 400 只跳过当前块。
export function classifyCaptureFailure({ httpStatus, errorCode, networkError } = {}) {
  const status = Number(httpStatus) || 0;
  const code = errorCode || (status ? `HTTP_${status}` : 'NETWORK_ERROR');
  const retryable = RETRYABLE_ERROR_CODES.has(code)
    || !!networkError
    || status === 0
    || status === 408
    || status === 409
    || status === 429
    || (status >= 500 && status <= 599);
  const fatal = !retryable && (
    FATAL_ERROR_CODES.has(code)
    || status === 401
    || status === 403
    || status === 404
  );
  return { retryable, fatal, code };
}

export function parseRetryAfter(value, now = () => Date.now()) {
  if (value == null || value === '') return null;
  const text = String(value).trim();
  if (/^\d+(?:\.\d+)?$/u.test(text)) return Math.min(Number(text) * 1000, MAX_RETRY_AFTER_MS);
  const date = Date.parse(text);
  if (!Number.isFinite(date)) return null;
  return Math.min(Math.max(0, date - now()), MAX_RETRY_AFTER_MS);
}

// 指数退避：第 n 次失败后等待 base×2^(n-1)，Retry-After 优先，上限 60 秒。
export function createRetryPolicy({
  maxAttempts = 3,
  baseDelayMs = 1000,
  maxDelayMs = MAX_RETRY_AFTER_MS,
} = {}) {
  return {
    maxAttempts,
    shouldRetry(attempt, classification) {
      return !!classification?.retryable && attempt < maxAttempts;
    },
    delayFor(attempt, retryAfterMs) {
      if (Number.isFinite(retryAfterMs) && retryAfterMs >= 0) return Math.min(retryAfterMs, maxDelayMs);
      return Math.min(baseDelayMs * 2 ** Math.max(0, attempt - 1), maxDelayMs);
    },
  };
}

const defaultSleep = (ms) => new Promise((resolve) => {
  setTimeout(resolve, ms);
});

// 执行带重试的请求；task 返回 { ok, httpStatus, errorCode, retryAfterMs, networkError }。
export async function runWithRetry(task, { policy = createRetryPolicy(), sleep = defaultSleep, signal } = {}) {
  let attempt = 0;
  for (;;) {
    attempt += 1;
    let result;
    try {
      result = await task(attempt);
    } catch (error) {
      result = { ok: false, networkError: true, error: String(error?.message || error) };
    }
    if (result?.ok) return { ...result, attempts: attempt };
    const classification = classifyCaptureFailure(result || {});
    if (signal?.aborted || !policy.shouldRetry(attempt, classification)) {
      return { ...(result || { ok: false }), ok: false, classification, attempts: attempt };
    }
    await sleep(policy.delayFor(attempt, result?.retryAfterMs), signal);
  }
}

// 本地滑动窗口限流：service worker 在后端 120 次/分钟限制之前先行削峰。
export function createSlidingWindowLimiter({ limit = 100, windowMs = 60_000, now = () => Date.now() } = {}) {
  const stamps = [];
  return {
    tryAcquire() {
      const current = now();
      while (stamps.length > 0 && current - stamps[0] >= windowMs) stamps.shift();
      if (stamps.length >= limit) {
        return { ok: false, retryAfterMs: Math.max(0, windowMs - (current - stamps[0])) };
      }
      stamps.push(current);
      return { ok: true, retryAfterMs: 0 };
    },
  };
}

// 被动采集控制器：页面稳定后解析分类路径 → 促销过滤 → 增量货号 → 100 一块回传。
// 致命失败（后台停用、未授权等）后熔断到页面生命周期结束，绝不影响按钮注入。
export function createPassiveCaptureController({
  supplierCode,
  getConfig,
  readPageContext,
  sendCapture,
  getCurrentHref = null,
  now = () => Date.now(),
  setTimer,
  clearTimer,
  sleep,
  quietMs = CAPTURE_QUIET_MS,
  retryPolicy = createRetryPolicy(),
  maxTrackedItems = 5000,
  onResult = null,
}) {
  let disabled = false;
  let queue = Promise.resolve();
  const sentByLeaf = new Map();
  let trackedCount = 0;

  const scheduler = createCaptureScheduler({
    quietMs,
    now,
    setTimer,
    clearTimer,
    onStable: (snapshot) => {
      queue = queue
        .then(() => capture(snapshot))
        .then((result) => onResult?.(result))
        .catch(() => undefined);
    },
  });

  function markSent(leafKey, items) {
    if (trackedCount + items.length > maxTrackedItems) {
      // 无限滚动页面防止内存无限增长：超过上限整体清空，最坏只会多发一次（服务端幂等）。
      sentByLeaf.clear();
      trackedCount = 0;
    }
    let sent = sentByLeaf.get(leafKey);
    if (!sent) {
      sent = new Set();
      sentByLeaf.set(leafKey, sent);
    }
    for (const item of items) {
      if (!sent.has(item)) {
        sent.add(item);
        trackedCount += 1;
      }
    }
  }

  async function capture({ href, items }) {
    const config = getConfig();
    if (disabled || !config?.enabled || !config.passiveEnabled) return { skipped: 'disabled' };
    if (getCurrentHref && getCurrentHref() !== href) return { skipped: 'navigated' };
    const context = readPageContext() || {};
    const resolved = resolveCategoryPath({
      pageUrl: href,
      breadcrumbItems: context.breadcrumbItems,
      title: context.title,
      config,
    });
    if (!resolved) return { skipped: 'no-path' };
    if (isPromotionalPath(resolved.path, config.promotionalPatterns)) return { skipped: 'promotional' };
    const newItems = diffNewItems(items, sentByLeaf.get(resolved.leafKey));
    if (newItems.length === 0) return { skipped: 'no-new-items' };

    const pageNumber = detectPageNumber(href);
    let sentCount = 0;
    for (const chunk of splitIntoChunks(newItems, CAPTURE_CHUNK_SIZE)) {
      const payload = buildCapturePayload({
        supplierCode,
        pageUrl: href,
        categoryPath: resolved.path,
        itemNumbers: chunk,
        mode: 'passive',
        pageNumber,
        now,
      });
      const result = await runWithRetry(() => sendCapture(payload), { policy: retryPolicy, sleep });
      if (result.ok) {
        markSent(resolved.leafKey, chunk);
        sentCount += chunk.length;
        continue;
      }
      if (result.classification?.fatal) {
        disabled = true;
        scheduler.reset();
        return { error: result.classification.code, sent: sentCount, fatal: true };
      }
    }
    return { sent: sentCount, leafKey: resolved.leafKey };
  }

  return {
    notify({ href, itemNumbers }) {
      const config = getConfig();
      if (disabled || !config?.enabled || !config.passiveEnabled) {
        scheduler.reset();
        return 'disabled';
      }
      if (!matchesCategoryPage(href, config)) {
        scheduler.reset();
        return 'not-category';
      }
      const items = normalizeCaptureItemNumbers(itemNumbers);
      if (items.length === 0) {
        scheduler.reset();
        return 'empty';
      }
      scheduler.notify(`${href}\n${items.length}:${hashItemNumbers(items)}`, { href, items });
      return 'scheduled';
    },
    reset() {
      scheduler.reset();
    },
    dispose() {
      disabled = true;
      scheduler.reset();
    },
    isDisabled() {
      return disabled;
    },
    // 测试与调试用：等待当前排队的回传完成。
    whenIdle() {
      return queue;
    },
  };
}
