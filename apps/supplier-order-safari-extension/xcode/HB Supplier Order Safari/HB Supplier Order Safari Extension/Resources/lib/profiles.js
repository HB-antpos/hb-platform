import { safeTransformList } from './transforms.js';
import {
  BUILTIN_CATEGORY_EXCLUDE_PATTERNS,
  DEFAULT_PROMOTIONAL_PATTERNS,
  KEY_QUERY_PARAM_PATTERN,
  MAX_KEY_QUERY_PARAMS,
  normalizeKeyQueryParams,
} from './category-path.js';

export const ALLOWED_SOURCES = new Set(['attribute', 'text']);
export const ALLOWED_MOUNT_POSITIONS = new Set(['beforebegin', 'afterbegin', 'beforeend', 'afterend']);

const TXK_HTTP_PATTERN = /^http:\/\/txkorders\.inzantsales\.com(?<path>\/[^\s]*)$/i;

function isSafeMatchPattern(value, originOnly = false) {
  if (typeof value !== 'string' || value.length === 0 || value.length > 300) return false;
  const match = /^https:\/\/(?:\*\.)?[A-Za-z0-9.-]+(?::\d+)?(?<path>\/[^\s]*)$/.exec(value)
    || TXK_HTTP_PATTERN.exec(value);
  return !!match && (!originOnly || match.groups.path === '/*');
}

function isSafePagePattern(value) {
  return (
    (typeof value === 'string' && value.startsWith('/') && value.length <= 300)
    || isSafeMatchPattern(value)
  );
}

export function originMatchesAny(origins, origin) {
  return (origins || []).some((pattern) => matchUrlPattern(pattern, `${origin}/`));
}

function escapeRegex(value) {
  return value.replace(/[|\\{}()[\]^$+?.]/g, '\\$&');
}

// 只解释 Chrome 风格 https match pattern 或路径 glob，不把后台字符串当 JavaScript/正则执行。
export function matchUrlPattern(pattern, href) {
  if (typeof pattern !== 'string' || !pattern || typeof href !== 'string') return false;
  let target = href;
  let candidate = pattern;
  if (candidate.startsWith('/')) {
    try {
      target = new URL(href).pathname;
    } catch {
      return false;
    }
  }
  const regex = `^${escapeRegex(candidate).replaceAll('*', '.*')}$`;
  return new RegExp(regex, 'i').test(target);
}

export function matchesListPage(listPagePatterns, href) {
  return (listPagePatterns || []).some((pattern) => matchUrlPattern(pattern, href));
}

// Hot Bargain 自营供应商的分类就是仓库分类，扩展永不采集。
export const NON_CAPTURABLE_SUPPLIER_CODES = new Set(['200']);

const CATEGORY_NUMBER_RULES = {
  breadcrumbSkip: { fallback: 1, min: 0, max: 5 },
  maxPages: { fallback: 20, min: 1, max: 50 },
  maxDepth: { fallback: 4, min: 1, max: 6 },
  maxCategories: { fallback: 400, min: 10, max: 2000 },
  crawlDelayMs: { fallback: 1500, min: 500, max: 15000 },
};

// 未配置（null/空串）时的选择器默认值；其余选择器缺省为 null（表示不使用）。
const CATEGORY_SELECTOR_DEFAULTS = {
  breadcrumbSelector: null,
  titleSelector: 'h1',
  navSelector: null,
  subcategoryLinkSelector: null,
  paginationNextSelector: 'a[rel="next"]',
};

const MAX_CATEGORY_SELECTOR_LENGTH = 500;
const MAX_PROMOTIONAL_PATTERNS = 50;
const MAX_PROMOTIONAL_PATTERN_LENGTH = 100;

function uniqueStrings(values) {
  return [...new Set(values)];
}

function normalizeCategorySelector(value, fallback, name, errors) {
  if (value == null || value === '') return fallback;
  if (
    typeof value !== 'string'
    || value.length > MAX_CATEGORY_SELECTOR_LENGTH
    || /[\r\n]/u.test(value)
  ) {
    errors.push(`${name} 非法`);
    return fallback;
  }
  const trimmed = value.trim();
  return trimmed || fallback;
}

function normalizeCategoryNumber(value, name, errors) {
  const rule = CATEGORY_NUMBER_RULES[name];
  if (value == null) return rule.fallback;
  if (!Number.isInteger(value) || value < rule.min || value > rule.max) {
    errors.push(`${name} 超出范围 ${rule.min}..${rule.max}`);
    return rule.fallback;
  }
  return value;
}

function normalizeCategoryBoolean(value, fallback, name, errors) {
  if (value == null) return fallback;
  if (typeof value !== 'boolean') {
    errors.push(`${name} 必须为 boolean`);
    return fallback;
  }
  return value;
}

function normalizePagePatternList(value, name, errors) {
  if (value == null) return [];
  if (!Array.isArray(value)) {
    errors.push(`${name} 必须为数组`);
    return [];
  }
  const out = [];
  value.forEach((pattern, index) => {
    if (!isSafePagePattern(pattern)) errors.push(`${name}[${index}] 非法`);
    else out.push(pattern);
  });
  return out;
}

function normalizePromotionalPatterns(value, errors) {
  if (value == null) return [...DEFAULT_PROMOTIONAL_PATTERNS];
  if (!Array.isArray(value) || value.length > MAX_PROMOTIONAL_PATTERNS) {
    errors.push(`promotionalPatterns 必须为不超过 ${MAX_PROMOTIONAL_PATTERNS} 项的数组`);
    return [...DEFAULT_PROMOTIONAL_PATTERNS];
  }
  const out = [];
  value.forEach((pattern, index) => {
    const trimmed = typeof pattern === 'string' ? pattern.trim().toLowerCase() : '';
    // glob 只允许 * 通配；? 与控制字符一律拒绝，避免后台误以为支持单字符通配。
    if (
      !trimmed
      || trimmed.length > MAX_PROMOTIONAL_PATTERN_LENGTH
      || /[?\u0000-\u001f\u007f]/u.test(trimmed)
    ) {
      errors.push(`promotionalPatterns[${index}] 非法`);
      return;
    }
    out.push(trimmed);
  });
  // 空列表视为未配置：扩展侧仍按默认模式预过滤，最终以后端判定为准。
  return out.length > 0 ? uniqueStrings(out) : [...DEFAULT_PROMOTIONAL_PATTERNS];
}

function normalizeNavRootUrl(value, profile, errors) {
  if (value == null || value === '') return null;
  if (typeof value !== 'string' || value.length > 1000 || /\s/u.test(value)) {
    errors.push('navRootUrl 非法');
    return null;
  }
  if (value.startsWith('/') && !value.startsWith('//')) return value;
  let url;
  try {
    url = new URL(value);
  } catch {
    errors.push('navRootUrl 非法');
    return null;
  }
  if (!/^https?:$/u.test(url.protocol) || url.username || url.password
    || !originMatchesAny(profile?.origins, url.origin)) {
    errors.push('navRootUrl 必须与供应商同源');
    return null;
  }
  return url.href;
}

// 分类采集配置归一化：任何非法字段只让该供应商的分类采集停用（enabled=false），
// 由调用方记入 warnings，绝不影响 profile 本身（按钮注入仍按原规则 fail-closed）。
export function normalizeCategoryConfig(raw, profile) {
  const errors = [];
  const source = raw == null ? {} : raw;
  if (typeof source !== 'object' || Array.isArray(source)) {
    errors.push('category 必须为对象');
  }
  const input = typeof source === 'object' && !Array.isArray(source) ? source : {};

  const categoryPagePatterns = normalizePagePatternList(
    input.categoryPagePatterns,
    'categoryPagePatterns',
    errors,
  );
  const excludePatterns = normalizePagePatternList(
    input.categoryExcludePatterns,
    'categoryExcludePatterns',
    errors,
  );

  let keySource = 'pathname';
  if (input.keySource != null) {
    const normalized = typeof input.keySource === 'string' ? input.keySource.toLowerCase() : '';
    if (normalized === 'pathname' || normalized === 'hash') keySource = normalized;
    else errors.push('keySource 必须为 pathname 或 hash');
  }

  let keyQueryParams = [];
  if (input.keyQueryParams != null) {
    if (!Array.isArray(input.keyQueryParams) || input.keyQueryParams.length > MAX_KEY_QUERY_PARAMS) {
      errors.push(`keyQueryParams 必须为不超过 ${MAX_KEY_QUERY_PARAMS} 项的数组`);
    } else {
      input.keyQueryParams.forEach((name, index) => {
        if (typeof name !== 'string' || !KEY_QUERY_PARAM_PATTERN.test(name)) {
          errors.push(`keyQueryParams[${index}] 非法`);
        }
      });
      keyQueryParams = normalizeKeyQueryParams(input.keyQueryParams);
    }
  }

  const config = {
    enabled: false,
    passiveEnabled: normalizeCategoryBoolean(input.passiveEnabled, true, 'passiveEnabled', errors),
    crawlEnabled: normalizeCategoryBoolean(input.crawlEnabled, true, 'crawlEnabled', errors),
    // 未单独配置分类页模式时沿用列表页模式。
    categoryPagePatterns: categoryPagePatterns.length > 0
      ? categoryPagePatterns
      : [...(Array.isArray(profile?.listPagePatterns) ? profile.listPagePatterns : [])],
    categoryExcludePatterns: uniqueStrings([...BUILTIN_CATEGORY_EXCLUDE_PATTERNS, ...excludePatterns]),
    breadcrumbSelector: normalizeCategorySelector(
      input.breadcrumbSelector,
      CATEGORY_SELECTOR_DEFAULTS.breadcrumbSelector,
      'breadcrumbSelector',
      errors,
    ),
    breadcrumbSkip: normalizeCategoryNumber(input.breadcrumbSkip, 'breadcrumbSkip', errors),
    titleSelector: normalizeCategorySelector(
      input.titleSelector,
      CATEGORY_SELECTOR_DEFAULTS.titleSelector,
      'titleSelector',
      errors,
    ),
    keySource,
    keyQueryParams,
    navRootUrl: normalizeNavRootUrl(input.navRootUrl, profile, errors),
    navSelector: normalizeCategorySelector(
      input.navSelector,
      CATEGORY_SELECTOR_DEFAULTS.navSelector,
      'navSelector',
      errors,
    ),
    subcategoryLinkSelector: normalizeCategorySelector(
      input.subcategoryLinkSelector,
      CATEGORY_SELECTOR_DEFAULTS.subcategoryLinkSelector,
      'subcategoryLinkSelector',
      errors,
    ),
    paginationNextSelector: normalizeCategorySelector(
      input.paginationNextSelector,
      CATEGORY_SELECTOR_DEFAULTS.paginationNextSelector,
      'paginationNextSelector',
      errors,
    ),
    maxPages: normalizeCategoryNumber(input.maxPages, 'maxPages', errors),
    maxDepth: normalizeCategoryNumber(input.maxDepth, 'maxDepth', errors),
    maxCategories: normalizeCategoryNumber(input.maxCategories, 'maxCategories', errors),
    crawlDelayMs: normalizeCategoryNumber(input.crawlDelayMs, 'crawlDelayMs', errors),
    promotionalPatterns: normalizePromotionalPatterns(input.promotionalPatterns, errors),
  };
  if (input.enabled != null && typeof input.enabled !== 'boolean') {
    errors.push('enabled 必须为 boolean');
  }
  const capturable = !NON_CAPTURABLE_SUPPLIER_CODES.has(profile?.supplierCode);
  config.enabled = input.enabled === true && capturable && errors.length === 0;
  return { config, errors };
}

// 校验 profile 数据，拒绝任何非声明式 transform
export function validateProfiles(raw) {
  if (!raw || typeof raw !== 'object' || !Array.isArray(raw.profiles)) {
    return {
      valid: false,
      profiles: [],
      errors: ['profiles 必须为 {profiles:[...]} 对象'],
      warnings: [],
    };
  }
  const errors = [];
  const warnings = [];
  const out = [];
  raw.profiles.forEach((p, i) => {
    const path = `profiles[${i}]`;
    if (!p || typeof p !== 'object') {
      errors.push(`${path} 不是对象`);
      return;
    }
    const errs = [];
    if (typeof p.supplierCode !== 'string' || !p.supplierCode) errs.push('supplierCode 必填');
    if (typeof p.displayName !== 'string' || !p.displayName) errs.push('displayName 必填');
    if (typeof p.enabled !== 'boolean') errs.push('enabled 必须为 boolean');
    if (!Array.isArray(p.origins) || p.origins.length === 0) {
      errs.push('origins 必须为非空数组');
    } else {
      p.origins.forEach((o, j) => {
        if (!isSafeMatchPattern(o, true)) errs.push(`origins[${j}] 非法`);
      });
    }
    if (!Array.isArray(p.listPagePatterns)) {
      errs.push('listPagePatterns 必须为数组');
    } else {
      p.listPagePatterns.forEach((pattern, j) => {
        if (!isSafePagePattern(pattern)) errs.push(`listPagePatterns[${j}] 非法`);
      });
    }
    if (typeof p.cardSelector !== 'string' || !p.cardSelector) errs.push('cardSelector 必填');
    if (!p.itemNumber || typeof p.itemNumber !== 'object') {
      errs.push('itemNumber 必填');
    } else {
      const it = p.itemNumber;
      if (!ALLOWED_SOURCES.has(it.source)) errs.push('itemNumber.source 非法');
      if (it.source === 'attribute' && (typeof it.attribute !== 'string' || !it.attribute)) {
        errs.push('attribute source 需要 attribute');
      }
      if (it.selector != null && typeof it.selector !== 'string') {
        errs.push('itemNumber.selector 必须为字符串或 null');
      }
      if (!safeTransformList(it.transforms)) errs.push('itemNumber.transforms 包含不支持的 transform');
    }
    if (typeof p.mountSelector !== 'string' || !p.mountSelector) errs.push('mountSelector 必填');
    if (!ALLOWED_MOUNT_POSITIONS.has(p.mountPosition)) errs.push('mountPosition 非法');
    if (errs.length) {
      errors.push(...errs.map((e) => `${path}.${e}`));
      return;
    }
    // 分类块单独归一化：非法只降级该供应商的分类采集并记 warnings，不进 errors。
    const category = normalizeCategoryConfig(p.category, p);
    warnings.push(...category.errors.map((e) => `${path}.category.${e}`));
    out.push({ ...p, category: category.config });
  });
  return { valid: errors.length === 0, profiles: out, errors, warnings };
}

// 按 origin 匹配第一个启用 profile（列表/详情判断交给 shouldInjectList）
export function matchProfile(profiles, { origin, pathname }) {
  for (const p of profiles || []) {
    if (p.enabled === false) continue;
    if (!originMatchesAny(p.origins, origin)) continue;
    return p;
  }
  return null;
}
