// 主动分类采集的纯逻辑：导航树构建、路径推导、任务状态合并与逐分类逐页的采集执行器。
// 执行器只通过注入的 fetchHtml / parsePage / onCapture / sleep / now 与外界交互，
// 单飞串行、限速、Retry-After、连续失败熔断与中止全部在这里实现并由 node --test 覆盖。
import {
  MAX_CATEGORY_KEY_LENGTH,
  MAX_CATEGORY_PATH_DEPTH,
  MAX_CATEGORY_URL_LENGTH,
  humanizeSlug,
  isPromotionalNode,
  isPromotionalPath,
  matchesCategoryPage,
  normalizeCategoryKey,
  normalizeCategoryName,
  resolveCategoryPath,
} from './category-path.js';
import {
  CAPTURE_CHUNK_SIZE,
  MAX_RETRY_AFTER_MS,
  MAX_TREE_SNAPSHOT_NODES,
  buildCapturePayload,
  createRetryPolicy,
  parseRetryAfter,
  runWithRetry,
  splitIntoChunks,
} from './category-capture.js';
import { normalizeCaptureItemNumbers } from './item-number.js';

export const CRAWL_STATUSES = Object.freeze({
  RUNNING: 'running',
  COMPLETED: 'completed',
  ABORTED: 'aborted',
  INTERRUPTED: 'interrupted',
  FAILED: 'failed',
});
export const TERMINAL_CRAWL_STATUSES = new Set(['completed', 'aborted', 'interrupted', 'failed']);

// 侧栏需要逐一提供中英文文案的错误码。
export const CRAWL_ERROR_CODES = Object.freeze([
  'SUPPLIER_TAB_REQUIRED',
  'CONTENT_SCRIPT_UNAVAILABLE',
  'CRAWL_ALREADY_RUNNING',
  'CRAWL_DISABLED',
  'LOGIN_REQUIRED',
  'SITE_BLOCKING',
  'NAV_NOT_FOUND',
  'FEATURE_DISABLED',
  'WEBSITE_SESSION_REQUIRED',
]);

export const MAX_CONSECUTIVE_CATEGORY_FAILURES = 5;
export const MAX_TRACKED_CRAWL_KEYS = 2000;
// 运行中任务超过 5 分钟没有任何进度，视为内容脚本已被浏览器回收。
export const STALE_RUNNING_JOB_MS = 5 * 60 * 1000;

const LOGIN_PATH = /(?:^|\/)(?:login|log-in|signin|sign-in|logon|account\/login|customer\/account\/login|my-account)(?:[/.?#]|$)/iu;

function parseUrl(value) {
  if (typeof value !== 'string' || !value) return null;
  try {
    return new URL(value);
  } catch {
    return null;
  }
}

function cleanHref(url) {
  const href = url.href;
  return href.length <= MAX_CATEGORY_URL_LENGTH ? href : null;
}

function lastSegmentName(key) {
  const segments = String(key || '').split('?')[0].split('/').filter(Boolean);
  return humanizeSlug(segments[segments.length - 1] || '');
}

// 路径型 key 的 URL 前缀父级：/a/b/c → /a/b → /a；查询参数型 key 不做前缀推断。
export function inferParentByPrefix(key, keys) {
  if (typeof key !== 'string' || key.includes('?')) return null;
  let current = key;
  for (;;) {
    const index = current.lastIndexOf('/');
    if (index <= 0) return null;
    current = current.slice(0, index);
    if (keys.has(current)) return current;
  }
}

// 导航锚点 → 分类树：DOM 祖先推父级优先，其次 URL 前缀；按 key 去重；剔除促销子树；
// 深度超过 maxDepth 的节点丢弃；BFS 顺序截断到 maxCategories（保证父节点总在子节点之前）。
export function buildNavTree(anchors, { config, origin }) {
  const byKey = new Map();
  const order = [];
  for (const anchor of Array.isArray(anchors) ? anchors : []) {
    const url = parseUrl(anchor?.url);
    if (!url || !/^https?:$/u.test(url.protocol) || url.origin !== origin) continue;
    if (!matchesCategoryPage(url.href, config)) continue;
    const key = normalizeCategoryKey(url, config);
    if (!key || key === '/') continue;
    const domParentKey = anchor.domParentUrl ? normalizeCategoryKey(anchor.domParentUrl, config) : null;
    const existing = byKey.get(key);
    if (existing) {
      // 同一分类在多个菜单出现时，保留首个名称，补上缺失的 DOM 父级。
      if (!existing.domParentKey && domParentKey && domParentKey !== key) existing.domParentKey = domParentKey;
      continue;
    }
    const name = normalizeCategoryName(anchor.name) || lastSegmentName(key);
    if (!name) continue;
    const node = {
      key,
      name,
      url: cleanHref(url),
      domParentKey: domParentKey && domParentKey !== key && domParentKey !== '/' ? domParentKey : null,
      parentKey: null,
    };
    byKey.set(key, node);
    order.push(node);
  }

  const keys = new Set(byKey.keys());
  for (const node of order) {
    node.parentKey = node.domParentKey && keys.has(node.domParentKey)
      ? node.domParentKey
      : inferParentByPrefix(node.key, keys);
  }
  // 断开可能由异常菜单结构造成的父子环。
  for (const node of order) {
    const seen = new Set([node.key]);
    let parentKey = node.parentKey;
    while (parentKey) {
      if (seen.has(parentKey)) {
        node.parentKey = null;
        break;
      }
      seen.add(parentKey);
      parentKey = byKey.get(parentKey)?.parentKey ?? null;
    }
  }

  const children = new Map();
  const roots = [];
  for (const node of order) {
    if (node.parentKey) {
      if (!children.has(node.parentKey)) children.set(node.parentKey, []);
      children.get(node.parentKey).push(node);
    } else {
      roots.push(node);
    }
  }

  const maxDepth = Number.isInteger(config?.maxDepth) ? config.maxDepth : 4;
  const maxCategories = Math.min(
    Number.isInteger(config?.maxCategories) ? config.maxCategories : 400,
    MAX_TREE_SNAPSHOT_NODES,
  );
  const nodes = [];
  let truncated = false;
  let droppedPromotional = 0;
  let droppedDepth = 0;
  const queue = roots.map((node, index) => ({ node, depth: 0, sortOrder: index }));
  while (queue.length > 0) {
    const { node, depth, sortOrder } = queue.shift();
    if (isPromotionalNode(node, config?.promotionalPatterns)) {
      droppedPromotional += 1;
      continue;
    }
    if (depth >= maxDepth) {
      droppedDepth += 1;
      continue;
    }
    if (nodes.length >= maxCategories) {
      truncated = true;
      break;
    }
    nodes.push({
      key: node.key,
      name: node.name,
      url: node.url,
      parentKey: node.parentKey,
      depth,
      sortOrder,
    });
    (children.get(node.key) || []).forEach((child, index) => {
      queue.push({ node: child, depth: depth + 1, sortOrder: index });
    });
  }
  return { nodes, truncated, droppedPromotional, droppedDepth };
}

// 沿 parentKey 回溯出根到叶路径（≤8 级），用于面包屑不可用时的主动采集回传。
export function resolvePathForNode(nodesByKey, key) {
  const path = [];
  const seen = new Set();
  let current = nodesByKey.get(key);
  while (current && !seen.has(current.key) && path.length < MAX_CATEGORY_PATH_DEPTH) {
    seen.add(current.key);
    path.unshift({ name: current.name, key: current.key, url: current.url || null });
    current = current.parentKey ? nodesByKey.get(current.parentKey) : null;
  }
  return path;
}

export function toTreeSnapshotNodes(nodes) {
  return (Array.isArray(nodes) ? nodes : []).slice(0, MAX_TREE_SNAPSHOT_NODES).map((node) => ({
    key: node.key,
    name: node.name,
    parentKey: node.parentKey || null,
    url: node.url || null,
    sortOrder: Number.isInteger(node.sortOrder) ? node.sortOrder : null,
  }));
}

// 登录页识别：被重定向到登录路径，或页面只有密码框而没有商品与面包屑。
export function detectLoginPage({
  requestedUrl,
  finalUrl,
  hasPasswordField = false,
  cardCount = 0,
  breadcrumbCount = 0,
} = {}) {
  const requested = parseUrl(requestedUrl);
  const final = parseUrl(finalUrl);
  if (final && requested && LOGIN_PATH.test(final.pathname) && !LOGIN_PATH.test(requested.pathname)) {
    return true;
  }
  return !!hasPasswordField && cardCount === 0 && breadcrumbCount === 0;
}

function isValidCrawlKey(value) {
  return typeof value === 'string' && value.startsWith('/') && value.length <= MAX_CATEGORY_KEY_LENGTH;
}

function sanitizeKeys(values, limit = MAX_TRACKED_CRAWL_KEYS) {
  const out = [];
  const seen = new Set();
  for (const value of Array.isArray(values) ? values : []) {
    if (!isValidCrawlKey(value) || seen.has(value)) continue;
    seen.add(value);
    out.push(value);
    if (out.length >= limit) break;
  }
  return out;
}

export function sanitizeCrawlNodes(values, limit = MAX_TRACKED_CRAWL_KEYS) {
  const out = [];
  const seen = new Set();
  for (const node of Array.isArray(values) ? values : []) {
    if (!node || !isValidCrawlKey(node.key) || seen.has(node.key)) continue;
    const name = normalizeCategoryName(node.name);
    if (!name) continue;
    seen.add(node.key);
    out.push({
      key: node.key,
      name,
      url: typeof node.url === 'string' && node.url.length <= MAX_CATEGORY_URL_LENGTH ? node.url : null,
      parentKey: isValidCrawlKey(node.parentKey) ? node.parentKey : null,
      depth: Number.isInteger(node.depth) && node.depth >= 0 ? node.depth : 0,
    });
    if (out.length >= limit) break;
  }
  return out;
}

function toCount(value) {
  const number = Number(value);
  return Number.isFinite(number) && number >= 0 ? Math.trunc(number) : 0;
}

export function isTerminalCrawlStatus(status) {
  return TERMINAL_CRAWL_STATUSES.has(status);
}

export function createCrawlJob({
  jobId,
  supplierCode,
  tabId,
  origin,
  mode = 'full',
  completedKeys = [],
  now = () => Date.now(),
}) {
  const timestamp = new Date(now()).toISOString();
  return {
    jobId,
    supplierCode,
    tabId,
    origin,
    mode,
    status: CRAWL_STATUSES.RUNNING,
    startedAt: timestamp,
    updatedAt: timestamp,
    finishedAt: null,
    total: 0,
    done: 0,
    failed: 0,
    pages: 0,
    itemsSent: 0,
    completedKeys: sanitizeKeys(completedKeys),
    failedNodes: [],
    current: null,
    errorCode: null,
  };
}

// 合并内容脚本上报的进度：终态之后只累积计数与已完成 key，不再改变状态。
export function mergeCrawlProgress(job, progress, { now = () => Date.now() } = {}) {
  if (!job || !progress || progress.jobId !== job.jobId) return job;
  const timestamp = new Date(now()).toISOString();
  const next = { ...job, updatedAt: timestamp };
  for (const field of ['total', 'done', 'failed', 'pages', 'itemsSent']) {
    if (progress[field] != null) next[field] = toCount(progress[field]);
  }
  if (Array.isArray(progress.completedKeys)) {
    next.completedKeys = sanitizeKeys([...(job.completedKeys || []), ...progress.completedKeys]);
  }
  if (Array.isArray(progress.failedNodes)) {
    next.failedNodes = sanitizeCrawlNodes(progress.failedNodes);
  }
  if (progress.current && isValidCrawlKey(progress.current.key)) {
    next.current = { key: progress.current.key, name: normalizeCategoryName(progress.current.name) };
  } else if (progress.current === null) {
    next.current = null;
  }
  if (!isTerminalCrawlStatus(job.status) && Object.values(CRAWL_STATUSES).includes(progress.status)) {
    next.status = progress.status;
    if (isTerminalCrawlStatus(progress.status)) {
      next.finishedAt = timestamp;
      next.current = null;
      next.errorCode = typeof progress.errorCode === 'string' ? progress.errorCode : null;
    }
  }
  return next;
}

// 由服务端/SW 直接判定的终态（中止、标签页关闭、导航离开）。
export function finalizeCrawlJob(job, status, { errorCode = null, now = () => Date.now() } = {}) {
  if (!job || isTerminalCrawlStatus(job.status)) return job;
  const timestamp = new Date(now()).toISOString();
  return {
    ...job,
    status,
    errorCode,
    current: null,
    updatedAt: timestamp,
    finishedAt: timestamp,
  };
}

export function isCrawlJobStale(job, now = () => Date.now()) {
  if (!job || job.status !== CRAWL_STATUSES.RUNNING) return false;
  const updatedAt = Date.parse(job.updatedAt || '');
  return !Number.isFinite(updatedAt) || now() - updatedAt > STALE_RUNNING_JOB_MS;
}

export function toCrawlHistoryEntry(job) {
  return {
    jobId: job.jobId,
    supplierCode: job.supplierCode,
    mode: job.mode,
    status: job.status,
    startedAt: job.startedAt,
    finishedAt: job.finishedAt,
    total: toCount(job.total),
    done: toCount(job.done),
    failed: toCount(job.failed),
    pages: toCount(job.pages),
    itemsSent: toCount(job.itemsSent),
    errorCode: job.errorCode || null,
    completedKeys: sanitizeKeys(job.completedKeys),
    failedNodes: sanitizeCrawlNodes(job.failedNodes),
  };
}

export function summarizeProgress(job) {
  const total = toCount(job?.total);
  const done = toCount(job?.done);
  const failed = toCount(job?.failed);
  return {
    total,
    done,
    failed,
    remaining: Math.max(0, total - done - failed),
    percent: total > 0 ? Math.min(100, Math.round((done / total) * 100)) : 0,
  };
}

const defaultSleep = (ms, signal) => new Promise((resolve) => {
  if (signal?.aborted) {
    resolve();
    return;
  }
  const timer = setTimeout(done, ms);
  function done() {
    clearTimeout(timer);
    signal?.removeEventListener?.('abort', done);
    resolve();
  }
  signal?.addEventListener?.('abort', done, { once: true });
});

// 逐分类逐页采集执行器（BFS、单飞串行、限速、分页跟随、子分类发现、熔断、可中止、可续跑）。
export function createCrawlRunner({
  config,
  origin,
  supplierCode,
  fetchHtml,
  parsePage,
  onCapture,
  onProgress = () => undefined,
  sleep = defaultSleep,
  now = () => Date.now(),
  signal = null,
  retryPolicy = createRetryPolicy(),
  progressIntervalMs = 1000,
  maxConsecutiveFailures = MAX_CONSECUTIVE_CATEGORY_FAILURES,
  maxFetchRetries = 2,
}) {
  let running = false;
  const delayMs = Number.isInteger(config?.crawlDelayMs) ? config.crawlDelayMs : 1500;
  const maxPages = Number.isInteger(config?.maxPages) ? config.maxPages : 20;
  const maxDepth = Number.isInteger(config?.maxDepth) ? config.maxDepth : 4;
  const maxCategories = Number.isInteger(config?.maxCategories) ? config.maxCategories : 400;
  let lastRequestAt = null;

  const aborted = () => !!signal?.aborted;

  async function throttle() {
    if (lastRequestAt == null) return;
    const wait = lastRequestAt + delayMs - now();
    if (wait > 0) await sleep(wait, signal);
  }

  // 同源 GET 一页：429/503 按 Retry-After（≤60s）重试，5xx/网络错误指数退避，401 视为登录失效。
  async function fetchPage(url) {
    for (let attempt = 0; attempt <= maxFetchRetries; attempt += 1) {
      await throttle();
      if (aborted()) return { aborted: true };
      lastRequestAt = now();
      let response;
      try {
        response = await fetchHtml(url, { signal });
      } catch {
        if (aborted()) return { aborted: true };
        response = { ok: false, status: 0 };
      }
      if (aborted()) return { aborted: true };
      const status = Number(response?.status) || 0;
      const finalUrl = response?.finalUrl || url;
      const final = parseUrl(finalUrl);
      if (final && final.origin !== origin) {
        return LOGIN_PATH.test(final.pathname)
          ? { ok: false, fatal: true, code: 'LOGIN_REQUIRED' }
          : { ok: false, code: 'CROSS_ORIGIN_REDIRECT' };
      }
      if (response?.ok && typeof response.html === 'string' && response.html) {
        return { ok: true, html: response.html, finalUrl };
      }
      if (status === 401 || status === 407) return { ok: false, fatal: true, code: 'LOGIN_REQUIRED' };
      const retryable = status === 0 || status === 429 || status >= 500;
      if (!retryable || attempt >= maxFetchRetries) {
        return { ok: false, code: status ? `HTTP_${status}` : 'NETWORK_ERROR' };
      }
      const retryAfter = status === 429 || status === 503 ? parseRetryAfter(response?.retryAfter, now) : null;
      const backoff = retryAfter ?? delayMs * 2 ** (attempt + 1);
      await sleep(Math.min(backoff, MAX_RETRY_AFTER_MS), signal);
    }
    return { ok: false, code: 'NETWORK_ERROR' };
  }

  function acceptSubcategory(link, parent, nodesByKey) {
    const url = parseUrl(link?.url);
    if (!url || !/^https?:$/u.test(url.protocol) || url.origin !== origin) return null;
    if (!matchesCategoryPage(url.href, config)) return null;
    const key = normalizeCategoryKey(url, config);
    if (!key || key === '/' || key === parent.key || nodesByKey.has(key)) return null;
    // 子分类链接区可能列出整棵树：路径型 key 只接受当前分类路径下的链接。
    const queryKeyed = (config.keyQueryParams || []).length > 0 || config.keySource === 'hash';
    if (!queryKeyed && !key.startsWith(`${parent.key}/`)) return null;
    const name = normalizeCategoryName(link.name) || lastSegmentName(key);
    const node = { key, name, url: cleanHref(url), parentKey: parent.key, depth: (parent.depth || 0) + 1 };
    if (!name || node.depth >= maxDepth || isPromotionalNode(node, config.promotionalPatterns)) return null;
    return node;
  }

  async function crawlCategory(node, nodesByKey, stats) {
    let url = node.url;
    if (!url) return { ok: false, code: 'NO_URL' };
    const visited = new Set();
    const subcategories = [];
    let pageNumber = 1;
    let failureCode = null;
    while (url && pageNumber <= maxPages) {
      if (aborted()) return { aborted: true };
      const visitKey = url.split('#')[0];
      if (visited.has(visitKey)) break;
      visited.add(visitKey);

      const page = await fetchPage(url);
      if (page.aborted) return { aborted: true };
      if (!page.ok) {
        if (page.fatal) return { ok: false, fatal: true, code: page.code };
        // 首页失败算分类失败；后续分页失败只截断该分类，已回传部分仍有效。
        if (pageNumber === 1) return { ok: false, code: page.code };
        break;
      }
      stats.pages += 1;

      let parsed;
      try {
        parsed = parsePage(page.html, page.finalUrl) || {};
      } catch {
        return { ok: false, code: 'PARSE_FAILED' };
      }
      const itemNumbers = normalizeCaptureItemNumbers(parsed.itemNumbers);
      const breadcrumbItems = Array.isArray(parsed.breadcrumbItems) ? parsed.breadcrumbItems : [];
      if (detectLoginPage({
        requestedUrl: url,
        finalUrl: page.finalUrl,
        hasPasswordField: parsed.hasPasswordField,
        cardCount: itemNumbers.length,
        breadcrumbCount: breadcrumbItems.length,
      })) {
        return { ok: false, fatal: true, code: 'LOGIN_REQUIRED' };
      }

      // 页面自身面包屑的叶子就是该分类时优先使用，否则沿导航树回溯。
      const resolved = resolveCategoryPath({
        pageUrl: page.finalUrl,
        breadcrumbItems,
        title: parsed.title,
        config,
      });
      const path = resolved && resolved.leafKey === node.key
        ? resolved.path
        : resolvePathForNode(nodesByKey, node.key);

      if (path.length > 0 && !isPromotionalPath(path, config.promotionalPatterns)) {
        for (const chunk of splitIntoChunks(itemNumbers, CAPTURE_CHUNK_SIZE)) {
          if (aborted()) return { aborted: true };
          const payload = buildCapturePayload({
            supplierCode,
            pageUrl: page.finalUrl,
            categoryPath: path,
            itemNumbers: chunk,
            mode: 'crawl',
            pageNumber,
            now,
          });
          const result = await runWithRetry(() => onCapture(payload), { policy: retryPolicy, sleep, signal });
          if (result.ok) {
            stats.itemsSent += chunk.length;
            continue;
          }
          if (aborted()) return { aborted: true };
          if (result.classification?.fatal) return { ok: false, fatal: true, code: result.classification.code };
          failureCode = result.classification?.code || 'CAPTURE_FAILED';
        }
      }

      if (pageNumber === 1) {
        for (const link of Array.isArray(parsed.subcategoryLinks) ? parsed.subcategoryLinks : []) {
          const child = acceptSubcategory(link, node, nodesByKey);
          if (child && !subcategories.some((item) => item.key === child.key)) subcategories.push(child);
        }
      }

      // 只跟随仍属于同一分类（归一化 key 相同）的同源下一页。
      const next = parseUrl(parsed.nextUrl);
      if (!next || next.origin !== origin || normalizeCategoryKey(next, config) !== node.key) break;
      url = next.href;
      pageNumber += 1;
    }
    // 有块回传失败（重试耗尽）时整个分类记为失败，便于“重试失败”重新采集。
    return failureCode ? { ok: false, code: failureCode, subcategories } : { ok: true, subcategories };
  }

  async function run({ nodes = [], completedKeys = [], onlyNodes = null } = {}) {
    if (running) return { status: CRAWL_STATUSES.FAILED, errorCode: 'CRAWL_ALREADY_RUNNING' };
    running = true;
    try {
      const nodesByKey = new Map();
      for (const node of Array.isArray(nodes) ? nodes : []) nodesByKey.set(node.key, { ...node });
      const scope = Array.isArray(onlyNodes) ? sanitizeCrawlNodes(onlyNodes) : [...nodesByKey.values()];
      for (const node of scope) {
        if (!nodesByKey.has(node.key)) nodesByKey.set(node.key, { ...node });
      }
      const queue = scope.map((node) => nodesByKey.get(node.key));
      const completed = new Set(sanitizeKeys(completedKeys));
      const failedNodes = new Map();
      // 子分类链接新发现的节点（不在导航树里），结束后补一次快照。
      const discovered = [];
      const stats = { pages: 0, itemsSent: 0 };
      let done = queue.filter((node) => completed.has(node.key)).length;
      let consecutiveFailures = 0;
      let status = CRAWL_STATUSES.COMPLETED;
      let errorCode = null;
      let current = null;
      let lastEmitAt = null;

      const emit = (force = false, overrides = {}) => {
        const timestamp = now();
        if (!force && lastEmitAt != null && timestamp - lastEmitAt < progressIntervalMs) return;
        lastEmitAt = timestamp;
        onProgress({
          status: CRAWL_STATUSES.RUNNING,
          total: queue.length,
          done,
          failed: failedNodes.size,
          pages: stats.pages,
          itemsSent: stats.itemsSent,
          completedKeys: [...completed].slice(0, MAX_TRACKED_CRAWL_KEYS),
          failedNodes: [...failedNodes.values()],
          current: current ? { key: current.key, name: current.name } : null,
          errorCode: null,
          ...overrides,
        });
      };

      emit(true);
      for (let index = 0; index < queue.length; index += 1) {
        if (aborted()) {
          status = CRAWL_STATUSES.ABORTED;
          break;
        }
        const node = queue[index];
        if (completed.has(node.key)) continue;
        current = node;
        emit();
        const outcome = await crawlCategory(node, nodesByKey, stats);
        if (outcome.aborted || aborted()) {
          status = CRAWL_STATUSES.ABORTED;
          break;
        }
        if (outcome.fatal) {
          status = CRAWL_STATUSES.FAILED;
          errorCode = outcome.code;
          break;
        }
        if (outcome.ok) {
          completed.add(node.key);
          failedNodes.delete(node.key);
          done += 1;
          consecutiveFailures = 0;
        } else {
          failedNodes.set(node.key, {
            key: node.key,
            name: node.name,
            url: node.url || null,
            parentKey: node.parentKey || null,
            depth: node.depth || 0,
          });
          consecutiveFailures += 1;
          if (consecutiveFailures >= maxConsecutiveFailures) {
            // 连续多个分类失败通常是站点限流/拦截，立即停止避免加重封禁。
            status = CRAWL_STATUSES.FAILED;
            errorCode = 'SITE_BLOCKING';
            break;
          }
        }
        for (const child of outcome.subcategories || []) {
          if (nodesByKey.size >= maxCategories || nodesByKey.has(child.key)) continue;
          nodesByKey.set(child.key, child);
          queue.push(child);
          discovered.push(child);
        }
        emit();
      }
      current = null;
      emit(true, { status, errorCode });
      return {
        status,
        errorCode,
        total: queue.length,
        done,
        failed: failedNodes.size,
        pages: stats.pages,
        itemsSent: stats.itemsSent,
        completedKeys: [...completed],
        failedNodes: [...failedNodes.values()],
        discovered,
      };
    } finally {
      running = false;
    }
  }

  return {
    run,
    get running() {
      return running;
    },
  };
}
