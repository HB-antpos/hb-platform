// 后台 Service Worker：网站会话交接、短期令牌存储、消息路由、动态内容脚本注册
import { isAuthFailure } from '../lib/api-response.js';
import {
  LOCAL_API_ORIGIN,
  normalizeApiOrigin,
  resolveApiOrigin,
  toApiHostPattern,
} from '../lib/api-origin.js';
import { resolveGrantedProfileOrigins } from '../lib/origin-registration.js';
import { DEFAULT_PROFILES } from '../lib/profiles-default.js';
import { migrateProfileConfig } from '../lib/profile-cache.js';
import {
  NON_CAPTURABLE_SUPPLIER_CODES,
  matchProfile,
  validateProfiles,
} from '../lib/profiles.js';
import {
  buildCaptureDedupeKey,
  createCaptureDedupeStore,
  createSlidingWindowLimiter,
  normalizeCaptureResponse,
  normalizeTreeSnapshotResponse,
  parseRetryAfter,
  validateCapturePayload,
  validateTreeSnapshotPayload,
} from '../lib/category-capture.js';
import {
  CRAWL_STATUSES,
  createCrawlJob,
  finalizeCrawlJob,
  isCrawlJobStale,
  isTerminalCrawlStatus,
  mergeCrawlProgress,
  sanitizeCrawlNodes,
  toCrawlHistoryEntry,
} from '../lib/category-crawl.js';
import { createAssistantPanelController } from '../lib/assistant-panel.js';
import { normalizeRankingDays, normalizeTopSalesRequest } from '../lib/ranking.js';
import {
  createAccessRequestExecutor,
  createSingleFlight,
  parseTokenResponse,
  validateWebsiteSessionContext,
  WEBSITE_SESSION_CLIENT_ID,
} from '../lib/session-handoff.js';
import {
  API_BASE,
  BUILD_TARGET,
  EXTENSION_VERSION,
  HB_WEB_ORIGIN,
} from '../config.js';

const ACCESS_KEY = 'websiteAccessToken';
const ACCESS_EXPIRY_KEY = 'websiteAccessTokenExpiry';
const USER_KEY = 'websiteSessionUser';
const PENDING_HANDOFF_KEY = 'pendingWebsiteSessionHandoff';
const LEGACY_ACCESS_KEY = 'accessToken';
const LEGACY_REFRESH_KEY = 'refreshToken';
const PROFILES_KEY = 'supplierProfiles';
const GRANTED_KEY = 'grantedOrigins';
const API_ORIGIN_KEY = 'apiOrigin';
// 分类采集：主动任务状态放会话存储（刷新侧栏不丢），终态摘要与去重记录放本地存储。
const CATEGORY_JOB_KEY = 'categoryCrawlJob';
const CATEGORY_HISTORY_KEY = 'categoryCrawlHistory';
const CATEGORY_DEDUPE_KEY = 'categoryCaptureDedupe';
const CATEGORY_CAPTURES_PATH = '/api/react/v1/browser-extension/supplier-categories/captures';
const CATEGORY_TREE_SNAPSHOT_PATH = '/api/react/v1/browser-extension/supplier-categories/tree-snapshot';
const assistantPanel = createAssistantPanelController({ browserApi: chrome, buildTarget: BUILD_TARGET });
assistantPanel.registerListeners();

const getSession = (keys) => chrome.storage.session.get(keys);
const setSession = (obj) => chrome.storage.session.set(obj);
const removeSession = (keys) => chrome.storage.session.remove(keys);
const getLocal = (keys) => chrome.storage.local.get(keys);
const setLocal = (obj) => chrome.storage.local.set(obj);
const removeLocal = (keys) => chrome.storage.local.remove(keys);

async function getAccessToken() {
  const stored = await getSession([ACCESS_KEY, ACCESS_EXPIRY_KEY]);
  const token = stored[ACCESS_KEY];
  const expiry = Date.parse(stored[ACCESS_EXPIRY_KEY] || '');
  if (!token || !Number.isFinite(expiry) || expiry <= Date.now() + 5_000) {
    if (token || stored[ACCESS_EXPIRY_KEY]) await clearAccessSession();
    return null;
  }
  return token;
}

async function clearAccessSession() {
  await removeSession([ACCESS_KEY, ACCESS_EXPIRY_KEY, USER_KEY]);
}

async function getStoredSessionUser() {
  const stored = await getSession(USER_KEY);
  const value = stored[USER_KEY];
  if (
    !value
    || typeof value !== 'object'
    || typeof value.userGuid !== 'string'
    || !value.userGuid.trim()
    || !(
      (typeof value.username === 'string' && value.username.trim())
      || (typeof value.fullName === 'string' && value.fullName.trim())
    )
  ) {
    return null;
  }

  return {
    userGuid: value.userGuid.trim(),
    ...(typeof value.username === 'string' && value.username.trim()
      ? { username: value.username.trim() }
      : {}),
    ...(typeof value.fullName === 'string' && value.fullName.trim()
      ? { fullName: value.fullName.trim() }
      : {}),
  };
}

async function clearLegacyCredentials() {
  // 旧版曾保存 refresh token；升级后只删除，后续代码不再读取或写入。
  await Promise.all([
    removeSession([LEGACY_ACCESS_KEY]),
    removeLocal([LEGACY_REFRESH_KEY]),
  ]);
}

async function getApiOrigin() {
  const stored = await getLocal(API_ORIGIN_KEY);
  return resolveApiOrigin(stored[API_ORIGIN_KEY], API_BASE);
}

// 直接 fetch 并解析 ApiResponse 信封，返回统一结构
async function rawFetch(path, options = {}, { anonymous = false } = {}) {
  const [accessToken, apiOrigin] = await Promise.all([
    anonymous ? null : getAccessToken(),
    getApiOrigin(),
  ]);
  const headers = {
    'X-HB-Extension-Version': EXTENSION_VERSION,
    ...(options.body ? { 'Content-Type': 'application/json' } : {}),
    ...(options.headers || {}),
  };
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  const res = await fetch(`${apiOrigin}${path}`, {
    ...options,
    credentials: 'omit',
    headers,
  });
  let body = null;
  try {
    body = await res.json();
  } catch {
    // 非 JSON 响应按业务失败处理
  }
  return {
    httpStatus: res.status,
    ok: res.ok,
    success: body && body.success,
    data: body && body.data,
    message: body && body.message,
    errorCode: body && body.errorCode,
    // 429/503 的退避提示，分类采集按它延迟重试。
    retryAfter: res.headers?.get?.('Retry-After') ?? null,
  };
}

async function handleGetApiOrigin() {
  return {
    ok: true,
    apiOrigin: await getApiOrigin(),
    defaultApiOrigin: API_BASE,
    localApiOrigin: LOCAL_API_ORIGIN,
  };
}

async function handleSetApiOrigin({ apiOrigin }) {
  const normalized = normalizeApiOrigin(apiOrigin, API_BASE);
  if (!normalized) return { ok: false, error: '接口地址无效' };

  const pattern = toApiHostPattern(normalized);
  if (!pattern || !(await chrome.permissions.contains({ origins: [pattern] }))) {
    return { ok: false, error: '接口地址尚未获得浏览器授权' };
  }

  const current = await getApiOrigin();
  if (normalized === current) {
    return { ok: true, apiOrigin: normalized, changed: false, requiresWebsiteSession: false };
  }

  // 环境切换时清除旧环境短期令牌和供应商缓存，避免跨环境传递授权。
  await setLocal({ [API_ORIGIN_KEY]: normalized, [PROFILES_KEY]: DEFAULT_PROFILES });
  await Promise.all([
    clearAccessSession(),
    removeSession(PENDING_HANDOFF_KEY),
    // 分类回传去重记录属于旧环境，新环境需要重新回传。
    removeLocal([CATEGORY_DEDUPE_KEY]),
  ]);
  await syncContentScripts();
  return { ok: true, apiOrigin: normalized, changed: true, requiresWebsiteSession: true };
}

const accessRequestExecutor = createAccessRequestExecutor({
  isAuthFailure: (r) => isAuthFailure(r, r.httpStatus),
  clearAccessSession,
});

// 统一请求：401/业务鉴权失败只清理扩展会话，不刷新或退出网站会话。
async function apiRequest(path, options = {}) {
  if (!(await getAccessToken())) {
    const handoff = await ensureWebsiteSession();
    if (!handoff.ok) {
      return {
        httpStatus: 401,
        ok: false,
        success: false,
        message: handoff.error,
        errorCode: handoff.reason || 'WEBSITE_SESSION_REQUIRED',
      };
    }
  }
  return accessRequestExecutor(() => rawFetch(path, options));
}

function validateGrantMessage(message) {
  return message?.clientId === WEBSITE_SESSION_CLIENT_ID
    && typeof message.code === 'string'
    && message.code.length >= 16
    && message.code.length <= 512
    && typeof message.codeVerifier === 'string'
    && /^[A-Za-z0-9_-]{43,128}$/u.test(message.codeVerifier)
    && typeof message.state === 'string'
    && /^[A-Za-z0-9_-]{32,128}$/u.test(message.state);
}

async function exchangeWebsiteSessionGrant(message, sender) {
  const apiOrigin = await getApiOrigin();
  const senderUrl = sender?.tab?.url || sender?.url;
  const context = validateWebsiteSessionContext({
    pageUrl: senderUrl,
    webOrigin: HB_WEB_ORIGIN,
    apiOrigin,
    isTopLevel: sender?.frameId == null || sender.frameId === 0,
  });
  if (!context.ok || !validateGrantMessage(message)) {
    return {
      ok: false,
      reason: context.reason || 'INVALID_WEBSITE_SESSION_GRANT',
      error: '网站会话授权来源无效',
    };
  }

  const res = await rawFetch('/api/Auth/extension/token', {
    method: 'POST',
    body: JSON.stringify({
      code: message.code,
      codeVerifier: message.codeVerifier,
      state: message.state,
      clientId: WEBSITE_SESSION_CLIENT_ID,
    }),
  }, { anonymous: true });
  const parsed = parseTokenResponse({
    httpOk: res.ok,
    body: {
      success: res.success,
      data: res.data,
      message: res.message,
      errorCode: res.errorCode,
    },
  });
  if (!parsed.ok) {
    await clearAccessSession();
    return { ok: false, reason: parsed.reason, error: parsed.error };
  }

  await setSession({
    [ACCESS_KEY]: parsed.accessToken,
    [ACCESS_EXPIRY_KEY]: parsed.accessTokenExpiry,
    [USER_KEY]: parsed.user,
  });
  await removeSession(PENDING_HANDOFF_KEY);
  return { ok: true, user: parsed.user, accessTokenExpiry: parsed.accessTokenExpiry };
}

const acceptWebsiteSessionGrant = createSingleFlight(exchangeWebsiteSessionGrant);

async function findTrustedShopTabs() {
  const tabs = await chrome.tabs.query({ url: `${HB_WEB_ORIGIN}/shop*` });
  return tabs.filter((tab) => {
    const context = validateWebsiteSessionContext({
      pageUrl: tab.url,
      webOrigin: HB_WEB_ORIGIN,
      apiOrigin: HB_WEB_ORIGIN,
      isTopLevel: true,
    });
    return tab.id != null && context.ok;
  });
}

async function requestWebsiteSessionFromTab() {
  const apiOrigin = await getApiOrigin();
  if (apiOrigin !== HB_WEB_ORIGIN) {
    return {
      ok: false,
      reason: 'API_ORIGIN_MISMATCH',
      error: '当前接口与 HB SHOP 网页不同源',
      loginUrl: `${HB_WEB_ORIGIN}/shop`,
    };
  }

  const tabs = await findTrustedShopTabs();
  if (!tabs.length) {
    return {
      ok: false,
      reason: 'WEBSITE_TAB_REQUIRED',
      error: '请打开或登录 HB SHOP',
      loginUrl: `${HB_WEB_ORIGIN}/shop`,
    };
  }

  let lastFailure = null;
  for (const tab of tabs) {
    try {
      const result = await chrome.tabs.sendMessage(tab.id, {
        type: 'REQUEST_WEBSITE_SESSION',
        apiOrigin,
      });
      if (result?.ok) return result;
      lastFailure = result;
    } catch (error) {
      lastFailure = { error: String(error?.message || error) };
    }
  }
  return {
    ok: false,
    reason: lastFailure?.reason || 'WEBSITE_BRIDGE_UNAVAILABLE',
    error: lastFailure?.error || 'HB SHOP 授权桥尚未就绪',
    loginUrl: `${HB_WEB_ORIGIN}/shop`,
  };
}

const ensureWebsiteSession = createSingleFlight(async () => {
  if (await getAccessToken()) return { ok: true };
  return requestWebsiteSessionFromTab();
});

async function handleCurrent() {
  if (await getAccessToken()) {
    const existingUser = await getStoredSessionUser();
    if (existingUser) return { ok: true, user: existingUser };

    // 升级或异常存储状态缺少最小身份时，重新执行完整的一次性交接。
    await clearAccessSession();
  }

  const handoff = await ensureWebsiteSession();
  if (!handoff.ok) return handoff;

  const currentUser = handoff.user || await getStoredSessionUser();
  if (!currentUser) {
    await clearAccessSession();
    return {
      ok: false,
      reason: 'INVALID_TOKEN_RESPONSE',
      error: '网站会话返回的账号信息无效',
      loginUrl: `${HB_WEB_ORIGIN}/shop`,
    };
  }
  return { ok: true, user: currentUser };
}

async function handleDisconnect() {
  await Promise.all([clearAccessSession(), removeSession(PENDING_HANDOFF_KEY)]);
  return { ok: true };
}

async function handleGetProfiles() {
  const storedConfig = await migrateStoredProfiles();
  const storedValidation = validateProfiles(storedConfig);
  let config = storedValidation.valid
    ? {
        configVersion: storedConfig.configVersion ?? '1',
        profiles: storedValidation.profiles,
      }
    : DEFAULT_PROFILES;
  let source = storedValidation.valid ? 'cache' : 'default';
  // 分类块非法只降级该供应商的分类采集，原因通过 warnings 返回给侧栏排查。
  let warnings = storedValidation.valid ? storedValidation.warnings : [];
  try {
    const res = await apiRequest('/api/react/v1/browser-extension/supplier-profiles', { method: 'GET' });
    if (res.success && res.data && Array.isArray(res.data.profiles)) {
      const v = validateProfiles(res.data);
      if (v.valid) {
        // 空数组是正式的后台停用信号，绝不能回退内置 DATS。
        config = {
          configVersion: res.data.configVersion ?? '1',
          profiles: v.profiles,
        };
        source = 'server';
        warnings = v.warnings;
      } else {
        // 非法远程配置采取 fail-closed，不继续使用可能已被后台停用的旧配置。
        config = { configVersion: res.data.configVersion ?? 'invalid', profiles: [] };
        source = 'invalid-server';
        warnings = [];
      }
    }
  } catch {
    // 未登录或服务端暂不可用时沿用最近一次已验证配置。
  }
  if (source === 'default') {
    // 内置配置同样经过归一化，保证分类块字段完整。
    const defaults = validateProfiles(DEFAULT_PROFILES);
    config = { configVersion: DEFAULT_PROFILES.configVersion, profiles: defaults.profiles };
    warnings = defaults.warnings;
  }
  await setLocal({ [PROFILES_KEY]: config });
  await syncContentScripts();
  return {
    ok: true,
    profiles: config.profiles,
    configVersion: config.configVersion,
    source,
    warnings,
  };
}

// ---------- 供应商分类采集 ----------

const captureDedupe = createCaptureDedupeStore({
  read: async () => (await getLocal(CATEGORY_DEDUPE_KEY))[CATEGORY_DEDUPE_KEY],
  write: (entries) => setLocal({ [CATEGORY_DEDUPE_KEY]: entries }),
});
// 后端按用户 120 次/分钟限流；本地先削峰到 100 次/分钟，超出时让内容脚本按提示退避。
const captureLimiter = createSlidingWindowLimiter({ limit: 100, windowMs: 60_000 });

// 任务状态的读-改-写全部串行，避免进度消息与标签页事件互相覆盖。
let crawlJobQueue = Promise.resolve();
function withCrawlJobLock(task) {
  const run = crawlJobQueue.then(task, task);
  crawlJobQueue = run.catch(() => undefined);
  return run;
}

async function loadValidatedProfiles() {
  const storedConfig = await migrateStoredProfiles();
  const validation = validateProfiles(storedConfig);
  return validation.valid ? validation.profiles : [];
}

function isTopFrameSender(sender) {
  return sender?.frameId == null || sender.frameId === 0;
}

function isExtensionPageSender(sender) {
  const root = chrome.runtime.getURL('');
  return sender?.id === chrome.runtime.id
    && typeof sender.url === 'string'
    && sender.url.startsWith(root);
}

function parseHttpUrl(value) {
  try {
    const url = new URL(value);
    return /^https?:$/u.test(url.protocol) ? url : null;
  } catch {
    return null;
  }
}

function categoryRejection(httpStatus, errorCode, error) {
  // 本地拒绝也带 HTTP 语义：403/404 让内容脚本熔断，400 只跳过当前块。
  return { ok: false, httpStatus, errorCode, ...(error ? { error } : {}) };
}

// 内容脚本来源校验：必须来自标签页顶层 frame，且页面 origin 属于该供应商 profile。
async function resolveCategorySender(sender, supplierCode) {
  if (!sender?.tab || sender.tab.id == null || !isTopFrameSender(sender)) {
    return categoryRejection(403, 'INVALID_SENDER');
  }
  const pageUrl = parseHttpUrl(sender.url || sender.tab.url);
  if (!pageUrl) return categoryRejection(403, 'INVALID_SENDER');
  const profile = matchProfile(await loadValidatedProfiles(), {
    origin: pageUrl.origin,
    pathname: pageUrl.pathname,
  });
  if (!profile || typeof supplierCode !== 'string' || profile.supplierCode !== supplierCode) {
    return categoryRejection(403, 'SUPPLIER_ORIGIN_MISMATCH');
  }
  if (NON_CAPTURABLE_SUPPLIER_CODES.has(profile.supplierCode)) {
    return categoryRejection(400, 'SUPPLIER_NOT_CAPTURABLE');
  }
  if (!profile.category?.enabled) return categoryRejection(404, 'CATEGORY_CAPTURE_DISABLED');
  return { ok: true, profile, origin: pageUrl.origin, tabId: sender.tab.id };
}

async function getCrawlJob() {
  const { [CATEGORY_JOB_KEY]: job } = await getSession(CATEGORY_JOB_KEY);
  return job && typeof job === 'object' ? job : null;
}

async function getCrawlHistory(supplierCode) {
  const { [CATEGORY_HISTORY_KEY]: history } = await getLocal(CATEGORY_HISTORY_KEY);
  const entry = history && typeof history === 'object' ? history[supplierCode] : null;
  return entry && typeof entry === 'object' ? entry : null;
}

async function recordCrawlHistory(job) {
  const { [CATEGORY_HISTORY_KEY]: history } = await getLocal(CATEGORY_HISTORY_KEY);
  await setLocal({
    [CATEGORY_HISTORY_KEY]: {
      ...(history && typeof history === 'object' ? history : {}),
      [job.supplierCode]: toCrawlHistoryEntry(job),
    },
  });
}

async function saveCrawlJob(job, { recordHistory = true } = {}) {
  await setSession({ [CATEGORY_JOB_KEY]: job });
  if (recordHistory && isTerminalCrawlStatus(job.status)) await recordCrawlHistory(job);
}

// 主动采集中的回传必须属于当前运行任务，且来自任务绑定的标签页。
async function requireRunningCrawlJob(jobId, tabId) {
  const job = await getCrawlJob();
  return !!job
    && job.jobId === jobId
    && job.tabId === tabId
    && job.status === CRAWL_STATUSES.RUNNING;
}

async function postCategoryApi(path, payload) {
  try {
    return await apiRequest(path, { method: 'POST', body: JSON.stringify(payload) });
  } catch (error) {
    return {
      httpStatus: 0,
      success: false,
      errorCode: 'NETWORK_ERROR',
      message: String(error?.message || error),
    };
  }
}

function categoryApiFailure(res) {
  return {
    ok: false,
    httpStatus: res.httpStatus || 0,
    errorCode: res.errorCode || (res.httpStatus ? `HTTP_${res.httpStatus}` : 'NETWORK_ERROR'),
    error: res.message || null,
    retryAfterMs: parseRetryAfter(res.retryAfter),
    ...(res.httpStatus ? {} : { networkError: true }),
  };
}

async function handleCategoryCapture(message, sender) {
  const payload = message?.payload;
  const source = await resolveCategorySender(sender, payload?.supplierCode);
  if (!source.ok) return source;
  const category = source.profile.category;
  if (payload?.mode === 'passive' && !category.passiveEnabled) {
    return categoryRejection(404, 'CATEGORY_CAPTURE_DISABLED');
  }
  if (payload?.mode === 'crawl') {
    if (!category.crawlEnabled) return categoryRejection(404, 'CATEGORY_CAPTURE_DISABLED');
    if (!(await requireRunningCrawlJob(message.jobId, source.tabId))) {
      return categoryRejection(403, 'CRAWL_JOB_MISMATCH');
    }
  }
  const validation = validateCapturePayload(payload, {
    senderOrigin: source.origin,
    expectedSupplierCode: source.profile.supplierCode,
  });
  if (!validation.ok) return categoryRejection(400, validation.errorCode, validation.error);

  // 6 小时内同一分类路径 + 同一批货号不重复回传。
  const dedupeKey = buildCaptureDedupeKey(validation.payload);
  if (await captureDedupe.has(dedupeKey)) return { ok: true, deduped: true };
  const permit = captureLimiter.tryAcquire();
  if (!permit.ok) {
    return { ok: false, httpStatus: 429, errorCode: 'LOCAL_RATE_LIMITED', retryAfterMs: permit.retryAfterMs };
  }
  const res = await postCategoryApi(CATEGORY_CAPTURES_PATH, validation.payload);
  if (!res.success) return categoryApiFailure(res);
  await captureDedupe.add(dedupeKey);
  return { ok: true, data: normalizeCaptureResponse(res.data) };
}

async function handleCategoryTreeSnapshot(message, sender) {
  const payload = message?.payload;
  const source = await resolveCategorySender(sender, payload?.supplierCode);
  if (!source.ok) return source;
  if (!source.profile.category.crawlEnabled) return categoryRejection(404, 'CATEGORY_CAPTURE_DISABLED');
  if (!(await requireRunningCrawlJob(message.jobId, source.tabId))) {
    return categoryRejection(403, 'CRAWL_JOB_MISMATCH');
  }
  const validation = validateTreeSnapshotPayload(payload, {
    senderOrigin: source.origin,
    expectedSupplierCode: source.profile.supplierCode,
  });
  if (!validation.ok) return categoryRejection(400, validation.errorCode, validation.error);
  const permit = captureLimiter.tryAcquire();
  if (!permit.ok) {
    return { ok: false, httpStatus: 429, errorCode: 'LOCAL_RATE_LIMITED', retryAfterMs: permit.retryAfterMs };
  }
  const res = await postCategoryApi(CATEGORY_TREE_SNAPSHOT_PATH, validation.payload);
  if (!res.success) return categoryApiFailure(res);
  return { ok: true, data: normalizeTreeSnapshotResponse(res.data) };
}

async function isTabAlive(tabId) {
  try {
    await chrome.tabs.get(tabId);
    return true;
  } catch {
    return false;
  }
}

async function handleCategoryCrawlStart(message, sender) {
  if (!isExtensionPageSender(sender)) return { ok: false, errorCode: 'FORBIDDEN' };
  let tab = null;
  try {
    [tab] = await assistantPanel.queryActiveTabs();
  } catch {
    tab = null;
  }
  const tabUrl = parseHttpUrl(tab?.url);
  if (tab?.id == null || !tabUrl) return { ok: false, errorCode: 'SUPPLIER_TAB_REQUIRED' };
  const profile = matchProfile(await loadValidatedProfiles(), {
    origin: tabUrl.origin,
    pathname: tabUrl.pathname,
  });
  if (!profile || (message?.supplierCode && profile.supplierCode !== message.supplierCode)) {
    return { ok: false, errorCode: 'SUPPLIER_TAB_REQUIRED' };
  }
  if (
    NON_CAPTURABLE_SUPPLIER_CODES.has(profile.supplierCode)
    || !profile.category?.enabled
    || !profile.category.crawlEnabled
  ) {
    return { ok: false, errorCode: 'CRAWL_DISABLED' };
  }

  return withCrawlJobLock(async () => {
    let existing = await getCrawlJob();
    if (existing?.status === CRAWL_STATUSES.RUNNING) {
      // 单飞：同一时间只允许一个主动采集任务；标签页已关闭或长时间无进度的任务视为中断。
      if ((await isTabAlive(existing.tabId)) && !isCrawlJobStale(existing)) {
        return { ok: false, errorCode: 'CRAWL_ALREADY_RUNNING', job: existing };
      }
      existing = finalizeCrawlJob(existing, CRAWL_STATUSES.INTERRUPTED);
      await saveCrawlJob(existing);
    }
    const mode = message?.mode === 'resume' || message?.mode === 'retry' ? message.mode : 'full';
    // 续跑/重试的依据来自上次真正运行过的任务摘要（未能启动的任务不写入摘要）。
    const previous = await getCrawlHistory(profile.supplierCode);
    const completedKeys = mode === 'full' ? [] : previous?.completedKeys || [];
    const onlyNodes = mode === 'retry' ? sanitizeCrawlNodes(previous?.failedNodes) : null;
    if (mode === 'retry' && onlyNodes.length === 0) return { ok: false, errorCode: 'NOTHING_TO_RETRY' };

    const job = createCrawlJob({
      jobId: crypto.randomUUID(),
      supplierCode: profile.supplierCode,
      tabId: tab.id,
      origin: tabUrl.origin,
      mode,
      completedKeys,
    });
    await saveCrawlJob(job);
    let response = null;
    try {
      response = await chrome.tabs.sendMessage(tab.id, {
        type: 'CATEGORY_CRAWL_RUN',
        jobId: job.jobId,
        supplierCode: profile.supplierCode,
        completedKeys,
        onlyNodes,
      });
    } catch {
      response = null;
    }
    if (!response?.ok) {
      const errorCode = response?.errorCode || 'CONTENT_SCRIPT_UNAVAILABLE';
      const failed = finalizeCrawlJob(job, CRAWL_STATUSES.FAILED, { errorCode });
      // 未真正开始的任务不覆盖上次采集摘要，保留“继续/重试失败”的依据。
      await saveCrawlJob(failed, { recordHistory: false });
      return { ok: false, errorCode, job: failed };
    }
    return { ok: true, job };
  });
}

async function handleCategoryCrawlAbort(sender) {
  if (!isExtensionPageSender(sender)) return { ok: false, errorCode: 'FORBIDDEN' };
  return withCrawlJobLock(async () => {
    const job = await getCrawlJob();
    if (!job || job.status !== CRAWL_STATUSES.RUNNING) return { ok: true, job };
    try {
      await chrome.tabs.sendMessage(job.tabId, { type: 'CATEGORY_CRAWL_STOP', jobId: job.jobId });
    } catch {
      // 标签页已关闭或内容脚本失效：直接记为中止。
    }
    const aborted = finalizeCrawlJob(job, CRAWL_STATUSES.ABORTED);
    await saveCrawlJob(aborted);
    return { ok: true, job: aborted };
  });
}

async function handleCategoryCrawlProgress(message, sender) {
  if (!sender?.tab || sender.tab.id == null || !isTopFrameSender(sender)) {
    return { ok: false, errorCode: 'INVALID_SENDER' };
  }
  return withCrawlJobLock(async () => {
    const job = await getCrawlJob();
    if (!job || job.jobId !== message?.jobId || job.tabId !== sender.tab.id) {
      return { ok: false, errorCode: 'CRAWL_JOB_MISMATCH' };
    }
    const next = mergeCrawlProgress(job, message.progress);
    await saveCrawlJob(next);
    return { ok: true, status: next.status };
  });
}

// 任务标签页关闭或整页导航（内容脚本随之销毁）时标记为已中断，可在侧栏“继续”。
function interruptCrawlForTab(tabId) {
  return withCrawlJobLock(async () => {
    const job = await getCrawlJob();
    if (!job || job.tabId !== tabId || job.status !== CRAWL_STATUSES.RUNNING) return;
    await saveCrawlJob(finalizeCrawlJob(job, CRAWL_STATUSES.INTERRUPTED));
  }).catch(() => undefined);
}

chrome.tabs.onRemoved.addListener((tabId) => {
  void interruptCrawlForTab(tabId);
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo?.status === 'loading') void interruptCrawlForTab(tabId);
});

async function migrateStoredProfiles() {
  const { [PROFILES_KEY]: storedConfig } = await getLocal(PROFILES_KEY);
  const migrated = migrateProfileConfig(storedConfig);
  if (migrated !== storedConfig) await setLocal({ [PROFILES_KEY]: migrated });
  return migrated;
}

async function handleRelease() {
  const res = await apiRequest('/api/react/v1/browser-extension/release', { method: 'GET' });
  if (!res.success) return { ok: false, error: res.message || res.errorCode || '获取版本失败' };
  return { ok: true, release: res.data };
}

async function handleSummaryBatch({ storeCode, supplierCode, itemNumbers, salesRankingDays }) {
  if (!storeCode || !supplierCode || !Array.isArray(itemNumbers)) {
    return { ok: false, error: '参数缺失' };
  }
  const res = await apiRequest('/api/react/v1/browser-extension/product-purchase-cycle-summary/batch', {
    method: 'POST',
    body: JSON.stringify({
      storeCode,
      supplierCode,
      itemNumbers,
      salesRankingDays: normalizeRankingDays(salesRankingDays),
    }),
  });
  if (!res.success) return { ok: false, error: res.message || res.errorCode || '摘要获取失败' };
  return { ok: true, data: res.data };
}

async function handlePurchaseCycles({ storeCode, supplierCode, itemNumber }) {
  if (!storeCode || !supplierCode || !itemNumber) {
    return { ok: false, error: '参数缺失' };
  }
  const res = await apiRequest('/api/react/v1/browser-extension/product-purchase-cycles', {
    method: 'POST',
    body: JSON.stringify({ storeCode, supplierCode, itemNumber }),
  });
  if (!res.success) return { ok: false, error: res.message || res.errorCode || '采购周期获取失败' };
  return { ok: true, data: res.data };
}

async function handleStores() {
  const res = await apiRequest('/api/react/v1/browser-extension/stores', { method: 'GET' });
  if (!res.success) return { ok: false, error: res.message || res.errorCode || '门店获取失败' };
  return { ok: true, data: res.data };
}

async function handleSupplierTopSales({ supplierCode, days, topPercent, page, pageSize }) {
  if (!supplierCode) return { ok: false, error: '供应商代码缺失' };
  let pagination;
  try {
    pagination = normalizeTopSalesRequest({ topPercent, page, pageSize });
  } catch (error) {
    return { ok: false, error: error.message };
  }
  const res = await apiRequest('/api/react/v1/browser-extension/supplier-top-sales', {
    method: 'POST',
    body: JSON.stringify({
      supplierCode,
      days: normalizeRankingDays(days),
      ...(pagination || {}),
    }),
  });
  if (!res.success) return { ok: false, error: res.message || res.errorCode || '热销排行获取失败' };
  return { ok: true, data: res.data, apiOrigin: await getApiOrigin() };
}

async function handleSupplierProductStoreSales(message) {
  const {
    supplierCode,
    productCode,
    days,
    startDate,
    endDate,
    totalSalesQuantity,
    snapshotVersion,
  } = message || {};
  if (
    !supplierCode
    || !productCode
    || !startDate
    || !endDate
    || !snapshotVersion
    || !Number.isFinite(Number(totalSalesQuantity))
  ) {
    return { ok: false, error: '排行榜商品快照不完整，请刷新排行榜后重试' };
  }
  const res = await apiRequest('/api/react/v1/browser-extension/supplier-product-store-sales', {
    method: 'POST',
    body: JSON.stringify({
      supplierCode,
      productCode,
      days: normalizeRankingDays(days),
      startDate,
      endDate,
      expectedTotalSalesQuantity: Number(totalSalesQuantity),
      snapshotVersion,
    }),
  });
  if (!res.success) {
    return {
      ok: false,
      error: res.message || res.errorCode || '商品分店销量获取失败',
      errorCode: res.errorCode,
    };
  }
  return { ok: true, data: res.data, apiOrigin: await getApiOrigin() };
}

async function handleActiveSupplier() {
  let tabs = [];
  try {
    tabs = await assistantPanel.queryActiveTabs();
  } catch {
    return { ok: true, supplier: null };
  }
  const href = tabs[0] && tabs[0].url;
  if (!href) return { ok: true, supplier: null };

  let url;
  try {
    url = new URL(href);
  } catch {
    return { ok: true, supplier: null };
  }
  const storedConfig = await migrateStoredProfiles();
  const validation = validateProfiles(storedConfig);
  const profile = validation.valid
    ? matchProfile(validation.profiles, { origin: url.origin, pathname: url.pathname })
    : null;
  return {
    ok: true,
    supplier: profile
      ? { supplierCode: profile.supplierCode, displayName: profile.displayName }
      : null,
  };
}

// 根据已授权 origin 同步动态内容脚本
async function syncContentScripts() {
  const stored = await getLocal([GRANTED_KEY, PROFILES_KEY]);
  const granted = Array.isArray(stored[GRANTED_KEY]) ? stored[GRANTED_KEY] : [];
  const validation = validateProfiles(stored[PROFILES_KEY]);
  const origins = await resolveGrantedProfileOrigins(
    validation.valid ? validation.profiles : [],
    (origin) => chrome.permissions.contains({ origins: [origin] }),
  );
  if (origins.length !== granted.length || origins.some((origin, index) => origin !== granted[index])) {
    await setLocal({ [GRANTED_KEY]: origins });
  }
  try {
    await chrome.scripting.unregisterContentScripts({ ids: ['hb-supplier-list'] });
  } catch {
    // 无动态脚本时忽略
  }
  if (origins.length) {
    await chrome.scripting.registerContentScripts([
      {
        id: 'hb-supplier-list',
        matches: origins,
        js: ['content/list.js'],
        runAt: 'document_idle',
        allFrames: false,
      },
    ]);
  }
}

async function handleRegisterOrigin({ originPattern }) {
  if (typeof originPattern !== 'string' || !originPattern) {
    return { ok: false, error: 'origin 缺失' };
  }
  const stored = await getLocal([GRANTED_KEY, PROFILES_KEY]);
  const validation = validateProfiles(stored[PROFILES_KEY]);
  const allowedOrigins = new Set(
    (validation.valid ? validation.profiles : [])
      .filter((profile) => profile.enabled !== false)
      .flatMap((profile) => profile.origins || []),
  );
  if (!allowedOrigins.has(originPattern)) {
    return { ok: false, error: 'origin 不在已启用供应商配置中' };
  }
  if (!(await chrome.permissions.contains({ origins: [originPattern] }))) {
    return { ok: false, error: 'origin 尚未获得浏览器授权' };
  }
  const granted = stored[GRANTED_KEY];
  const set = new Set(Array.isArray(granted) ? granted : []);
  set.add(originPattern);
  const grantedList = [...set];
  await setLocal({ [GRANTED_KEY]: grantedList });
  await syncContentScripts();
  return { ok: true, granted: grantedList };
}

function friendlySidePanelError(e) {
  const msg = String((e && e.message) || e);
  if (/user gesture/i.test(msg)) return '需要用户操作';
  if (/tab/i.test(msg)) return '未找到标签页';
  return msg || '打开侧栏失败';
}

async function focusTab(tab) {
  if (tab.windowId != null && chrome.windows?.update) {
    try {
      await chrome.windows.update(tab.windowId, { focused: true });
    } catch {
      // Safari 或受限窗口环境不支持聚焦时，激活标签页仍可继续。
    }
  }
  return chrome.tabs.update(tab.id, { active: true });
}

async function handleOpenHbShop() {
  await setSession({ [PENDING_HANDOFF_KEY]: true });

  const shopTabs = await findTrustedShopTabs();
  if (shopTabs.length) {
    await focusTab(shopTabs[0]);
    const handoff = await ensureWebsiteSession();
    return { ok: true, connected: !!handoff.ok, reason: handoff.reason };
  }

  const webTabs = await chrome.tabs.query({ url: `${HB_WEB_ORIGIN}/*` });
  const existing = webTabs.find((tab) => tab.id != null);
  if (existing) {
    await chrome.tabs.update(existing.id, { url: `${HB_WEB_ORIGIN}/shop`, active: true });
    if (existing.windowId != null && chrome.windows?.update) {
      try {
        await chrome.windows.update(existing.windowId, { focused: true });
      } catch {
        // 标签页导航已经成功，窗口聚焦失败不影响授权。
      }
    }
    return { ok: true, connected: false, pending: true };
  }

  await chrome.tabs.create({ url: `${HB_WEB_ORIGIN}/shop`, active: true });
  return { ok: true, connected: false, pending: true };
}

const openHbShop = createSingleFlight(handleOpenHbShop);

async function handleShopBridgeReady(sender) {
  const senderUrl = sender?.tab?.url || sender?.url;
  const source = validateWebsiteSessionContext({
    pageUrl: senderUrl,
    webOrigin: HB_WEB_ORIGIN,
    apiOrigin: HB_WEB_ORIGIN,
    isTopLevel: sender?.frameId == null || sender.frameId === 0,
  });
  if (!source.ok) return { ok: false, reason: source.reason };

  const [{ [PENDING_HANDOFF_KEY]: pending }, apiOrigin] = await Promise.all([
    getSession(PENDING_HANDOFF_KEY),
    getApiOrigin(),
  ]);
  return {
    ok: true,
    shouldAuthorize: pending === true,
    apiOrigin,
  };
}

function openSidePanel(sender, pendingLocate) {
  const tabId = sender && sender.tab && sender.tab.id;
  if (tabId == null) return Promise.resolve({ ok: false, error: '缺少标签页' });
  // 先同步调用 open 保留用户手势，再异步返回状态
  const openPromise = assistantPanel.open({ tabId });
  const locatePromise = pendingLocate
    ? chrome.storage.session.set({ pendingLocate })
    : Promise.resolve();
  return Promise.all([openPromise, locatePromise])
    .then(() => ({ ok: true }))
    .catch((e) => ({ ok: false, error: friendlySidePanelError(e) }));
}

chrome.runtime.onInstalled.addListener(async () => {
  await clearLegacyCredentials();
  try {
    await assistantPanel.configureAction();
  } catch {
    // 某些环境不支持，忽略
  }
  const existing = await migrateStoredProfiles();
  if (!existing) await setLocal({ [PROFILES_KEY]: DEFAULT_PROFILES });
  await syncContentScripts();
});

chrome.runtime.onStartup.addListener(async () => {
  await clearLegacyCredentials();
  await migrateStoredProfiles();
  await syncContentScripts();
});

// Service Worker 被浏览器直接唤醒时也执行幂等迁移，确保旧长期凭据立即消失。
void clearLegacyCredentials().catch(() => {});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const type = message && message.type;
  const run = async () => {
    switch (type) {
      case 'GET_API_ORIGIN':
        return handleGetApiOrigin();
      case 'SET_API_ORIGIN':
        return handleSetApiOrigin(message);
      case 'CURRENT':
        return handleCurrent();
      case 'WEBSITE_SESSION_GRANT':
        return acceptWebsiteSessionGrant(message, sender);
      case 'SHOP_BRIDGE_READY':
        return handleShopBridgeReady(sender);
      case 'DISCONNECT':
        return handleDisconnect();
      case 'OPEN_HB_SHOP':
        return openHbShop();
      case 'RELEASE':
        return handleRelease();
      case 'GET_PROFILES':
        return handleGetProfiles();
      case 'SUMMARY_BATCH':
        return handleSummaryBatch(message);
      case 'PURCHASE_CYCLES':
        return handlePurchaseCycles(message);
      case 'GET_STORES':
        return handleStores();
      case 'SUPPLIER_TOP_SALES':
        return handleSupplierTopSales(message);
      case 'SUPPLIER_PRODUCT_STORE_SALES':
        return handleSupplierProductStoreSales(message);
      case 'ACTIVE_SUPPLIER':
        return handleActiveSupplier();
      case 'REGISTER_ORIGIN':
        return handleRegisterOrigin(message);
      case 'OPEN_SIDE_PANEL':
        return openSidePanel(sender);
      case 'LOCATE_ITEM':
        return openSidePanel(sender, {
          storeCode: message.storeCode,
          supplierCode: message.supplierCode,
          itemNumber: message.itemNumber,
        });
      case 'CATEGORY_CAPTURE':
        return handleCategoryCapture(message, sender);
      case 'CATEGORY_TREE_SNAPSHOT':
        return handleCategoryTreeSnapshot(message, sender);
      case 'CATEGORY_CRAWL_START':
        return handleCategoryCrawlStart(message, sender);
      case 'CATEGORY_CRAWL_ABORT':
        return handleCategoryCrawlAbort(sender);
      case 'CATEGORY_CRAWL_PROGRESS':
        return handleCategoryCrawlProgress(message, sender);
      default:
        return { ok: false, error: '未知消息类型' };
    }
  };
  run()
    .then(sendResponse)
    .catch((err) => sendResponse({ ok: false, error: String((err && err.message) || err) }));
  return true;
});
