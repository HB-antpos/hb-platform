(() => {
  // src/lib/api-response.js
  var AUTH_ERROR_CODES = [
    "UNAUTHORIZED",
    "TOKEN_EXPIRED",
    "AUTH_FAILED",
    "INVALID_TOKEN",
    "LOGIN_REQUIRED",
    "EXPIRED_TOKEN"
  ];
  function isAuthFailure(resp, httpStatus) {
    if (httpStatus === 401) return true;
    if (resp && resp.success === false && typeof resp.errorCode === "string" && AUTH_ERROR_CODES.includes(resp.errorCode.toUpperCase())) {
      return true;
    }
    return false;
  }

  // src/lib/api-origin.js
  var LOCAL_API_ORIGIN = "http://localhost:5002";
  function parseAllowedOrigin(value) {
    try {
      const url = new URL(value);
      const isLocalHttp = url.protocol === "http:" && (url.hostname === "localhost" || url.hostname === "127.0.0.1");
      if (url.protocol !== "https:" && !isLocalHttp) return null;
      if (url.username || url.password || url.pathname !== "/" || url.search || url.hash) return null;
      return url.origin;
    } catch {
      return null;
    }
  }
  function normalizeApiOrigin(value, defaultOrigin) {
    const normalizedDefault = parseAllowedOrigin(String(defaultOrigin || "").trim());
    if (!normalizedDefault) return null;
    const input = String(value ?? "").trim();
    if (!input || input === "/") return normalizedDefault;
    return parseAllowedOrigin(input);
  }
  function resolveApiOrigin(storedOrigin, defaultOrigin) {
    return normalizeApiOrigin(storedOrigin, defaultOrigin) || normalizeApiOrigin("/", defaultOrigin);
  }
  function toApiHostPattern(origin) {
    const parsed = parseAllowedOrigin(String(origin || "").trim());
    return parsed ? `${parsed}/*` : null;
  }

  // src/lib/origin-registration.js
  async function resolveGrantedProfileOrigins(profiles, hasPermission) {
    const allowedOrigins = [
      ...new Set(
        (Array.isArray(profiles) ? profiles : []).filter((profile) => profile && profile.enabled !== false).flatMap((profile) => Array.isArray(profile.origins) ? profile.origins : [])
      )
    ];
    const grantedOrigins = [];
    for (const origin of allowedOrigins) {
      try {
        if (await hasPermission(origin)) grantedOrigins.push(origin);
      } catch {
      }
    }
    return grantedOrigins;
  }

  // src/lib/profiles-default.js
  var DEFAULT_PROFILES = {
    configVersion: "3",
    profiles: [
      {
        // DATS 是显示名称；HB 的供应商业务代码是 240。
        supplierCode: "240",
        displayName: "DATS",
        enabled: true,
        origins: ["https://www.dats.com.au/*"],
        listPagePatterns: ["https://www.dats.com.au/*"],
        cardSelector: ".product[data-product-code]",
        itemNumber: {
          source: "attribute",
          selector: null,
          attribute: "data-product-code",
          transforms: ["trim", "uppercase"]
        },
        mountSelector: ".widget-productlist-code",
        mountPosition: "afterend",
        // 供应商分类采集（1.5.0+）：离线回退时使用；联网后以后端下发的 category 块为准。
        // 以下选择器已于 2026-09-23 对公开页 /、/office-stationery、/office-stationery/adhesives-and-tape 核实；
        // 登录后的页面结构待登录核实，若不同由后端配置热更新修正（递增 ConfigVersion，无需发版）。
        category: {
          enabled: true,
          passiveEnabled: true,
          crawlEnabled: true,
          categoryPagePatterns: ["https://www.dats.com.au/*"],
          categoryExcludePatterns: [],
          // 面包屑：首项 Home 只有图标，名称在 meta[itemprop=name]；末项无链接，URL 在 meta/data-url。
          breadcrumbSelector: '.widget-breadcrumb li[itemprop="itemListElement"]',
          breadcrumbSkip: 1,
          titleSelector: "h1.page-title, h1",
          keySource: "pathname",
          keyQueryParams: [],
          navRootUrl: "https://www.dats.com.au/",
          // 顶部 mega menu（标题链接 + 兄弟 ul）与分类页侧栏分类树（li 嵌套）两个候选，按 key 去重合并。
          navSelector: ".widget-navigation-menu .dropdown-area a[href], .widget-product-category-list a.box-title",
          // 分类页侧栏会列出整棵树；采集器只接受当前分类路径下的子链接，其余忽略。
          subcategoryLinkSelector: ".widget-product-category-list a.box-title",
          // DATS 分页写在 <head> 的 link[rel=next]（?PageProduct=2&PageSizeProduct=24）。
          paginationNextSelector: 'link[rel="next"], a[rel="next"]',
          maxPages: 20,
          maxDepth: 4,
          maxCategories: 400,
          crawlDelayMs: 1500,
          promotionalPatterns: []
        }
      }
    ]
  };

  // src/lib/profile-cache.js
  var LEGACY_DATS_CODE = "DATS";
  var DATS_BUSINESS_CODE = "240";
  var DATS_ORIGIN = "https://www.dats.com.au/*";
  function migrateProfileConfig(raw) {
    if (!raw || typeof raw !== "object" || !Array.isArray(raw.profiles)) return raw;
    let changed = false;
    const profiles = raw.profiles.map((profile) => {
      const isLegacyDats = profile && profile.supplierCode === LEGACY_DATS_CODE && Array.isArray(profile.origins) && profile.origins.includes(DATS_ORIGIN);
      if (!isLegacyDats) return profile;
      changed = true;
      return { ...profile, supplierCode: DATS_BUSINESS_CODE };
    });
    return changed ? { ...raw, profiles } : raw;
  }

  // src/lib/transforms.js
  var ALLOWED_TRANSFORMS = /* @__PURE__ */ new Set([
    "trim",
    "uppercase",
    "lowercase",
    "after-colon",
    "underscore-to-slash",
    "after-sku"
  ]);
  function isTransformAllowed(type) {
    return ALLOWED_TRANSFORMS.has(type);
  }
  function normalizeTransform(transform) {
    return typeof transform === "string" ? { type: transform } : transform;
  }
  function safeTransformList(transforms) {
    if (transforms == null) return true;
    if (!Array.isArray(transforms)) return false;
    return transforms.every((transform) => {
      const normalized = normalizeTransform(transform);
      return !!normalized && isTransformAllowed(normalized.type);
    });
  }

  // src/lib/category-path.js
  var MAX_CATEGORY_KEY_LENGTH = 300;
  var MAX_CATEGORY_NAME_LENGTH = 200;
  var MAX_CATEGORY_URL_LENGTH = 1e3;
  var MAX_CATEGORY_PATH_DEPTH = 8;
  var MAX_KEY_QUERY_PARAMS = 5;
  var KEY_QUERY_PARAM_PATTERN = /^[A-Za-z0-9_\-[\]]{1,50}$/u;
  var BUILTIN_CATEGORY_EXCLUDE_PATTERNS = Object.freeze([
    "/search*",
    "/cart*",
    "/checkout*",
    "/account*",
    "/login*",
    "/my-account*",
    "/wishlist*"
  ]);
  var DEFAULT_PROMOTIONAL_PATTERNS = Object.freeze([
    "clearance*",
    "*-clearance",
    "sale",
    "sale-*",
    "on-sale*",
    "specials*",
    "special-offers*",
    "new-arrivals*",
    "new-in*",
    "whats-new*",
    "new-products*",
    "best-sellers*",
    "bestsellers*",
    "shop-by-*",
    "gift-ideas*",
    "trending*",
    "promotions*",
    "deals*"
  ]);
  var TRAILING_COUNT = /\s*\(\s*\d+\s*\)$/u;
  function normalizeKeyQueryParams(params) {
    const names = /* @__PURE__ */ new Map();
    for (const name of Array.isArray(params) ? params : []) {
      if (typeof name !== "string" || !KEY_QUERY_PARAM_PATTERN.test(name)) continue;
      const lower = name.toLowerCase();
      if (!names.has(lower)) names.set(lower, lower);
    }
    return [...names.values()].sort();
  }
  function normalizeCategoryName(name) {
    if (name == null) return "";
    let value = String(name).replace(/\s+/gu, " ").trim();
    value = value.replace(TRAILING_COUNT, "").trim();
    if (value.length > MAX_CATEGORY_NAME_LENGTH) value = value.slice(0, MAX_CATEGORY_NAME_LENGTH).trim();
    return value;
  }

  // src/lib/profiles.js
  var ALLOWED_SOURCES = /* @__PURE__ */ new Set(["attribute", "text"]);
  var ALLOWED_MOUNT_POSITIONS = /* @__PURE__ */ new Set(["beforebegin", "afterbegin", "beforeend", "afterend"]);
  var TXK_HTTP_PATTERN = /^http:\/\/txkorders\.inzantsales\.com(?<path>\/[^\s]*)$/i;
  function isSafeMatchPattern(value, originOnly = false) {
    if (typeof value !== "string" || value.length === 0 || value.length > 300) return false;
    const match = /^https:\/\/(?:\*\.)?[A-Za-z0-9.-]+(?::\d+)?(?<path>\/[^\s]*)$/.exec(value) || TXK_HTTP_PATTERN.exec(value);
    return !!match && (!originOnly || match.groups.path === "/*");
  }
  function isSafePagePattern(value) {
    return typeof value === "string" && value.startsWith("/") && value.length <= 300 || isSafeMatchPattern(value);
  }
  function originMatchesAny(origins, origin) {
    return (origins || []).some((pattern) => matchUrlPattern(pattern, `${origin}/`));
  }
  function escapeRegex(value) {
    return value.replace(/[|\\{}()[\]^$+?.]/g, "\\$&");
  }
  function matchUrlPattern(pattern, href) {
    if (typeof pattern !== "string" || !pattern || typeof href !== "string") return false;
    let target = href;
    let candidate = pattern;
    if (candidate.startsWith("/")) {
      try {
        target = new URL(href).pathname;
      } catch {
        return false;
      }
    }
    const regex = `^${escapeRegex(candidate).replaceAll("*", ".*")}$`;
    return new RegExp(regex, "i").test(target);
  }
  var NON_CAPTURABLE_SUPPLIER_CODES = /* @__PURE__ */ new Set(["200"]);
  var CATEGORY_NUMBER_RULES = {
    breadcrumbSkip: { fallback: 1, min: 0, max: 5 },
    maxPages: { fallback: 20, min: 1, max: 50 },
    maxDepth: { fallback: 4, min: 1, max: 6 },
    maxCategories: { fallback: 400, min: 10, max: 2e3 },
    crawlDelayMs: { fallback: 1500, min: 500, max: 15e3 }
  };
  var CATEGORY_SELECTOR_DEFAULTS = {
    breadcrumbSelector: null,
    titleSelector: "h1",
    navSelector: null,
    subcategoryLinkSelector: null,
    paginationNextSelector: 'a[rel="next"]'
  };
  var MAX_CATEGORY_SELECTOR_LENGTH = 500;
  var MAX_PROMOTIONAL_PATTERNS = 50;
  var MAX_PROMOTIONAL_PATTERN_LENGTH = 100;
  function uniqueStrings(values) {
    return [...new Set(values)];
  }
  function normalizeCategorySelector(value, fallback, name, errors) {
    if (value == null || value === "") return fallback;
    if (typeof value !== "string" || value.length > MAX_CATEGORY_SELECTOR_LENGTH || /[\r\n]/u.test(value)) {
      errors.push(`${name} \u975E\u6CD5`);
      return fallback;
    }
    const trimmed = value.trim();
    return trimmed || fallback;
  }
  function normalizeCategoryNumber(value, name, errors) {
    const rule = CATEGORY_NUMBER_RULES[name];
    if (value == null) return rule.fallback;
    if (!Number.isInteger(value) || value < rule.min || value > rule.max) {
      errors.push(`${name} \u8D85\u51FA\u8303\u56F4 ${rule.min}..${rule.max}`);
      return rule.fallback;
    }
    return value;
  }
  function normalizeCategoryBoolean(value, fallback, name, errors) {
    if (value == null) return fallback;
    if (typeof value !== "boolean") {
      errors.push(`${name} \u5FC5\u987B\u4E3A boolean`);
      return fallback;
    }
    return value;
  }
  function normalizePagePatternList(value, name, errors) {
    if (value == null) return [];
    if (!Array.isArray(value)) {
      errors.push(`${name} \u5FC5\u987B\u4E3A\u6570\u7EC4`);
      return [];
    }
    const out = [];
    value.forEach((pattern, index) => {
      if (!isSafePagePattern(pattern)) errors.push(`${name}[${index}] \u975E\u6CD5`);
      else out.push(pattern);
    });
    return out;
  }
  function normalizePromotionalPatterns(value, errors) {
    if (value == null) return [...DEFAULT_PROMOTIONAL_PATTERNS];
    if (!Array.isArray(value) || value.length > MAX_PROMOTIONAL_PATTERNS) {
      errors.push(`promotionalPatterns \u5FC5\u987B\u4E3A\u4E0D\u8D85\u8FC7 ${MAX_PROMOTIONAL_PATTERNS} \u9879\u7684\u6570\u7EC4`);
      return [...DEFAULT_PROMOTIONAL_PATTERNS];
    }
    const out = [];
    value.forEach((pattern, index) => {
      const trimmed = typeof pattern === "string" ? pattern.trim().toLowerCase() : "";
      if (!trimmed || trimmed.length > MAX_PROMOTIONAL_PATTERN_LENGTH || /[?\u0000-\u001f\u007f]/u.test(trimmed)) {
        errors.push(`promotionalPatterns[${index}] \u975E\u6CD5`);
        return;
      }
      out.push(trimmed);
    });
    return out.length > 0 ? uniqueStrings(out) : [...DEFAULT_PROMOTIONAL_PATTERNS];
  }
  function normalizeNavRootUrl(value, profile, errors) {
    if (value == null || value === "") return null;
    if (typeof value !== "string" || value.length > 1e3 || /\s/u.test(value)) {
      errors.push("navRootUrl \u975E\u6CD5");
      return null;
    }
    if (value.startsWith("/") && !value.startsWith("//")) return value;
    let url;
    try {
      url = new URL(value);
    } catch {
      errors.push("navRootUrl \u975E\u6CD5");
      return null;
    }
    if (!/^https?:$/u.test(url.protocol) || url.username || url.password || !originMatchesAny(profile?.origins, url.origin)) {
      errors.push("navRootUrl \u5FC5\u987B\u4E0E\u4F9B\u5E94\u5546\u540C\u6E90");
      return null;
    }
    return url.href;
  }
  function normalizeCategoryConfig(raw, profile) {
    const errors = [];
    const source = raw == null ? {} : raw;
    if (typeof source !== "object" || Array.isArray(source)) {
      errors.push("category \u5FC5\u987B\u4E3A\u5BF9\u8C61");
    }
    const input = typeof source === "object" && !Array.isArray(source) ? source : {};
    const categoryPagePatterns = normalizePagePatternList(
      input.categoryPagePatterns,
      "categoryPagePatterns",
      errors
    );
    const excludePatterns = normalizePagePatternList(
      input.categoryExcludePatterns,
      "categoryExcludePatterns",
      errors
    );
    let keySource = "pathname";
    if (input.keySource != null) {
      const normalized = typeof input.keySource === "string" ? input.keySource.toLowerCase() : "";
      if (normalized === "pathname" || normalized === "hash") keySource = normalized;
      else errors.push("keySource \u5FC5\u987B\u4E3A pathname \u6216 hash");
    }
    let keyQueryParams = [];
    if (input.keyQueryParams != null) {
      if (!Array.isArray(input.keyQueryParams) || input.keyQueryParams.length > MAX_KEY_QUERY_PARAMS) {
        errors.push(`keyQueryParams \u5FC5\u987B\u4E3A\u4E0D\u8D85\u8FC7 ${MAX_KEY_QUERY_PARAMS} \u9879\u7684\u6570\u7EC4`);
      } else {
        input.keyQueryParams.forEach((name, index) => {
          if (typeof name !== "string" || !KEY_QUERY_PARAM_PATTERN.test(name)) {
            errors.push(`keyQueryParams[${index}] \u975E\u6CD5`);
          }
        });
        keyQueryParams = normalizeKeyQueryParams(input.keyQueryParams);
      }
    }
    const config = {
      enabled: false,
      passiveEnabled: normalizeCategoryBoolean(input.passiveEnabled, true, "passiveEnabled", errors),
      crawlEnabled: normalizeCategoryBoolean(input.crawlEnabled, true, "crawlEnabled", errors),
      // 未单独配置分类页模式时沿用列表页模式。
      categoryPagePatterns: categoryPagePatterns.length > 0 ? categoryPagePatterns : [...Array.isArray(profile?.listPagePatterns) ? profile.listPagePatterns : []],
      categoryExcludePatterns: uniqueStrings([...BUILTIN_CATEGORY_EXCLUDE_PATTERNS, ...excludePatterns]),
      breadcrumbSelector: normalizeCategorySelector(
        input.breadcrumbSelector,
        CATEGORY_SELECTOR_DEFAULTS.breadcrumbSelector,
        "breadcrumbSelector",
        errors
      ),
      breadcrumbSkip: normalizeCategoryNumber(input.breadcrumbSkip, "breadcrumbSkip", errors),
      titleSelector: normalizeCategorySelector(
        input.titleSelector,
        CATEGORY_SELECTOR_DEFAULTS.titleSelector,
        "titleSelector",
        errors
      ),
      keySource,
      keyQueryParams,
      navRootUrl: normalizeNavRootUrl(input.navRootUrl, profile, errors),
      navSelector: normalizeCategorySelector(
        input.navSelector,
        CATEGORY_SELECTOR_DEFAULTS.navSelector,
        "navSelector",
        errors
      ),
      subcategoryLinkSelector: normalizeCategorySelector(
        input.subcategoryLinkSelector,
        CATEGORY_SELECTOR_DEFAULTS.subcategoryLinkSelector,
        "subcategoryLinkSelector",
        errors
      ),
      paginationNextSelector: normalizeCategorySelector(
        input.paginationNextSelector,
        CATEGORY_SELECTOR_DEFAULTS.paginationNextSelector,
        "paginationNextSelector",
        errors
      ),
      maxPages: normalizeCategoryNumber(input.maxPages, "maxPages", errors),
      maxDepth: normalizeCategoryNumber(input.maxDepth, "maxDepth", errors),
      maxCategories: normalizeCategoryNumber(input.maxCategories, "maxCategories", errors),
      crawlDelayMs: normalizeCategoryNumber(input.crawlDelayMs, "crawlDelayMs", errors),
      promotionalPatterns: normalizePromotionalPatterns(input.promotionalPatterns, errors)
    };
    if (input.enabled != null && typeof input.enabled !== "boolean") {
      errors.push("enabled \u5FC5\u987B\u4E3A boolean");
    }
    const capturable = !NON_CAPTURABLE_SUPPLIER_CODES.has(profile?.supplierCode);
    config.enabled = input.enabled === true && capturable && errors.length === 0;
    return { config, errors };
  }
  function validateProfiles(raw) {
    if (!raw || typeof raw !== "object" || !Array.isArray(raw.profiles)) {
      return {
        valid: false,
        profiles: [],
        errors: ["profiles \u5FC5\u987B\u4E3A {profiles:[...]} \u5BF9\u8C61"],
        warnings: []
      };
    }
    const errors = [];
    const warnings = [];
    const out = [];
    raw.profiles.forEach((p, i) => {
      const path = `profiles[${i}]`;
      if (!p || typeof p !== "object") {
        errors.push(`${path} \u4E0D\u662F\u5BF9\u8C61`);
        return;
      }
      const errs = [];
      if (typeof p.supplierCode !== "string" || !p.supplierCode) errs.push("supplierCode \u5FC5\u586B");
      if (typeof p.displayName !== "string" || !p.displayName) errs.push("displayName \u5FC5\u586B");
      if (typeof p.enabled !== "boolean") errs.push("enabled \u5FC5\u987B\u4E3A boolean");
      if (!Array.isArray(p.origins) || p.origins.length === 0) {
        errs.push("origins \u5FC5\u987B\u4E3A\u975E\u7A7A\u6570\u7EC4");
      } else {
        p.origins.forEach((o, j) => {
          if (!isSafeMatchPattern(o, true)) errs.push(`origins[${j}] \u975E\u6CD5`);
        });
      }
      if (!Array.isArray(p.listPagePatterns)) {
        errs.push("listPagePatterns \u5FC5\u987B\u4E3A\u6570\u7EC4");
      } else {
        p.listPagePatterns.forEach((pattern, j) => {
          if (!isSafePagePattern(pattern)) errs.push(`listPagePatterns[${j}] \u975E\u6CD5`);
        });
      }
      if (typeof p.cardSelector !== "string" || !p.cardSelector) errs.push("cardSelector \u5FC5\u586B");
      if (!p.itemNumber || typeof p.itemNumber !== "object") {
        errs.push("itemNumber \u5FC5\u586B");
      } else {
        const it = p.itemNumber;
        if (!ALLOWED_SOURCES.has(it.source)) errs.push("itemNumber.source \u975E\u6CD5");
        if (it.source === "attribute" && (typeof it.attribute !== "string" || !it.attribute)) {
          errs.push("attribute source \u9700\u8981 attribute");
        }
        if (it.selector != null && typeof it.selector !== "string") {
          errs.push("itemNumber.selector \u5FC5\u987B\u4E3A\u5B57\u7B26\u4E32\u6216 null");
        }
        if (!safeTransformList(it.transforms)) errs.push("itemNumber.transforms \u5305\u542B\u4E0D\u652F\u6301\u7684 transform");
      }
      if (typeof p.mountSelector !== "string" || !p.mountSelector) errs.push("mountSelector \u5FC5\u586B");
      if (!ALLOWED_MOUNT_POSITIONS.has(p.mountPosition)) errs.push("mountPosition \u975E\u6CD5");
      if (errs.length) {
        errors.push(...errs.map((e) => `${path}.${e}`));
        return;
      }
      const category = normalizeCategoryConfig(p.category, p);
      warnings.push(...category.errors.map((e) => `${path}.category.${e}`));
      out.push({ ...p, category: category.config });
    });
    return { valid: errors.length === 0, profiles: out, errors, warnings };
  }
  function matchProfile(profiles, { origin, pathname }) {
    for (const p of profiles || []) {
      if (p.enabled === false) continue;
      if (!originMatchesAny(p.origins, origin)) continue;
      return p;
    }
    return null;
  }

  // src/lib/item-number.js
  var MAX_ITEM_NUMBER_LENGTH = 50;
  function normalizeCaptureItemNumber(value) {
    if (value == null) return "";
    const normalized = String(value).trim().toUpperCase();
    if (!normalized || normalized.length > MAX_ITEM_NUMBER_LENGTH) return "";
    if (/[\u0000-\u001f\u007f]/u.test(normalized)) return "";
    return normalized;
  }
  function normalizeCaptureItemNumbers(values) {
    const seen = /* @__PURE__ */ new Set();
    const out = [];
    for (const value of values || []) {
      const normalized = normalizeCaptureItemNumber(value);
      if (!normalized || seen.has(normalized)) continue;
      seen.add(normalized);
      out.push(normalized);
    }
    return out;
  }

  // src/lib/category-capture.js
  var CAPTURE_CHUNK_SIZE = 100;
  var CAPTURE_DEDUPE_TTL_MS = 6 * 60 * 60 * 1e3;
  var CAPTURE_DEDUPE_MAX_ENTRIES = 500;
  var CAPTURE_MODES = Object.freeze(["passive", "crawl"]);
  var MAX_TREE_SNAPSHOT_NODES = 2e3;
  var MAX_RETRY_AFTER_MS = 6e4;
  var MAX_SUPPLIER_CODE_LENGTH = 50;
  function fnv1a(value) {
    let hash = 2166136261;
    const text = String(value ?? "");
    for (let index = 0; index < text.length; index += 1) {
      hash ^= text.charCodeAt(index);
      hash = Math.imul(hash, 16777619) >>> 0;
    }
    return hash.toString(16).padStart(8, "0");
  }
  function hashItemNumbers(itemNumbers) {
    const unique = [...new Set(normalizeCaptureItemNumbers(itemNumbers))].sort();
    return fnv1a(unique.join("\n"));
  }
  function buildCaptureDedupeKey({ supplierCode, categoryPath, itemNumbers }) {
    const keys = (Array.isArray(categoryPath) ? categoryPath : []).map((node) => node?.key || "");
    const items = normalizeCaptureItemNumbers(itemNumbers);
    return `${String(supplierCode || "")}|${fnv1a(keys.join(">"))}|${hashItemNumbers(items)}:${items.length}`;
  }
  function createCaptureDedupeStore({
    read,
    write,
    now = () => Date.now(),
    ttlMs = CAPTURE_DEDUPE_TTL_MS,
    maxEntries = CAPTURE_DEDUPE_MAX_ENTRIES
  }) {
    let queue = Promise.resolve();
    const serial = (task) => {
      const run = queue.then(task, task);
      queue = run.catch(() => void 0);
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
      return (Array.isArray(raw) ? raw : []).filter((entry) => Array.isArray(entry) && typeof entry[0] === "string" && Number.isFinite(entry[1]) && current - entry[1] < ttlMs && entry[1] <= current + 6e4);
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
      }
    };
  }
  function parseUrl(value) {
    if (typeof value !== "string" || !value) return null;
    try {
      return new URL(value);
    } catch {
      return null;
    }
  }
  function truncatePageUrl(value) {
    const url = parseUrl(value);
    if (!url) return typeof value === "string" ? value.slice(0, MAX_CATEGORY_URL_LENGTH) : "";
    if (url.href.length <= MAX_CATEGORY_URL_LENGTH) return url.href;
    url.hash = "";
    if (url.href.length <= MAX_CATEGORY_URL_LENGTH) return url.href;
    const base = `${url.origin}${url.pathname}`;
    return base.slice(0, MAX_CATEGORY_URL_LENGTH);
  }
  function toIsoString(value, now) {
    const date = value instanceof Date ? value : new Date(value ?? now());
    return Number.isFinite(date.getTime()) ? date.toISOString() : new Date(now()).toISOString();
  }
  function isValidKey(value) {
    return typeof value === "string" && value.startsWith("/") && value.length <= MAX_CATEGORY_KEY_LENGTH && !/[\s\u0000-\u001f\u007f]/u.test(value.split("?")[0]);
  }
  function sanitizeName(value) {
    if (typeof value !== "string") return null;
    const name = value.replace(/\s+/gu, " ").trim();
    return name && name.length <= MAX_CATEGORY_NAME_LENGTH ? name : null;
  }
  function sanitizeSameOriginUrl(value, origin) {
    if (value == null || value === "") return { ok: true, url: null };
    const url = parseUrl(value);
    if (!url || !/^https?:$/u.test(url.protocol) || url.origin !== origin) return { ok: false };
    if (url.href.length > MAX_CATEGORY_URL_LENGTH) return { ok: true, url: null };
    return { ok: true, url: url.href };
  }
  function invalid(error) {
    return { ok: false, errorCode: "INVALID_CAPTURE", error };
  }
  function checkSupplier(supplierCode, expectedSupplierCode) {
    if (typeof supplierCode !== "string" || !supplierCode || supplierCode.length > MAX_SUPPLIER_CODE_LENGTH) {
      return "supplierCode \u975E\u6CD5";
    }
    if (expectedSupplierCode != null && supplierCode !== expectedSupplierCode) return "supplierCode \u4E0E\u6765\u6E90\u9875\u9762\u4E0D\u4E00\u81F4";
    return null;
  }
  function validateCapturePayload(payload, { senderOrigin, expectedSupplierCode, now = () => Date.now() } = {}) {
    if (!payload || typeof payload !== "object") return invalid("\u8F7D\u8377\u7F3A\u5931");
    const supplierError = checkSupplier(payload.supplierCode, expectedSupplierCode);
    if (supplierError) return invalid(supplierError);
    if (!CAPTURE_MODES.includes(payload.mode)) return invalid("mode \u975E\u6CD5");
    const page = parseUrl(payload.pageUrl);
    if (!page || !/^https?:$/u.test(page.protocol) || page.origin !== senderOrigin) {
      return invalid("pageUrl \u5FC5\u987B\u4E0E\u6765\u6E90\u9875\u9762\u540C\u6E90");
    }
    const path = payload.categoryPath;
    if (!Array.isArray(path) || path.length < 1 || path.length > MAX_CATEGORY_PATH_DEPTH) {
      return invalid(`categoryPath \u5FC5\u987B\u4E3A 1..${MAX_CATEGORY_PATH_DEPTH} \u4E2A\u8282\u70B9`);
    }
    const categoryPath = [];
    for (const node of path) {
      const name = sanitizeName(node?.name);
      if (!name || !isValidKey(node?.key)) return invalid("categoryPath \u8282\u70B9\u975E\u6CD5");
      const url = sanitizeSameOriginUrl(node.url, senderOrigin);
      if (!url.ok) return invalid("categoryPath \u8282\u70B9 URL \u5FC5\u987B\u4E0E\u6765\u6E90\u9875\u9762\u540C\u6E90");
      categoryPath.push({ name, key: node.key, url: url.url });
    }
    if (!Array.isArray(payload.itemNumbers)) return invalid("itemNumbers \u5FC5\u987B\u4E3A\u6570\u7EC4");
    const itemNumbers = normalizeCaptureItemNumbers(payload.itemNumbers);
    if (itemNumbers.length < 1 || itemNumbers.length > CAPTURE_CHUNK_SIZE || payload.itemNumbers.length > CAPTURE_CHUNK_SIZE) {
      return invalid(`itemNumbers \u5FC5\u987B\u4E3A 1..${CAPTURE_CHUNK_SIZE} \u4E2A\u6709\u6548\u8D27\u53F7`);
    }
    const pageNumber = Number.isInteger(payload.pageNumber) && payload.pageNumber >= 1 && payload.pageNumber <= 1e4 ? payload.pageNumber : void 0;
    return {
      ok: true,
      payload: {
        supplierCode: payload.supplierCode,
        pageUrl: truncatePageUrl(page.href),
        categoryPath,
        itemNumbers,
        capturedAt: toIsoString(payload.capturedAt, now),
        mode: payload.mode,
        ...pageNumber ? { pageNumber } : {}
      }
    };
  }
  function validateTreeSnapshotPayload(payload, { senderOrigin, expectedSupplierCode } = {}) {
    if (!payload || typeof payload !== "object") return invalid("\u8F7D\u8377\u7F3A\u5931");
    const supplierError = checkSupplier(payload.supplierCode, expectedSupplierCode);
    if (supplierError) return invalid(supplierError);
    const source = sanitizeSameOriginUrl(payload.sourceUrl, senderOrigin);
    if (!source.ok || !payload.sourceUrl) return invalid("sourceUrl \u5FC5\u987B\u4E0E\u6765\u6E90\u9875\u9762\u540C\u6E90");
    const sourceUrl = source.url || truncatePageUrl(payload.sourceUrl);
    if (!Array.isArray(payload.nodes) || payload.nodes.length < 1 || payload.nodes.length > MAX_TREE_SNAPSHOT_NODES) {
      return invalid(`nodes \u5FC5\u987B\u4E3A 1..${MAX_TREE_SNAPSHOT_NODES} \u4E2A\u8282\u70B9`);
    }
    const nodes = [];
    const keys = /* @__PURE__ */ new Set();
    for (const node of payload.nodes) {
      const name = sanitizeName(node?.name);
      if (!name || !isValidKey(node?.key) || keys.has(node.key)) return invalid("nodes \u8282\u70B9\u975E\u6CD5\u6216\u91CD\u590D");
      if (node.parentKey != null && (!isValidKey(node.parentKey) || node.parentKey === node.key)) {
        return invalid("nodes.parentKey \u975E\u6CD5");
      }
      const url = sanitizeSameOriginUrl(node.url, senderOrigin);
      if (!url.ok) return invalid("nodes \u8282\u70B9 URL \u5FC5\u987B\u4E0E\u6765\u6E90\u9875\u9762\u540C\u6E90");
      keys.add(node.key);
      nodes.push({
        key: node.key,
        name,
        parentKey: node.parentKey ?? null,
        url: url.url,
        sortOrder: Number.isInteger(node.sortOrder) ? node.sortOrder : null
      });
    }
    return { ok: true, payload: { supplierCode: payload.supplierCode, sourceUrl, nodes } };
  }
  function toCount(value) {
    const number = Number(value);
    return Number.isFinite(number) && number >= 0 ? Math.trunc(number) : 0;
  }
  function normalizeCaptureResponse(data) {
    const source = data && typeof data === "object" ? data : {};
    return {
      categoryGuid: typeof source.categoryGuid === "string" ? source.categoryGuid : "",
      fullPath: typeof source.fullPath === "string" ? source.fullPath : "",
      depth: toCount(source.depth),
      isPromotional: source.isPromotional === true,
      categoriesCreated: toCount(source.categoriesCreated),
      matchedProducts: toCount(source.matchedProducts),
      assignedProducts: toCount(source.assignedProducts),
      unchangedProducts: toCount(source.unchangedProducts),
      skippedManual: toCount(source.skippedManual),
      unmatchedItemNumberCount: toCount(source.unmatchedItemNumberCount),
      unmatchedSamples: Array.isArray(source.unmatchedSamples) ? source.unmatchedSamples.filter((item) => typeof item === "string").slice(0, 10) : []
    };
  }
  function normalizeTreeSnapshotResponse(data) {
    const source = data && typeof data === "object" ? data : {};
    return {
      created: toCount(source.created),
      updated: toCount(source.updated),
      unchanged: toCount(source.unchanged),
      orphanCount: toCount(source.orphanCount),
      promotionalCount: toCount(source.promotionalCount)
    };
  }
  function parseRetryAfter(value, now = () => Date.now()) {
    if (value == null || value === "") return null;
    const text = String(value).trim();
    if (/^\d+(?:\.\d+)?$/u.test(text)) return Math.min(Number(text) * 1e3, MAX_RETRY_AFTER_MS);
    const date = Date.parse(text);
    if (!Number.isFinite(date)) return null;
    return Math.min(Math.max(0, date - now()), MAX_RETRY_AFTER_MS);
  }
  function createSlidingWindowLimiter({ limit = 100, windowMs = 6e4, now = () => Date.now() } = {}) {
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
      }
    };
  }

  // src/lib/category-crawl.js
  var CRAWL_STATUSES = Object.freeze({
    RUNNING: "running",
    COMPLETED: "completed",
    ABORTED: "aborted",
    INTERRUPTED: "interrupted",
    FAILED: "failed"
  });
  var TERMINAL_CRAWL_STATUSES = /* @__PURE__ */ new Set(["completed", "aborted", "interrupted", "failed"]);
  var CRAWL_ERROR_CODES = Object.freeze([
    "SUPPLIER_TAB_REQUIRED",
    "CONTENT_SCRIPT_UNAVAILABLE",
    "CRAWL_ALREADY_RUNNING",
    "CRAWL_DISABLED",
    "LOGIN_REQUIRED",
    "SITE_BLOCKING",
    "NAV_NOT_FOUND",
    "FEATURE_DISABLED",
    "WEBSITE_SESSION_REQUIRED"
  ]);
  var MAX_TRACKED_CRAWL_KEYS = 2e3;
  var STALE_RUNNING_JOB_MS = 5 * 60 * 1e3;
  function isValidCrawlKey(value) {
    return typeof value === "string" && value.startsWith("/") && value.length <= MAX_CATEGORY_KEY_LENGTH;
  }
  function sanitizeKeys(values, limit = MAX_TRACKED_CRAWL_KEYS) {
    const out = [];
    const seen = /* @__PURE__ */ new Set();
    for (const value of Array.isArray(values) ? values : []) {
      if (!isValidCrawlKey(value) || seen.has(value)) continue;
      seen.add(value);
      out.push(value);
      if (out.length >= limit) break;
    }
    return out;
  }
  function sanitizeCrawlNodes(values, limit = MAX_TRACKED_CRAWL_KEYS) {
    const out = [];
    const seen = /* @__PURE__ */ new Set();
    for (const node of Array.isArray(values) ? values : []) {
      if (!node || !isValidCrawlKey(node.key) || seen.has(node.key)) continue;
      const name = normalizeCategoryName(node.name);
      if (!name) continue;
      seen.add(node.key);
      out.push({
        key: node.key,
        name,
        url: typeof node.url === "string" && node.url.length <= MAX_CATEGORY_URL_LENGTH ? node.url : null,
        parentKey: isValidCrawlKey(node.parentKey) ? node.parentKey : null,
        depth: Number.isInteger(node.depth) && node.depth >= 0 ? node.depth : 0
      });
      if (out.length >= limit) break;
    }
    return out;
  }
  function toCount2(value) {
    const number = Number(value);
    return Number.isFinite(number) && number >= 0 ? Math.trunc(number) : 0;
  }
  function isTerminalCrawlStatus(status) {
    return TERMINAL_CRAWL_STATUSES.has(status);
  }
  function createCrawlJob({
    jobId,
    supplierCode,
    tabId,
    origin,
    mode = "full",
    completedKeys = [],
    now = () => Date.now()
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
      errorCode: null
    };
  }
  function mergeCrawlProgress(job, progress, { now = () => Date.now() } = {}) {
    if (!job || !progress || progress.jobId !== job.jobId) return job;
    const timestamp = new Date(now()).toISOString();
    const next = { ...job, updatedAt: timestamp };
    for (const field of ["total", "done", "failed", "pages", "itemsSent"]) {
      if (progress[field] != null) next[field] = toCount2(progress[field]);
    }
    if (Array.isArray(progress.completedKeys)) {
      next.completedKeys = sanitizeKeys([...job.completedKeys || [], ...progress.completedKeys]);
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
        next.errorCode = typeof progress.errorCode === "string" ? progress.errorCode : null;
      }
    }
    return next;
  }
  function finalizeCrawlJob(job, status, { errorCode = null, now = () => Date.now() } = {}) {
    if (!job || isTerminalCrawlStatus(job.status)) return job;
    const timestamp = new Date(now()).toISOString();
    return {
      ...job,
      status,
      errorCode,
      current: null,
      updatedAt: timestamp,
      finishedAt: timestamp
    };
  }
  function isCrawlJobStale(job, now = () => Date.now()) {
    if (!job || job.status !== CRAWL_STATUSES.RUNNING) return false;
    const updatedAt = Date.parse(job.updatedAt || "");
    return !Number.isFinite(updatedAt) || now() - updatedAt > STALE_RUNNING_JOB_MS;
  }
  function toCrawlHistoryEntry(job) {
    return {
      jobId: job.jobId,
      supplierCode: job.supplierCode,
      mode: job.mode,
      status: job.status,
      startedAt: job.startedAt,
      finishedAt: job.finishedAt,
      total: toCount2(job.total),
      done: toCount2(job.done),
      failed: toCount2(job.failed),
      pages: toCount2(job.pages),
      itemsSent: toCount2(job.itemsSent),
      errorCode: job.errorCode || null,
      completedKeys: sanitizeKeys(job.completedKeys),
      failedNodes: sanitizeCrawlNodes(job.failedNodes)
    };
  }

  // src/lib/assistant-panel.js
  var ASSISTANT_PANEL_SOURCE_TAB_KEY = "assistantPanelSourceTabId";
  var PANEL_PATH = "sidepanel/sidepanel.html";
  function createAssistantPanelController({ browserApi, buildTarget }) {
    const isSafari = buildTarget === "safari";
    const panelUrl = browserApi.runtime.getURL(PANEL_PATH);
    const extensionRoot = browserApi.runtime.getURL("");
    let safariPagePromise = null;
    async function rememberSourceTab(tab) {
      if (!isSafari || !Number.isInteger(tab?.id)) return;
      if (typeof tab.url === "string" && tab.url.startsWith(extensionRoot)) return;
      await browserApi.storage.session.set({ [ASSISTANT_PANEL_SOURCE_TAB_KEY]: tab.id });
    }
    async function focusOrCreateSafariTab() {
      const tabs = await browserApi.tabs.query({});
      const existing = tabs.find((tab) => tab.url === panelUrl);
      if (Number.isInteger(existing?.id)) {
        await browserApi.tabs.update(existing.id, { active: true });
        return;
      }
      await browserApi.tabs.create({
        url: panelUrl,
        active: true
      });
    }
    async function openSafariPage({ tabId } = {}) {
      if (Number.isInteger(tabId)) {
        try {
          await rememberSourceTab(await browserApi.tabs.get(tabId));
        } catch {
        }
      }
      if (!safariPagePromise) {
        safariPagePromise = (async () => {
          try {
            await browserApi.runtime.openOptionsPage();
          } catch {
            await focusOrCreateSafariTab();
          }
        })().finally(() => {
          safariPagePromise = null;
        });
      }
      return safariPagePromise;
    }
    async function queryActiveTabs() {
      if (isSafari) {
        const stored = await browserApi.storage.session.get(ASSISTANT_PANEL_SOURCE_TAB_KEY);
        const tabId = stored[ASSISTANT_PANEL_SOURCE_TAB_KEY];
        if (Number.isInteger(tabId)) {
          try {
            const tab = await browserApi.tabs.get(tabId);
            if (!(typeof tab.url === "string" && tab.url.startsWith(extensionRoot))) {
              return [tab];
            }
          } catch {
          }
          await browserApi.storage.session.remove(ASSISTANT_PANEL_SOURCE_TAB_KEY);
        }
      }
      return browserApi.tabs.query({ active: true, lastFocusedWindow: true });
    }
    function registerListeners() {
      if (!isSafari) return;
      browserApi.action.onClicked.addListener((tab) => openSafariPage({ tabId: tab?.id }).catch(() => void 0));
      browserApi.tabs.onActivated.addListener(({ tabId }) => browserApi.tabs.get(tabId).then(rememberSourceTab).catch(() => void 0));
    }
    return {
      rememberSourceTab,
      queryActiveTabs,
      registerListeners,
      configureAction() {
        return isSafari ? Promise.resolve() : browserApi.sidePanel.setPanelBehavior({ openPanelOnActionClick: true });
      },
      open(options) {
        return isSafari ? openSafariPage(options) : browserApi.sidePanel.open(options);
      }
    };
  }

  // src/lib/ranking.js
  var DEFAULT_RANKING_PAGE_SIZE = 50;
  var RANKING_PAGE_SIZES = /* @__PURE__ */ new Set([50, 100, 200]);
  function normalizeRankingDays(value) {
    return Number(value) === 90 ? 90 : 60;
  }
  function normalizeRankingPageSize(value) {
    const numericValue = Number(value);
    return RANKING_PAGE_SIZES.has(numericValue) ? numericValue : DEFAULT_RANKING_PAGE_SIZE;
  }
  function normalizeTopSalesRequest({ topPercent, page, pageSize } = {}) {
    const providedCount = [topPercent, page, pageSize].filter((value) => value != null).length;
    if (providedCount === 0) return null;
    const numericTopPercent = Number(topPercent);
    const numericPage = Number(page);
    const numericPageSize = Number(pageSize);
    if (providedCount !== 3 || numericTopPercent !== 30 || !Number.isInteger(numericPage) || numericPage < 1 || normalizeRankingPageSize(numericPageSize) !== numericPageSize) {
      throw new Error("\u65E0\u6548\u7684\u70ED\u9500\u699C\u5206\u9875\u53C2\u6570");
    }
    return {
      topPercent: numericTopPercent,
      page: numericPage,
      pageSize: numericPageSize
    };
  }

  // src/lib/session-handoff.js
  var WEBSITE_SESSION_CLIENT_ID = "hb-supplier-order";
  function parseOrigin(value) {
    try {
      const url = new URL(value);
      if (url.username || url.password) return null;
      return url.origin;
    } catch {
      return null;
    }
  }
  function validateWebsiteSessionContext({
    pageUrl,
    webOrigin,
    apiOrigin,
    isTopLevel
  }) {
    if (!isTopLevel) return { ok: false, reason: "NOT_TOP_LEVEL" };
    let page;
    try {
      page = new URL(pageUrl);
    } catch {
      return { ok: false, reason: "INVALID_PAGE_URL" };
    }
    const trustedWebOrigin = parseOrigin(webOrigin);
    const trustedApiOrigin = parseOrigin(apiOrigin);
    if (!trustedWebOrigin || page.origin !== trustedWebOrigin) {
      return { ok: false, reason: "UNTRUSTED_PAGE" };
    }
    if (page.pathname !== "/shop") return { ok: false, reason: "NOT_SHOP_PAGE" };
    if (!trustedApiOrigin || trustedApiOrigin !== page.origin) {
      return { ok: false, reason: "API_ORIGIN_MISMATCH" };
    }
    return { ok: true, origin: page.origin };
  }
  function failure(body, fallbackReason) {
    return {
      ok: false,
      reason: body?.errorCode || fallbackReason,
      error: body?.message || fallbackReason
    };
  }
  function parseTokenResponse({ httpOk, body, nowMs = Date.now() }) {
    if (!httpOk || body?.success !== true) return failure(body, "TOKEN_EXCHANGE_FAILED");
    const data = body.data;
    const expiryMs = Date.parse(data?.accessTokenExpiry || "");
    if (!data || typeof data.accessToken !== "string" || !data.accessToken || typeof data.accessTokenExpiry !== "string" || !Number.isFinite(expiryMs) || expiryMs <= nowMs + 5e3 || data.refreshToken != null || typeof data.userGuid !== "string" || !data.userGuid.trim() || !(typeof data.username === "string" && data.username.trim() || typeof data.fullName === "string" && data.fullName.trim())) {
      return failure(body, "INVALID_TOKEN_RESPONSE");
    }
    const user = {
      userGuid: data.userGuid.trim(),
      ...typeof data.username === "string" && data.username.trim() ? { username: data.username.trim() } : {},
      ...typeof data.fullName === "string" && data.fullName.trim() ? { fullName: data.fullName.trim() } : {}
    };
    return {
      ok: true,
      accessToken: data.accessToken,
      accessTokenExpiry: data.accessTokenExpiry,
      user
    };
  }
  function createSingleFlight(task) {
    let pending = null;
    return (...args) => {
      if (pending) return pending;
      let result;
      try {
        result = task(...args);
      } catch (error) {
        result = Promise.reject(error);
      }
      const current = Promise.resolve(result);
      const wrapped = current.finally(() => {
        if (pending === wrapped) pending = null;
      });
      pending = wrapped;
      return wrapped;
    };
  }
  function createAccessRequestExecutor({ isAuthFailure: isAuthFailure2, clearAccessSession: clearAccessSession2 }) {
    return async (request) => {
      const response = await request();
      if (isAuthFailure2(response)) await clearAccessSession2();
      return response;
    };
  }

  // hb-safari-config:config.js
  var EXTENSION_VERSION = "1.5.0";
  var HB_API_ORIGIN = "https://hotbargain.vip";
  var HB_WEB_ORIGIN = "https://hotbargain.vip";
  var BUILD_TARGET = "safari";
  var API_BASE = HB_API_ORIGIN;

  // src/background/service-worker.js
  var ACCESS_KEY = "websiteAccessToken";
  var ACCESS_EXPIRY_KEY = "websiteAccessTokenExpiry";
  var USER_KEY = "websiteSessionUser";
  var PENDING_HANDOFF_KEY = "pendingWebsiteSessionHandoff";
  var LEGACY_ACCESS_KEY = "accessToken";
  var LEGACY_REFRESH_KEY = "refreshToken";
  var PROFILES_KEY = "supplierProfiles";
  var GRANTED_KEY = "grantedOrigins";
  var API_ORIGIN_KEY = "apiOrigin";
  var CATEGORY_JOB_KEY = "categoryCrawlJob";
  var CATEGORY_HISTORY_KEY = "categoryCrawlHistory";
  var CATEGORY_DEDUPE_KEY = "categoryCaptureDedupe";
  var CATEGORY_CAPTURES_PATH = "/api/react/v1/browser-extension/supplier-categories/captures";
  var CATEGORY_TREE_SNAPSHOT_PATH = "/api/react/v1/browser-extension/supplier-categories/tree-snapshot";
  var assistantPanel = createAssistantPanelController({ browserApi: chrome, buildTarget: BUILD_TARGET });
  assistantPanel.registerListeners();
  var getSession = (keys) => chrome.storage.session.get(keys);
  var setSession = (obj) => chrome.storage.session.set(obj);
  var removeSession = (keys) => chrome.storage.session.remove(keys);
  var getLocal = (keys) => chrome.storage.local.get(keys);
  var setLocal = (obj) => chrome.storage.local.set(obj);
  var removeLocal = (keys) => chrome.storage.local.remove(keys);
  async function getAccessToken() {
    const stored = await getSession([ACCESS_KEY, ACCESS_EXPIRY_KEY]);
    const token = stored[ACCESS_KEY];
    const expiry = Date.parse(stored[ACCESS_EXPIRY_KEY] || "");
    if (!token || !Number.isFinite(expiry) || expiry <= Date.now() + 5e3) {
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
    if (!value || typeof value !== "object" || typeof value.userGuid !== "string" || !value.userGuid.trim() || !(typeof value.username === "string" && value.username.trim() || typeof value.fullName === "string" && value.fullName.trim())) {
      return null;
    }
    return {
      userGuid: value.userGuid.trim(),
      ...typeof value.username === "string" && value.username.trim() ? { username: value.username.trim() } : {},
      ...typeof value.fullName === "string" && value.fullName.trim() ? { fullName: value.fullName.trim() } : {}
    };
  }
  async function clearLegacyCredentials() {
    await Promise.all([
      removeSession([LEGACY_ACCESS_KEY]),
      removeLocal([LEGACY_REFRESH_KEY])
    ]);
  }
  async function getApiOrigin() {
    const stored = await getLocal(API_ORIGIN_KEY);
    return resolveApiOrigin(stored[API_ORIGIN_KEY], API_BASE);
  }
  async function rawFetch(path, options = {}, { anonymous = false } = {}) {
    const [accessToken, apiOrigin] = await Promise.all([
      anonymous ? null : getAccessToken(),
      getApiOrigin()
    ]);
    const headers = {
      "X-HB-Extension-Version": EXTENSION_VERSION,
      ...options.body ? { "Content-Type": "application/json" } : {},
      ...options.headers || {}
    };
    if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
    const res = await fetch(`${apiOrigin}${path}`, {
      ...options,
      credentials: "omit",
      headers
    });
    let body = null;
    try {
      body = await res.json();
    } catch {
    }
    return {
      httpStatus: res.status,
      ok: res.ok,
      success: body && body.success,
      data: body && body.data,
      message: body && body.message,
      errorCode: body && body.errorCode,
      // 429/503 的退避提示，分类采集按它延迟重试。
      retryAfter: res.headers?.get?.("Retry-After") ?? null
    };
  }
  async function handleGetApiOrigin() {
    return {
      ok: true,
      apiOrigin: await getApiOrigin(),
      defaultApiOrigin: API_BASE,
      localApiOrigin: LOCAL_API_ORIGIN
    };
  }
  async function handleSetApiOrigin({ apiOrigin }) {
    const normalized = normalizeApiOrigin(apiOrigin, API_BASE);
    if (!normalized) return { ok: false, error: "\u63A5\u53E3\u5730\u5740\u65E0\u6548" };
    const pattern = toApiHostPattern(normalized);
    if (!pattern || !await chrome.permissions.contains({ origins: [pattern] })) {
      return { ok: false, error: "\u63A5\u53E3\u5730\u5740\u5C1A\u672A\u83B7\u5F97\u6D4F\u89C8\u5668\u6388\u6743" };
    }
    const current = await getApiOrigin();
    if (normalized === current) {
      return { ok: true, apiOrigin: normalized, changed: false, requiresWebsiteSession: false };
    }
    await setLocal({ [API_ORIGIN_KEY]: normalized, [PROFILES_KEY]: DEFAULT_PROFILES });
    await Promise.all([
      clearAccessSession(),
      removeSession(PENDING_HANDOFF_KEY),
      // 分类回传去重记录属于旧环境，新环境需要重新回传。
      removeLocal([CATEGORY_DEDUPE_KEY])
    ]);
    await syncContentScripts();
    return { ok: true, apiOrigin: normalized, changed: true, requiresWebsiteSession: true };
  }
  var accessRequestExecutor = createAccessRequestExecutor({
    isAuthFailure: (r) => isAuthFailure(r, r.httpStatus),
    clearAccessSession
  });
  async function apiRequest(path, options = {}) {
    if (!await getAccessToken()) {
      const handoff = await ensureWebsiteSession();
      if (!handoff.ok) {
        return {
          httpStatus: 401,
          ok: false,
          success: false,
          message: handoff.error,
          errorCode: handoff.reason || "WEBSITE_SESSION_REQUIRED"
        };
      }
    }
    return accessRequestExecutor(() => rawFetch(path, options));
  }
  function validateGrantMessage(message) {
    return message?.clientId === WEBSITE_SESSION_CLIENT_ID && typeof message.code === "string" && message.code.length >= 16 && message.code.length <= 512 && typeof message.codeVerifier === "string" && /^[A-Za-z0-9_-]{43,128}$/u.test(message.codeVerifier) && typeof message.state === "string" && /^[A-Za-z0-9_-]{32,128}$/u.test(message.state);
  }
  async function exchangeWebsiteSessionGrant(message, sender) {
    const apiOrigin = await getApiOrigin();
    const senderUrl = sender?.tab?.url || sender?.url;
    const context = validateWebsiteSessionContext({
      pageUrl: senderUrl,
      webOrigin: HB_WEB_ORIGIN,
      apiOrigin,
      isTopLevel: sender?.frameId == null || sender.frameId === 0
    });
    if (!context.ok || !validateGrantMessage(message)) {
      return {
        ok: false,
        reason: context.reason || "INVALID_WEBSITE_SESSION_GRANT",
        error: "\u7F51\u7AD9\u4F1A\u8BDD\u6388\u6743\u6765\u6E90\u65E0\u6548"
      };
    }
    const res = await rawFetch("/api/Auth/extension/token", {
      method: "POST",
      body: JSON.stringify({
        code: message.code,
        codeVerifier: message.codeVerifier,
        state: message.state,
        clientId: WEBSITE_SESSION_CLIENT_ID
      })
    }, { anonymous: true });
    const parsed = parseTokenResponse({
      httpOk: res.ok,
      body: {
        success: res.success,
        data: res.data,
        message: res.message,
        errorCode: res.errorCode
      }
    });
    if (!parsed.ok) {
      await clearAccessSession();
      return { ok: false, reason: parsed.reason, error: parsed.error };
    }
    await setSession({
      [ACCESS_KEY]: parsed.accessToken,
      [ACCESS_EXPIRY_KEY]: parsed.accessTokenExpiry,
      [USER_KEY]: parsed.user
    });
    await removeSession(PENDING_HANDOFF_KEY);
    return { ok: true, user: parsed.user, accessTokenExpiry: parsed.accessTokenExpiry };
  }
  var acceptWebsiteSessionGrant = createSingleFlight(exchangeWebsiteSessionGrant);
  async function findTrustedShopTabs() {
    const tabs = await chrome.tabs.query({ url: `${HB_WEB_ORIGIN}/shop*` });
    return tabs.filter((tab) => {
      const context = validateWebsiteSessionContext({
        pageUrl: tab.url,
        webOrigin: HB_WEB_ORIGIN,
        apiOrigin: HB_WEB_ORIGIN,
        isTopLevel: true
      });
      return tab.id != null && context.ok;
    });
  }
  async function requestWebsiteSessionFromTab() {
    const apiOrigin = await getApiOrigin();
    if (apiOrigin !== HB_WEB_ORIGIN) {
      return {
        ok: false,
        reason: "API_ORIGIN_MISMATCH",
        error: "\u5F53\u524D\u63A5\u53E3\u4E0E HB SHOP \u7F51\u9875\u4E0D\u540C\u6E90",
        loginUrl: `${HB_WEB_ORIGIN}/shop`
      };
    }
    const tabs = await findTrustedShopTabs();
    if (!tabs.length) {
      return {
        ok: false,
        reason: "WEBSITE_TAB_REQUIRED",
        error: "\u8BF7\u6253\u5F00\u6216\u767B\u5F55 HB SHOP",
        loginUrl: `${HB_WEB_ORIGIN}/shop`
      };
    }
    let lastFailure = null;
    for (const tab of tabs) {
      try {
        const result = await chrome.tabs.sendMessage(tab.id, {
          type: "REQUEST_WEBSITE_SESSION",
          apiOrigin
        });
        if (result?.ok) return result;
        lastFailure = result;
      } catch (error) {
        lastFailure = { error: String(error?.message || error) };
      }
    }
    return {
      ok: false,
      reason: lastFailure?.reason || "WEBSITE_BRIDGE_UNAVAILABLE",
      error: lastFailure?.error || "HB SHOP \u6388\u6743\u6865\u5C1A\u672A\u5C31\u7EEA",
      loginUrl: `${HB_WEB_ORIGIN}/shop`
    };
  }
  var ensureWebsiteSession = createSingleFlight(async () => {
    if (await getAccessToken()) return { ok: true };
    return requestWebsiteSessionFromTab();
  });
  async function handleCurrent() {
    if (await getAccessToken()) {
      const existingUser = await getStoredSessionUser();
      if (existingUser) return { ok: true, user: existingUser };
      await clearAccessSession();
    }
    const handoff = await ensureWebsiteSession();
    if (!handoff.ok) return handoff;
    const currentUser = handoff.user || await getStoredSessionUser();
    if (!currentUser) {
      await clearAccessSession();
      return {
        ok: false,
        reason: "INVALID_TOKEN_RESPONSE",
        error: "\u7F51\u7AD9\u4F1A\u8BDD\u8FD4\u56DE\u7684\u8D26\u53F7\u4FE1\u606F\u65E0\u6548",
        loginUrl: `${HB_WEB_ORIGIN}/shop`
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
    let config = storedValidation.valid ? {
      configVersion: storedConfig.configVersion ?? "1",
      profiles: storedValidation.profiles
    } : DEFAULT_PROFILES;
    let source = storedValidation.valid ? "cache" : "default";
    let warnings = storedValidation.valid ? storedValidation.warnings : [];
    try {
      const res = await apiRequest("/api/react/v1/browser-extension/supplier-profiles", { method: "GET" });
      if (res.success && res.data && Array.isArray(res.data.profiles)) {
        const v = validateProfiles(res.data);
        if (v.valid) {
          config = {
            configVersion: res.data.configVersion ?? "1",
            profiles: v.profiles
          };
          source = "server";
          warnings = v.warnings;
        } else {
          config = { configVersion: res.data.configVersion ?? "invalid", profiles: [] };
          source = "invalid-server";
          warnings = [];
        }
      }
    } catch {
    }
    if (source === "default") {
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
      warnings
    };
  }
  var captureDedupe = createCaptureDedupeStore({
    read: async () => (await getLocal(CATEGORY_DEDUPE_KEY))[CATEGORY_DEDUPE_KEY],
    write: (entries) => setLocal({ [CATEGORY_DEDUPE_KEY]: entries })
  });
  var captureLimiter = createSlidingWindowLimiter({ limit: 100, windowMs: 6e4 });
  var crawlJobQueue = Promise.resolve();
  function withCrawlJobLock(task) {
    const run = crawlJobQueue.then(task, task);
    crawlJobQueue = run.catch(() => void 0);
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
    const root = chrome.runtime.getURL("");
    return sender?.id === chrome.runtime.id && typeof sender.url === "string" && sender.url.startsWith(root);
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
    return { ok: false, httpStatus, errorCode, ...error ? { error } : {} };
  }
  async function resolveCategorySender(sender, supplierCode) {
    if (!sender?.tab || sender.tab.id == null || !isTopFrameSender(sender)) {
      return categoryRejection(403, "INVALID_SENDER");
    }
    const pageUrl = parseHttpUrl(sender.url || sender.tab.url);
    if (!pageUrl) return categoryRejection(403, "INVALID_SENDER");
    const profile = matchProfile(await loadValidatedProfiles(), {
      origin: pageUrl.origin,
      pathname: pageUrl.pathname
    });
    if (!profile || typeof supplierCode !== "string" || profile.supplierCode !== supplierCode) {
      return categoryRejection(403, "SUPPLIER_ORIGIN_MISMATCH");
    }
    if (NON_CAPTURABLE_SUPPLIER_CODES.has(profile.supplierCode)) {
      return categoryRejection(400, "SUPPLIER_NOT_CAPTURABLE");
    }
    if (!profile.category?.enabled) return categoryRejection(404, "CATEGORY_CAPTURE_DISABLED");
    return { ok: true, profile, origin: pageUrl.origin, tabId: sender.tab.id };
  }
  async function getCrawlJob() {
    const { [CATEGORY_JOB_KEY]: job } = await getSession(CATEGORY_JOB_KEY);
    return job && typeof job === "object" ? job : null;
  }
  async function getCrawlHistory(supplierCode) {
    const { [CATEGORY_HISTORY_KEY]: history } = await getLocal(CATEGORY_HISTORY_KEY);
    const entry = history && typeof history === "object" ? history[supplierCode] : null;
    return entry && typeof entry === "object" ? entry : null;
  }
  async function recordCrawlHistory(job) {
    const { [CATEGORY_HISTORY_KEY]: history } = await getLocal(CATEGORY_HISTORY_KEY);
    await setLocal({
      [CATEGORY_HISTORY_KEY]: {
        ...history && typeof history === "object" ? history : {},
        [job.supplierCode]: toCrawlHistoryEntry(job)
      }
    });
  }
  async function saveCrawlJob(job, { recordHistory = true } = {}) {
    await setSession({ [CATEGORY_JOB_KEY]: job });
    if (recordHistory && isTerminalCrawlStatus(job.status)) await recordCrawlHistory(job);
  }
  async function requireRunningCrawlJob(jobId, tabId) {
    const job = await getCrawlJob();
    return !!job && job.jobId === jobId && job.tabId === tabId && job.status === CRAWL_STATUSES.RUNNING;
  }
  async function postCategoryApi(path, payload) {
    try {
      return await apiRequest(path, { method: "POST", body: JSON.stringify(payload) });
    } catch (error) {
      return {
        httpStatus: 0,
        success: false,
        errorCode: "NETWORK_ERROR",
        message: String(error?.message || error)
      };
    }
  }
  function categoryApiFailure(res) {
    return {
      ok: false,
      httpStatus: res.httpStatus || 0,
      errorCode: res.errorCode || (res.httpStatus ? `HTTP_${res.httpStatus}` : "NETWORK_ERROR"),
      error: res.message || null,
      retryAfterMs: parseRetryAfter(res.retryAfter),
      ...res.httpStatus ? {} : { networkError: true }
    };
  }
  async function handleCategoryCapture(message, sender) {
    const payload = message?.payload;
    const source = await resolveCategorySender(sender, payload?.supplierCode);
    if (!source.ok) return source;
    const category = source.profile.category;
    if (payload?.mode === "passive" && !category.passiveEnabled) {
      return categoryRejection(404, "CATEGORY_CAPTURE_DISABLED");
    }
    if (payload?.mode === "crawl") {
      if (!category.crawlEnabled) return categoryRejection(404, "CATEGORY_CAPTURE_DISABLED");
      if (!await requireRunningCrawlJob(message.jobId, source.tabId)) {
        return categoryRejection(403, "CRAWL_JOB_MISMATCH");
      }
    }
    const validation = validateCapturePayload(payload, {
      senderOrigin: source.origin,
      expectedSupplierCode: source.profile.supplierCode
    });
    if (!validation.ok) return categoryRejection(400, validation.errorCode, validation.error);
    const dedupeKey = buildCaptureDedupeKey(validation.payload);
    if (await captureDedupe.has(dedupeKey)) return { ok: true, deduped: true };
    const permit = captureLimiter.tryAcquire();
    if (!permit.ok) {
      return { ok: false, httpStatus: 429, errorCode: "LOCAL_RATE_LIMITED", retryAfterMs: permit.retryAfterMs };
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
    if (!source.profile.category.crawlEnabled) return categoryRejection(404, "CATEGORY_CAPTURE_DISABLED");
    if (!await requireRunningCrawlJob(message.jobId, source.tabId)) {
      return categoryRejection(403, "CRAWL_JOB_MISMATCH");
    }
    const validation = validateTreeSnapshotPayload(payload, {
      senderOrigin: source.origin,
      expectedSupplierCode: source.profile.supplierCode
    });
    if (!validation.ok) return categoryRejection(400, validation.errorCode, validation.error);
    const permit = captureLimiter.tryAcquire();
    if (!permit.ok) {
      return { ok: false, httpStatus: 429, errorCode: "LOCAL_RATE_LIMITED", retryAfterMs: permit.retryAfterMs };
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
    if (!isExtensionPageSender(sender)) return { ok: false, errorCode: "FORBIDDEN" };
    let tab = null;
    try {
      [tab] = await assistantPanel.queryActiveTabs();
    } catch {
      tab = null;
    }
    const tabUrl = parseHttpUrl(tab?.url);
    if (tab?.id == null || !tabUrl) return { ok: false, errorCode: "SUPPLIER_TAB_REQUIRED" };
    const profile = matchProfile(await loadValidatedProfiles(), {
      origin: tabUrl.origin,
      pathname: tabUrl.pathname
    });
    if (!profile || message?.supplierCode && profile.supplierCode !== message.supplierCode) {
      return { ok: false, errorCode: "SUPPLIER_TAB_REQUIRED" };
    }
    if (NON_CAPTURABLE_SUPPLIER_CODES.has(profile.supplierCode) || !profile.category?.enabled || !profile.category.crawlEnabled) {
      return { ok: false, errorCode: "CRAWL_DISABLED" };
    }
    return withCrawlJobLock(async () => {
      let existing = await getCrawlJob();
      if (existing?.status === CRAWL_STATUSES.RUNNING) {
        if (await isTabAlive(existing.tabId) && !isCrawlJobStale(existing)) {
          return { ok: false, errorCode: "CRAWL_ALREADY_RUNNING", job: existing };
        }
        existing = finalizeCrawlJob(existing, CRAWL_STATUSES.INTERRUPTED);
        await saveCrawlJob(existing);
      }
      const mode = message?.mode === "resume" || message?.mode === "retry" ? message.mode : "full";
      const previous = await getCrawlHistory(profile.supplierCode);
      const completedKeys = mode === "full" ? [] : previous?.completedKeys || [];
      const onlyNodes = mode === "retry" ? sanitizeCrawlNodes(previous?.failedNodes) : null;
      if (mode === "retry" && onlyNodes.length === 0) return { ok: false, errorCode: "NOTHING_TO_RETRY" };
      const job = createCrawlJob({
        jobId: crypto.randomUUID(),
        supplierCode: profile.supplierCode,
        tabId: tab.id,
        origin: tabUrl.origin,
        mode,
        completedKeys
      });
      await saveCrawlJob(job);
      let response = null;
      try {
        response = await chrome.tabs.sendMessage(tab.id, {
          type: "CATEGORY_CRAWL_RUN",
          jobId: job.jobId,
          supplierCode: profile.supplierCode,
          completedKeys,
          onlyNodes
        });
      } catch {
        response = null;
      }
      if (!response?.ok) {
        const errorCode = response?.errorCode || "CONTENT_SCRIPT_UNAVAILABLE";
        const failed = finalizeCrawlJob(job, CRAWL_STATUSES.FAILED, { errorCode });
        await saveCrawlJob(failed, { recordHistory: false });
        return { ok: false, errorCode, job: failed };
      }
      return { ok: true, job };
    });
  }
  async function handleCategoryCrawlAbort(sender) {
    if (!isExtensionPageSender(sender)) return { ok: false, errorCode: "FORBIDDEN" };
    return withCrawlJobLock(async () => {
      const job = await getCrawlJob();
      if (!job || job.status !== CRAWL_STATUSES.RUNNING) return { ok: true, job };
      try {
        await chrome.tabs.sendMessage(job.tabId, { type: "CATEGORY_CRAWL_STOP", jobId: job.jobId });
      } catch {
      }
      const aborted = finalizeCrawlJob(job, CRAWL_STATUSES.ABORTED);
      await saveCrawlJob(aborted);
      return { ok: true, job: aborted };
    });
  }
  async function handleCategoryCrawlProgress(message, sender) {
    if (!sender?.tab || sender.tab.id == null || !isTopFrameSender(sender)) {
      return { ok: false, errorCode: "INVALID_SENDER" };
    }
    return withCrawlJobLock(async () => {
      const job = await getCrawlJob();
      if (!job || job.jobId !== message?.jobId || job.tabId !== sender.tab.id) {
        return { ok: false, errorCode: "CRAWL_JOB_MISMATCH" };
      }
      const next = mergeCrawlProgress(job, message.progress);
      await saveCrawlJob(next);
      return { ok: true, status: next.status };
    });
  }
  function interruptCrawlForTab(tabId) {
    return withCrawlJobLock(async () => {
      const job = await getCrawlJob();
      if (!job || job.tabId !== tabId || job.status !== CRAWL_STATUSES.RUNNING) return;
      await saveCrawlJob(finalizeCrawlJob(job, CRAWL_STATUSES.INTERRUPTED));
    }).catch(() => void 0);
  }
  chrome.tabs.onRemoved.addListener((tabId) => {
    void interruptCrawlForTab(tabId);
  });
  chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
    if (changeInfo?.status === "loading") void interruptCrawlForTab(tabId);
  });
  async function migrateStoredProfiles() {
    const { [PROFILES_KEY]: storedConfig } = await getLocal(PROFILES_KEY);
    const migrated = migrateProfileConfig(storedConfig);
    if (migrated !== storedConfig) await setLocal({ [PROFILES_KEY]: migrated });
    return migrated;
  }
  async function handleRelease() {
    const res = await apiRequest("/api/react/v1/browser-extension/release", { method: "GET" });
    if (!res.success) return { ok: false, error: res.message || res.errorCode || "\u83B7\u53D6\u7248\u672C\u5931\u8D25" };
    return { ok: true, release: res.data };
  }
  async function handleSummaryBatch({ storeCode, supplierCode, itemNumbers, salesRankingDays }) {
    if (!storeCode || !supplierCode || !Array.isArray(itemNumbers)) {
      return { ok: false, error: "\u53C2\u6570\u7F3A\u5931" };
    }
    const res = await apiRequest("/api/react/v1/browser-extension/product-purchase-cycle-summary/batch", {
      method: "POST",
      body: JSON.stringify({
        storeCode,
        supplierCode,
        itemNumbers,
        salesRankingDays: normalizeRankingDays(salesRankingDays)
      })
    });
    if (!res.success) return { ok: false, error: res.message || res.errorCode || "\u6458\u8981\u83B7\u53D6\u5931\u8D25" };
    return { ok: true, data: res.data };
  }
  async function handlePurchaseCycles({ storeCode, supplierCode, itemNumber }) {
    if (!storeCode || !supplierCode || !itemNumber) {
      return { ok: false, error: "\u53C2\u6570\u7F3A\u5931" };
    }
    const res = await apiRequest("/api/react/v1/browser-extension/product-purchase-cycles", {
      method: "POST",
      body: JSON.stringify({ storeCode, supplierCode, itemNumber })
    });
    if (!res.success) return { ok: false, error: res.message || res.errorCode || "\u91C7\u8D2D\u5468\u671F\u83B7\u53D6\u5931\u8D25" };
    return { ok: true, data: res.data };
  }
  async function handleStores() {
    const res = await apiRequest("/api/react/v1/browser-extension/stores", { method: "GET" });
    if (!res.success) return { ok: false, error: res.message || res.errorCode || "\u95E8\u5E97\u83B7\u53D6\u5931\u8D25" };
    return { ok: true, data: res.data };
  }
  async function handleSupplierTopSales({ supplierCode, days, topPercent, page, pageSize }) {
    if (!supplierCode) return { ok: false, error: "\u4F9B\u5E94\u5546\u4EE3\u7801\u7F3A\u5931" };
    let pagination;
    try {
      pagination = normalizeTopSalesRequest({ topPercent, page, pageSize });
    } catch (error) {
      return { ok: false, error: error.message };
    }
    const res = await apiRequest("/api/react/v1/browser-extension/supplier-top-sales", {
      method: "POST",
      body: JSON.stringify({
        supplierCode,
        days: normalizeRankingDays(days),
        ...pagination || {}
      })
    });
    if (!res.success) return { ok: false, error: res.message || res.errorCode || "\u70ED\u9500\u6392\u884C\u83B7\u53D6\u5931\u8D25" };
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
      snapshotVersion
    } = message || {};
    if (!supplierCode || !productCode || !startDate || !endDate || !snapshotVersion || !Number.isFinite(Number(totalSalesQuantity))) {
      return { ok: false, error: "\u6392\u884C\u699C\u5546\u54C1\u5FEB\u7167\u4E0D\u5B8C\u6574\uFF0C\u8BF7\u5237\u65B0\u6392\u884C\u699C\u540E\u91CD\u8BD5" };
    }
    const res = await apiRequest("/api/react/v1/browser-extension/supplier-product-store-sales", {
      method: "POST",
      body: JSON.stringify({
        supplierCode,
        productCode,
        days: normalizeRankingDays(days),
        startDate,
        endDate,
        expectedTotalSalesQuantity: Number(totalSalesQuantity),
        snapshotVersion
      })
    });
    if (!res.success) {
      return {
        ok: false,
        error: res.message || res.errorCode || "\u5546\u54C1\u5206\u5E97\u9500\u91CF\u83B7\u53D6\u5931\u8D25",
        errorCode: res.errorCode
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
    const profile = validation.valid ? matchProfile(validation.profiles, { origin: url.origin, pathname: url.pathname }) : null;
    return {
      ok: true,
      supplier: profile ? { supplierCode: profile.supplierCode, displayName: profile.displayName } : null
    };
  }
  async function syncContentScripts() {
    const stored = await getLocal([GRANTED_KEY, PROFILES_KEY]);
    const granted = Array.isArray(stored[GRANTED_KEY]) ? stored[GRANTED_KEY] : [];
    const validation = validateProfiles(stored[PROFILES_KEY]);
    const origins = await resolveGrantedProfileOrigins(
      validation.valid ? validation.profiles : [],
      (origin) => chrome.permissions.contains({ origins: [origin] })
    );
    if (origins.length !== granted.length || origins.some((origin, index) => origin !== granted[index])) {
      await setLocal({ [GRANTED_KEY]: origins });
    }
    try {
      await chrome.scripting.unregisterContentScripts({ ids: ["hb-supplier-list"] });
    } catch {
    }
    if (origins.length) {
      await chrome.scripting.registerContentScripts([
        {
          id: "hb-supplier-list",
          matches: origins,
          js: ["content/list.js"],
          runAt: "document_idle",
          allFrames: false
        }
      ]);
    }
  }
  async function handleRegisterOrigin({ originPattern }) {
    if (typeof originPattern !== "string" || !originPattern) {
      return { ok: false, error: "origin \u7F3A\u5931" };
    }
    const stored = await getLocal([GRANTED_KEY, PROFILES_KEY]);
    const validation = validateProfiles(stored[PROFILES_KEY]);
    const allowedOrigins = new Set(
      (validation.valid ? validation.profiles : []).filter((profile) => profile.enabled !== false).flatMap((profile) => profile.origins || [])
    );
    if (!allowedOrigins.has(originPattern)) {
      return { ok: false, error: "origin \u4E0D\u5728\u5DF2\u542F\u7528\u4F9B\u5E94\u5546\u914D\u7F6E\u4E2D" };
    }
    if (!await chrome.permissions.contains({ origins: [originPattern] })) {
      return { ok: false, error: "origin \u5C1A\u672A\u83B7\u5F97\u6D4F\u89C8\u5668\u6388\u6743" };
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
    const msg = String(e && e.message || e);
    if (/user gesture/i.test(msg)) return "\u9700\u8981\u7528\u6237\u64CD\u4F5C";
    if (/tab/i.test(msg)) return "\u672A\u627E\u5230\u6807\u7B7E\u9875";
    return msg || "\u6253\u5F00\u4FA7\u680F\u5931\u8D25";
  }
  async function focusTab(tab) {
    if (tab.windowId != null && chrome.windows?.update) {
      try {
        await chrome.windows.update(tab.windowId, { focused: true });
      } catch {
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
        }
      }
      return { ok: true, connected: false, pending: true };
    }
    await chrome.tabs.create({ url: `${HB_WEB_ORIGIN}/shop`, active: true });
    return { ok: true, connected: false, pending: true };
  }
  var openHbShop = createSingleFlight(handleOpenHbShop);
  async function handleShopBridgeReady(sender) {
    const senderUrl = sender?.tab?.url || sender?.url;
    const source = validateWebsiteSessionContext({
      pageUrl: senderUrl,
      webOrigin: HB_WEB_ORIGIN,
      apiOrigin: HB_WEB_ORIGIN,
      isTopLevel: sender?.frameId == null || sender.frameId === 0
    });
    if (!source.ok) return { ok: false, reason: source.reason };
    const [{ [PENDING_HANDOFF_KEY]: pending }, apiOrigin] = await Promise.all([
      getSession(PENDING_HANDOFF_KEY),
      getApiOrigin()
    ]);
    return {
      ok: true,
      shouldAuthorize: pending === true,
      apiOrigin
    };
  }
  function openSidePanel(sender, pendingLocate) {
    const tabId = sender && sender.tab && sender.tab.id;
    if (tabId == null) return Promise.resolve({ ok: false, error: "\u7F3A\u5C11\u6807\u7B7E\u9875" });
    const openPromise = assistantPanel.open({ tabId });
    const locatePromise = pendingLocate ? chrome.storage.session.set({ pendingLocate }) : Promise.resolve();
    return Promise.all([openPromise, locatePromise]).then(() => ({ ok: true })).catch((e) => ({ ok: false, error: friendlySidePanelError(e) }));
  }
  chrome.runtime.onInstalled.addListener(async () => {
    await clearLegacyCredentials();
    try {
      await assistantPanel.configureAction();
    } catch {
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
  void clearLegacyCredentials().catch(() => {
  });
  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    const type = message && message.type;
    const run = async () => {
      switch (type) {
        case "GET_API_ORIGIN":
          return handleGetApiOrigin();
        case "SET_API_ORIGIN":
          return handleSetApiOrigin(message);
        case "CURRENT":
          return handleCurrent();
        case "WEBSITE_SESSION_GRANT":
          return acceptWebsiteSessionGrant(message, sender);
        case "SHOP_BRIDGE_READY":
          return handleShopBridgeReady(sender);
        case "DISCONNECT":
          return handleDisconnect();
        case "OPEN_HB_SHOP":
          return openHbShop();
        case "RELEASE":
          return handleRelease();
        case "GET_PROFILES":
          return handleGetProfiles();
        case "SUMMARY_BATCH":
          return handleSummaryBatch(message);
        case "PURCHASE_CYCLES":
          return handlePurchaseCycles(message);
        case "GET_STORES":
          return handleStores();
        case "SUPPLIER_TOP_SALES":
          return handleSupplierTopSales(message);
        case "SUPPLIER_PRODUCT_STORE_SALES":
          return handleSupplierProductStoreSales(message);
        case "ACTIVE_SUPPLIER":
          return handleActiveSupplier();
        case "REGISTER_ORIGIN":
          return handleRegisterOrigin(message);
        case "OPEN_SIDE_PANEL":
          return openSidePanel(sender);
        case "LOCATE_ITEM":
          return openSidePanel(sender, {
            storeCode: message.storeCode,
            supplierCode: message.supplierCode,
            itemNumber: message.itemNumber
          });
        case "CATEGORY_CAPTURE":
          return handleCategoryCapture(message, sender);
        case "CATEGORY_TREE_SNAPSHOT":
          return handleCategoryTreeSnapshot(message, sender);
        case "CATEGORY_CRAWL_START":
          return handleCategoryCrawlStart(message, sender);
        case "CATEGORY_CRAWL_ABORT":
          return handleCategoryCrawlAbort(sender);
        case "CATEGORY_CRAWL_PROGRESS":
          return handleCategoryCrawlProgress(message, sender);
        default:
          return { ok: false, error: "\u672A\u77E5\u6D88\u606F\u7C7B\u578B" };
      }
    };
    run().then(sendResponse).catch((err) => sendResponse({ ok: false, error: String(err && err.message || err) }));
    return true;
  });
})();
