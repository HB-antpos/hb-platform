// 供应商分类路径的纯逻辑：分类键归一化、名称整理、页面判定、促销判定与路径解析。
// 本模块不依赖 DOM 与浏览器 API，内容脚本、service worker 与 node --test 共用。
// 安全边界：后台下发的模式只按 glob（仅 * 通配）解释，绝不当作正则或代码执行。

export const MAX_CATEGORY_KEY_LENGTH = 300;
export const MAX_CATEGORY_NAME_LENGTH = 200;
export const MAX_CATEGORY_URL_LENGTH = 1000;
export const MAX_CATEGORY_PATH_DEPTH = 8;
export const MAX_KEY_QUERY_PARAMS = 5;
export const KEY_QUERY_PARAM_PATTERN = /^[A-Za-z0-9_\-[\]]{1,50}$/u;

// 与后端一致的内置排除路径：搜索、购物车、结账、账户等页面永远不是分类页。
export const BUILTIN_CATEGORY_EXCLUDE_PATTERNS = Object.freeze([
  '/search*',
  '/cart*',
  '/checkout*',
  '/account*',
  '/login*',
  '/my-account*',
  '/wishlist*',
]);

// 默认促销分类模式（小写 glob）；不含 *sale*，避免误伤 wholesale。
export const DEFAULT_PROMOTIONAL_PATTERNS = Object.freeze([
  'clearance*',
  '*-clearance',
  'sale',
  'sale-*',
  'on-sale*',
  'specials*',
  'special-offers*',
  'new-arrivals*',
  'new-in*',
  'whats-new*',
  'new-products*',
  'best-sellers*',
  'bestsellers*',
  'shop-by-*',
  'gift-ideas*',
  'trending*',
  'promotions*',
  'deals*',
]);

const REGEX_SPECIALS = /[|\\{}()[\]^$+?.]/g;
const PAGE_SUFFIX = /\/page\/\d+$/u;
const FILE_EXTENSION = /\.(?:html?|php|aspx?|jsp)$/iu;
const TRAILING_COUNT = /\s*\(\s*\d+\s*\)$/u;

// glob → 正则：只有 * 是通配，其余字符全部按字面量转义。
export function globToRegex(pattern, { caseInsensitive = true } = {}) {
  const source = String(pattern ?? '').replace(REGEX_SPECIALS, '\\$&').replaceAll('*', '.*');
  return new RegExp(`^${source}$`, caseInsensitive ? 'i' : '');
}

// 与 profiles.matchUrlPattern 同语义：/ 开头按路径匹配，否则按完整 URL 匹配。
export function matchesPagePattern(pattern, href) {
  if (typeof pattern !== 'string' || !pattern || typeof href !== 'string') return false;
  let target = href;
  if (pattern.startsWith('/')) {
    try {
      target = new URL(href).pathname;
    } catch {
      return false;
    }
  }
  return globToRegex(pattern).test(target);
}

function safeDecode(value) {
  try {
    return decodeURIComponent(value);
  } catch {
    // 非法百分号编码保留原文，避免整条链接被丢弃。
    return value;
  }
}

function toUrl(input) {
  if (input instanceof URL) return input;
  if (typeof input !== 'string' || !input) return null;
  try {
    return new URL(input);
  } catch {
    return null;
  }
}

function normalizeKeyPath(rawPath) {
  let path = safeDecode(rawPath || '');
  path = path.replace(/\/{2,}/gu, '/').toLowerCase();
  if (!path.startsWith('/')) path = `/${path}`;
  // 去尾斜杠（根保留 /），再剥离分页后缀；剥离后可能又露出尾斜杠。
  const trimTrailing = (value) => (value.length > 1 ? value.replace(/\/+$/u, '') || '/' : value);
  path = trimTrailing(path);
  path = path.replace(PAGE_SUFFIX, '') || '/';
  return trimTrailing(path);
}

function splitHash(hash) {
  let value = String(hash || '').replace(/^#/u, '');
  if (value.startsWith('!')) value = value.slice(1);
  const queryIndex = value.indexOf('?');
  return {
    path: queryIndex === -1 ? value : value.slice(0, queryIndex),
    query: queryIndex === -1 ? '' : value.slice(queryIndex + 1),
  };
}

// 归一化配置的查询参数白名单：去重（忽略大小写）、只保留合法名称、按名排序。
export function normalizeKeyQueryParams(params) {
  const names = new Map();
  for (const name of Array.isArray(params) ? params : []) {
    if (typeof name !== 'string' || !KEY_QUERY_PARAM_PATTERN.test(name)) continue;
    const lower = name.toLowerCase();
    if (!names.has(lower)) names.set(lower, lower);
  }
  return [...names.values()].sort();
}

function readQueryValue(searchParams, name) {
  for (const [key, value] of searchParams) {
    if (key.toLowerCase() === name) {
      const normalized = String(value ?? '').trim().toLowerCase();
      if (normalized) return normalized;
    }
  }
  return null;
}

// 分类稳定键（与后端同算法）：路径 → decode → 合并斜杠/去尾斜杠/小写 → 剥离 /page/<n>
// → 追加白名单查询参数（按名排序，值 trim+小写）→ 必须以 / 开头且 ≤300 字符。
export function normalizeCategoryKey(input, { keySource = 'pathname', keyQueryParams = [] } = {}) {
  const url = toUrl(input);
  if (!url) return null;
  let rawPath = url.pathname;
  let hashQuery = '';
  if (keySource === 'hash') {
    const parts = splitHash(url.hash);
    rawPath = parts.path;
    hashQuery = parts.query;
  }
  let key = normalizeKeyPath(rawPath);
  const params = normalizeKeyQueryParams(keyQueryParams);
  if (params.length > 0) {
    const hashParams = new URLSearchParams(hashQuery);
    const pairs = [];
    for (const name of params) {
      // hash 路由自带的查询串优先，其次才是页面 URL 查询串。
      const value = readQueryValue(hashParams, name) ?? readQueryValue(url.searchParams, name);
      if (value != null) pairs.push(`${name}=${value}`);
    }
    if (pairs.length > 0) key = `${key}?${pairs.join('&')}`;
  }
  if (!key.startsWith('/') || key.length > MAX_CATEGORY_KEY_LENGTH) return null;
  return key;
}

// 分类名称：合并空白、去掉导航里常见的尾部计数 “(12)”、限制 200 字符。
export function normalizeCategoryName(name) {
  if (name == null) return '';
  let value = String(name).replace(/\s+/gu, ' ').trim();
  value = value.replace(TRAILING_COUNT, '').trim();
  if (value.length > MAX_CATEGORY_NAME_LENGTH) value = value.slice(0, MAX_CATEGORY_NAME_LENGTH).trim();
  return value;
}

// URL 段 → 可读名称：office-stationery → Office Stationery；去掉 .html 等扩展名。
export function humanizeSlug(slug) {
  const decoded = safeDecode(String(slug ?? '')).replace(FILE_EXTENSION, '');
  const words = decoded.replace(/[-_+]+/gu, ' ').replace(/\s+/gu, ' ').trim();
  if (!words) return '';
  return normalizeCategoryName(
    words
      .split(' ')
      .map((word) => (word ? word[0].toUpperCase() + word.slice(1) : word))
      .join(' '),
  );
}

// 页面是否为分类页：先排除，再按 categoryPagePatterns（归一化时已回退到 listPagePatterns）匹配。
export function matchesCategoryPage(href, config) {
  if (typeof href !== 'string' || !config) return false;
  const excludes = Array.isArray(config.categoryExcludePatterns)
    ? config.categoryExcludePatterns
    : BUILTIN_CATEGORY_EXCLUDE_PATTERNS;
  if (excludes.some((pattern) => matchesPagePattern(pattern, href))) return false;
  const patterns = Array.isArray(config.categoryPagePatterns) ? config.categoryPagePatterns : [];
  return patterns.some((pattern) => matchesPagePattern(pattern, href));
}

// 促销模式匹配（小写 glob）。
export function matchesPromotional(value, patterns = DEFAULT_PROMOTIONAL_PATTERNS) {
  if (typeof value !== 'string' || !value) return false;
  const target = value.toLowerCase();
  return (patterns || []).some((pattern) => typeof pattern === 'string' && pattern
    && globToRegex(pattern.toLowerCase()).test(target));
}

// 同时检查 key 的每一段、完整 key 与名称（空格→-），与后端判定规则一致。
export function isPromotionalNode(node, patterns = DEFAULT_PROMOTIONAL_PATTERNS) {
  if (!node) return false;
  const key = typeof node.key === 'string' ? node.key : '';
  const path = key.split('?')[0];
  const segments = path
    .split('/')
    .filter(Boolean)
    .map((segment) => segment.replace(FILE_EXTENSION, ''));
  const candidates = [...segments, key];
  const name = normalizeCategoryName(node.name).toLowerCase().replace(/\s+/gu, '-');
  if (name) candidates.push(name);
  return candidates.some((candidate) => matchesPromotional(candidate, patterns));
}

export function isPromotionalPath(path, patterns = DEFAULT_PROMOTIONAL_PATTERNS) {
  return Array.isArray(path) && path.some((node) => isPromotionalNode(node, patterns));
}

// 节点 URL 只保留同源 http(s) 链接，超长返回 null（后端 Url 字段上限 1000）。
function cleanUrl(value, origin) {
  const url = toUrl(value instanceof URL ? value.href : value);
  if (!url || !/^https?:$/u.test(url.protocol)) return null;
  if (origin && url.origin !== origin) return null;
  const href = url.href;
  return href.length <= MAX_CATEGORY_URL_LENGTH ? href : null;
}

function buildNode(name, key, url, origin) {
  const normalizedName = normalizeCategoryName(name);
  if (!normalizedName || !key) return null;
  return { name: normalizedName, key, url: cleanUrl(url, origin) };
}

function dedupeConsecutive(path) {
  const out = [];
  for (const node of path) {
    if (out.length > 0 && out[out.length - 1].key === node.key) {
      out[out.length - 1] = node;
      continue;
    }
    out.push(node);
  }
  return out;
}

function resolveFromBreadcrumb({ items, pageKey, pageUrl, title, config, origin }) {
  const skip = Number.isInteger(config.breadcrumbSkip) ? config.breadcrumbSkip : 1;
  const effective = (Array.isArray(items) ? items : []).slice(skip);
  if (effective.length === 0) return null;
  const path = [];
  for (let index = 0; index < effective.length; index += 1) {
    const item = effective[index] || {};
    const isLast = index === effective.length - 1;
    let key = null;
    if (item.url) key = normalizeCategoryKey(item.url, config);
    // 面包屑末项常常没有链接（就是当前页），用当前页 key 补齐。
    if (!key && isLast) key = pageKey;
    if (!key) return null;
    // 首页（/）永远不是分类，跳过配置遗漏的 Home 节点。
    if (key === '/') continue;
    const node = buildNode(item.name, key, item.url || (key === pageKey ? pageUrl : null), origin);
    if (!node) {
      if (isLast) continue;
      return null;
    }
    path.push(node);
  }
  if (path.length === 0) return null;
  const leaf = path[path.length - 1];
  if (leaf.key !== pageKey) {
    // 面包屑只到父级（如 WooCommerce 末项是纯文本）：用页面标题补叶子。
    const leafName = normalizeCategoryName(title) || humanizeLastSegment(pageKey);
    const extra = buildNode(leafName, pageKey, pageUrl, origin);
    if (!extra) return null;
    path.push(extra);
  }
  return dedupeConsecutive(path);
}

function humanizeLastSegment(key) {
  const path = String(key || '').split('?')[0];
  const segments = path.split('/').filter(Boolean);
  return humanizeSlug(segments[segments.length - 1] || '');
}

function resolveFromUrlSegments({ pageKey, pageUrl, title, config, origin }) {
  if (pageKey.includes('?')) return null;
  const segments = pageKey.split('/').filter(Boolean);
  if (segments.length === 0) return null;
  const path = [];
  for (let index = 0; index < segments.length; index += 1) {
    const key = `/${segments.slice(0, index + 1).join('/')}`;
    const isLast = index === segments.length - 1;
    const url = origin ? `${origin}${key}` : null;
    // 中间段必须本身也像分类页（如 /product-category 前缀段会被 /product-category/* 排除）。
    if (!isLast && url && !matchesCategoryPage(url, config)) continue;
    const name = isLast ? normalizeCategoryName(title) || humanizeSlug(segments[index]) : humanizeSlug(segments[index]);
    const node = buildNode(name, key, isLast ? pageUrl : url, origin);
    if (!node) return null;
    path.push(node);
  }
  return path.length > 0 ? path : null;
}

// 分类路径解析：面包屑 → URL 段 → 标题三级回退；返回根到叶路径（1..8 个节点）或 null。
export function resolveCategoryPath({ pageUrl, breadcrumbItems, title, config }) {
  if (!config) return null;
  const url = toUrl(pageUrl);
  if (!url) return null;
  const origin = url.origin;
  const pageKey = normalizeCategoryKey(url, config);
  if (!pageKey || pageKey === '/') return null;
  const href = cleanUrl(url.href, origin);

  const attempts = [
    ['breadcrumb', () => resolveFromBreadcrumb({
      items: breadcrumbItems, pageKey, pageUrl: href, title, config, origin,
    })],
    ['url', () => (config.keySource === 'hash' || (config.keyQueryParams || []).length > 0
      ? null
      : resolveFromUrlSegments({ pageKey, pageUrl: href, title, config, origin }))],
    ['title', () => {
      const node = buildNode(title, pageKey, href, origin);
      return node ? [node] : null;
    }],
  ];
  for (const [source, attempt] of attempts) {
    const path = attempt();
    if (!path || path.length === 0) continue;
    if (path.length > MAX_CATEGORY_PATH_DEPTH) return null;
    return { source, path, leafKey: path[path.length - 1].key };
  }
  return null;
}

// 从 URL 推断分页页码（/page/2、?page=2、?PageProduct=2 等），无法识别返回 undefined。
export function detectPageNumber(href) {
  const url = toUrl(href);
  if (!url) return undefined;
  const pathMatch = /\/page\/(\d+)\/?$/iu.exec(url.pathname);
  const candidates = pathMatch ? [pathMatch[1]] : [];
  for (const [name, value] of url.searchParams) {
    if (/^(?:page|paged|p|pg|pagenumber|pageproduct|pageno)$/iu.test(name)) candidates.push(value);
  }
  for (const candidate of candidates) {
    const number = Number(candidate);
    if (Number.isInteger(number) && number >= 1 && number <= 10000) return number;
  }
  return undefined;
}
