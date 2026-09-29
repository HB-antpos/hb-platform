(() => {
  var __defProp = Object.defineProperty;
  var __getOwnPropNames = Object.getOwnPropertyNames;
  var __esm = (fn, res, err) => function __init() {
    if (err) throw err[0];
    try {
      return fn && (res = (0, fn[__getOwnPropNames(fn)[0]])(fn = 0)), res;
    } catch (e) {
      throw err = [e], e;
    }
  };
  var __export = (target, all) => {
    for (var name in all)
      __defProp(target, name, { get: all[name], enumerable: true });
  };

  // src/lib/category-path.js
  var category_path_exports = {};
  __export(category_path_exports, {
    BUILTIN_CATEGORY_EXCLUDE_PATTERNS: () => BUILTIN_CATEGORY_EXCLUDE_PATTERNS,
    DEFAULT_PROMOTIONAL_PATTERNS: () => DEFAULT_PROMOTIONAL_PATTERNS,
    KEY_QUERY_PARAM_PATTERN: () => KEY_QUERY_PARAM_PATTERN,
    MAX_CATEGORY_KEY_LENGTH: () => MAX_CATEGORY_KEY_LENGTH,
    MAX_CATEGORY_NAME_LENGTH: () => MAX_CATEGORY_NAME_LENGTH,
    MAX_CATEGORY_PATH_DEPTH: () => MAX_CATEGORY_PATH_DEPTH,
    MAX_CATEGORY_URL_LENGTH: () => MAX_CATEGORY_URL_LENGTH,
    MAX_KEY_QUERY_PARAMS: () => MAX_KEY_QUERY_PARAMS,
    detectPageNumber: () => detectPageNumber,
    globToRegex: () => globToRegex,
    humanizeSlug: () => humanizeSlug,
    isPromotionalNode: () => isPromotionalNode,
    isPromotionalPath: () => isPromotionalPath,
    matchesCategoryPage: () => matchesCategoryPage,
    matchesPagePattern: () => matchesPagePattern,
    matchesPromotional: () => matchesPromotional,
    normalizeCategoryKey: () => normalizeCategoryKey,
    normalizeCategoryName: () => normalizeCategoryName,
    normalizeKeyQueryParams: () => normalizeKeyQueryParams,
    resolveCategoryPath: () => resolveCategoryPath
  });
  function globToRegex(pattern, { caseInsensitive = true } = {}) {
    const source = String(pattern ?? "").replace(REGEX_SPECIALS, "\\$&").replaceAll("*", ".*");
    return new RegExp(`^${source}$`, caseInsensitive ? "i" : "");
  }
  function matchesPagePattern(pattern, href) {
    if (typeof pattern !== "string" || !pattern || typeof href !== "string") return false;
    let target = href;
    if (pattern.startsWith("/")) {
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
      return value;
    }
  }
  function toUrl(input) {
    if (input instanceof URL) return input;
    if (typeof input !== "string" || !input) return null;
    try {
      return new URL(input);
    } catch {
      return null;
    }
  }
  function normalizeKeyPath(rawPath) {
    let path = safeDecode(rawPath || "");
    path = path.replace(/\/{2,}/gu, "/").toLowerCase();
    if (!path.startsWith("/")) path = `/${path}`;
    const trimTrailing = (value) => value.length > 1 ? value.replace(/\/+$/u, "") || "/" : value;
    path = trimTrailing(path);
    path = path.replace(PAGE_SUFFIX, "") || "/";
    return trimTrailing(path);
  }
  function splitHash(hash) {
    let value = String(hash || "").replace(/^#/u, "");
    if (value.startsWith("!")) value = value.slice(1);
    const queryIndex = value.indexOf("?");
    return {
      path: queryIndex === -1 ? value : value.slice(0, queryIndex),
      query: queryIndex === -1 ? "" : value.slice(queryIndex + 1)
    };
  }
  function normalizeKeyQueryParams(params) {
    const names = /* @__PURE__ */ new Map();
    for (const name of Array.isArray(params) ? params : []) {
      if (typeof name !== "string" || !KEY_QUERY_PARAM_PATTERN.test(name)) continue;
      const lower = name.toLowerCase();
      if (!names.has(lower)) names.set(lower, lower);
    }
    return [...names.values()].sort();
  }
  function readQueryValue(searchParams, name) {
    for (const [key, value] of searchParams) {
      if (key.toLowerCase() === name) {
        const normalized = String(value ?? "").trim().toLowerCase();
        if (normalized) return normalized;
      }
    }
    return null;
  }
  function normalizeCategoryKey(input, { keySource = "pathname", keyQueryParams = [] } = {}) {
    const url = toUrl(input);
    if (!url) return null;
    let rawPath = url.pathname;
    let hashQuery = "";
    if (keySource === "hash") {
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
        const value = readQueryValue(hashParams, name) ?? readQueryValue(url.searchParams, name);
        if (value != null) pairs.push(`${name}=${value}`);
      }
      if (pairs.length > 0) key = `${key}?${pairs.join("&")}`;
    }
    if (!key.startsWith("/") || key.length > MAX_CATEGORY_KEY_LENGTH) return null;
    return key;
  }
  function normalizeCategoryName(name) {
    if (name == null) return "";
    let value = String(name).replace(/\s+/gu, " ").trim();
    value = value.replace(TRAILING_COUNT, "").trim();
    if (value.length > MAX_CATEGORY_NAME_LENGTH) value = value.slice(0, MAX_CATEGORY_NAME_LENGTH).trim();
    return value;
  }
  function humanizeSlug(slug) {
    const decoded = safeDecode(String(slug ?? "")).replace(FILE_EXTENSION, "");
    const words = decoded.replace(/[-_+]+/gu, " ").replace(/\s+/gu, " ").trim();
    if (!words) return "";
    return normalizeCategoryName(
      words.split(" ").map((word) => word ? word[0].toUpperCase() + word.slice(1) : word).join(" ")
    );
  }
  function matchesCategoryPage(href, config) {
    if (typeof href !== "string" || !config) return false;
    const excludes = Array.isArray(config.categoryExcludePatterns) ? config.categoryExcludePatterns : BUILTIN_CATEGORY_EXCLUDE_PATTERNS;
    if (excludes.some((pattern) => matchesPagePattern(pattern, href))) return false;
    const patterns = Array.isArray(config.categoryPagePatterns) ? config.categoryPagePatterns : [];
    return patterns.some((pattern) => matchesPagePattern(pattern, href));
  }
  function matchesPromotional(value, patterns = DEFAULT_PROMOTIONAL_PATTERNS) {
    if (typeof value !== "string" || !value) return false;
    const target = value.toLowerCase();
    return (patterns || []).some((pattern) => typeof pattern === "string" && pattern && globToRegex(pattern.toLowerCase()).test(target));
  }
  function isPromotionalNode(node, patterns = DEFAULT_PROMOTIONAL_PATTERNS) {
    if (!node) return false;
    const key = typeof node.key === "string" ? node.key : "";
    const path = key.split("?")[0];
    const segments = path.split("/").filter(Boolean).map((segment) => segment.replace(FILE_EXTENSION, ""));
    const candidates = [...segments, key];
    const name = normalizeCategoryName(node.name).toLowerCase().replace(/\s+/gu, "-");
    if (name) candidates.push(name);
    return candidates.some((candidate) => matchesPromotional(candidate, patterns));
  }
  function isPromotionalPath(path, patterns = DEFAULT_PROMOTIONAL_PATTERNS) {
    return Array.isArray(path) && path.some((node) => isPromotionalNode(node, patterns));
  }
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
      if (!key && isLast) key = pageKey;
      if (!key) return null;
      if (key === "/") continue;
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
      const leafName = normalizeCategoryName(title) || humanizeLastSegment(pageKey);
      const extra = buildNode(leafName, pageKey, pageUrl, origin);
      if (!extra) return null;
      path.push(extra);
    }
    return dedupeConsecutive(path);
  }
  function humanizeLastSegment(key) {
    const path = String(key || "").split("?")[0];
    const segments = path.split("/").filter(Boolean);
    return humanizeSlug(segments[segments.length - 1] || "");
  }
  function resolveFromUrlSegments({ pageKey, pageUrl, title, config, origin }) {
    if (pageKey.includes("?")) return null;
    const segments = pageKey.split("/").filter(Boolean);
    if (segments.length === 0) return null;
    const path = [];
    for (let index = 0; index < segments.length; index += 1) {
      const key = `/${segments.slice(0, index + 1).join("/")}`;
      const isLast = index === segments.length - 1;
      const url = origin ? `${origin}${key}` : null;
      if (!isLast && url && !matchesCategoryPage(url, config)) continue;
      const name = isLast ? normalizeCategoryName(title) || humanizeSlug(segments[index]) : humanizeSlug(segments[index]);
      const node = buildNode(name, key, isLast ? pageUrl : url, origin);
      if (!node) return null;
      path.push(node);
    }
    return path.length > 0 ? path : null;
  }
  function resolveCategoryPath({ pageUrl, breadcrumbItems, title, config }) {
    if (!config) return null;
    const url = toUrl(pageUrl);
    if (!url) return null;
    const origin = url.origin;
    const pageKey = normalizeCategoryKey(url, config);
    if (!pageKey || pageKey === "/") return null;
    const href = cleanUrl(url.href, origin);
    const attempts = [
      ["breadcrumb", () => resolveFromBreadcrumb({
        items: breadcrumbItems,
        pageKey,
        pageUrl: href,
        title,
        config,
        origin
      })],
      ["url", () => config.keySource === "hash" || (config.keyQueryParams || []).length > 0 ? null : resolveFromUrlSegments({ pageKey, pageUrl: href, title, config, origin })],
      ["title", () => {
        const node = buildNode(title, pageKey, href, origin);
        return node ? [node] : null;
      }]
    ];
    for (const [source, attempt] of attempts) {
      const path = attempt();
      if (!path || path.length === 0) continue;
      if (path.length > MAX_CATEGORY_PATH_DEPTH) return null;
      return { source, path, leafKey: path[path.length - 1].key };
    }
    return null;
  }
  function detectPageNumber(href) {
    const url = toUrl(href);
    if (!url) return void 0;
    const pathMatch = /\/page\/(\d+)\/?$/iu.exec(url.pathname);
    const candidates = pathMatch ? [pathMatch[1]] : [];
    for (const [name, value] of url.searchParams) {
      if (/^(?:page|paged|p|pg|pagenumber|pageproduct|pageno)$/iu.test(name)) candidates.push(value);
    }
    for (const candidate of candidates) {
      const number = Number(candidate);
      if (Number.isInteger(number) && number >= 1 && number <= 1e4) return number;
    }
    return void 0;
  }
  var MAX_CATEGORY_KEY_LENGTH, MAX_CATEGORY_NAME_LENGTH, MAX_CATEGORY_URL_LENGTH, MAX_CATEGORY_PATH_DEPTH, MAX_KEY_QUERY_PARAMS, KEY_QUERY_PARAM_PATTERN, BUILTIN_CATEGORY_EXCLUDE_PATTERNS, DEFAULT_PROMOTIONAL_PATTERNS, REGEX_SPECIALS, PAGE_SUFFIX, FILE_EXTENSION, TRAILING_COUNT;
  var init_category_path = __esm({
    "src/lib/category-path.js"() {
      MAX_CATEGORY_KEY_LENGTH = 300;
      MAX_CATEGORY_NAME_LENGTH = 200;
      MAX_CATEGORY_URL_LENGTH = 1e3;
      MAX_CATEGORY_PATH_DEPTH = 8;
      MAX_KEY_QUERY_PARAMS = 5;
      KEY_QUERY_PARAM_PATTERN = /^[A-Za-z0-9_\-[\]]{1,50}$/u;
      BUILTIN_CATEGORY_EXCLUDE_PATTERNS = Object.freeze([
        "/search*",
        "/cart*",
        "/checkout*",
        "/account*",
        "/login*",
        "/my-account*",
        "/wishlist*"
      ]);
      DEFAULT_PROMOTIONAL_PATTERNS = Object.freeze([
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
      REGEX_SPECIALS = /[|\\{}()[\]^$+?.]/g;
      PAGE_SUFFIX = /\/page\/\d+$/u;
      FILE_EXTENSION = /\.(?:html?|php|aspx?|jsp)$/iu;
      TRAILING_COUNT = /\s*\(\s*\d+\s*\)$/u;
    }
  });

  // src/lib/transforms.js
  function isTransformAllowed(type) {
    return ALLOWED_TRANSFORMS.has(type);
  }
  function normalizeTransform(transform) {
    return typeof transform === "string" ? { type: transform } : transform;
  }
  function applyTransform(value, transform) {
    const normalized = normalizeTransform(transform);
    const t2 = normalized && normalized.type;
    if (!isTransformAllowed(t2)) {
      throw new Error(`unsupported transform: ${String(t2)}`);
    }
    const s = value == null ? "" : String(value);
    switch (t2) {
      case "trim":
        return s.trim();
      case "uppercase":
        return s.toUpperCase();
      case "lowercase":
        return s.toLowerCase();
      case "after-colon": {
        const colonIndex = s.indexOf(":");
        return colonIndex === -1 ? "" : s.slice(colonIndex + 1).trim();
      }
      case "underscore-to-slash":
        return s.replaceAll("_", "/");
      case "after-sku": {
        const match = /^\s*-?\s*SKU\s+(.+)$/i.exec(s);
        return match ? match[1].trim() : "";
      }
      default:
        throw new Error(`unsupported transform: ${String(t2)}`);
    }
  }
  function applyTransforms(value, transforms) {
    let out = value;
    for (const t2 of transforms || []) {
      out = applyTransform(out, t2);
    }
    return out;
  }
  function safeTransformList(transforms) {
    if (transforms == null) return true;
    if (!Array.isArray(transforms)) return false;
    return transforms.every((transform) => {
      const normalized = normalizeTransform(transform);
      return !!normalized && isTransformAllowed(normalized.type);
    });
  }
  var ALLOWED_TRANSFORMS;
  var init_transforms = __esm({
    "src/lib/transforms.js"() {
      ALLOWED_TRANSFORMS = /* @__PURE__ */ new Set([
        "trim",
        "uppercase",
        "lowercase",
        "after-colon",
        "underscore-to-slash",
        "after-sku"
      ]);
    }
  });

  // src/lib/item-number.js
  var item_number_exports = {};
  __export(item_number_exports, {
    MAX_ITEM_NUMBER_LENGTH: () => MAX_ITEM_NUMBER_LENGTH,
    normalizeCaptureItemNumber: () => normalizeCaptureItemNumber,
    normalizeCaptureItemNumbers: () => normalizeCaptureItemNumbers,
    readItemNumberFrom: () => readItemNumberFrom,
    readItemNumbersFromCards: () => readItemNumbersFromCards
  });
  function readItemNumberFrom(card, itemCfg) {
    if (!card || !itemCfg) return "";
    let el = card;
    if (itemCfg.selector) {
      const sub = card.querySelector(itemCfg.selector);
      if (!sub) return "";
      el = sub;
    }
    const raw = itemCfg.source === "attribute" ? el.getAttribute(itemCfg.attribute) : el.textContent;
    return applyTransforms(raw, itemCfg.transforms);
  }
  function normalizeCaptureItemNumber(value) {
    if (value == null) return "";
    const normalized = String(value).trim().toUpperCase();
    if (!normalized || normalized.length > MAX_ITEM_NUMBER_LENGTH) return "";
    if (/[\u0000-\u001f\u007f]/u.test(normalized)) return "";
    return normalized;
  }
  function readItemNumbersFromCards(cards, itemCfg) {
    const seen = /* @__PURE__ */ new Set();
    const out = [];
    for (const card of cards || []) {
      let value = "";
      try {
        value = normalizeCaptureItemNumber(readItemNumberFrom(card, itemCfg));
      } catch {
        value = "";
      }
      if (!value || seen.has(value)) continue;
      seen.add(value);
      out.push(value);
    }
    return out;
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
  var MAX_ITEM_NUMBER_LENGTH;
  var init_item_number = __esm({
    "src/lib/item-number.js"() {
      init_transforms();
      MAX_ITEM_NUMBER_LENGTH = 50;
    }
  });

  // src/lib/category-capture.js
  var category_capture_exports = {};
  __export(category_capture_exports, {
    CAPTURE_CHUNK_SIZE: () => CAPTURE_CHUNK_SIZE,
    CAPTURE_DEDUPE_MAX_ENTRIES: () => CAPTURE_DEDUPE_MAX_ENTRIES,
    CAPTURE_DEDUPE_TTL_MS: () => CAPTURE_DEDUPE_TTL_MS,
    CAPTURE_MODES: () => CAPTURE_MODES,
    CAPTURE_QUIET_MS: () => CAPTURE_QUIET_MS,
    MAX_RETRY_AFTER_MS: () => MAX_RETRY_AFTER_MS,
    MAX_SUPPLIER_CODE_LENGTH: () => MAX_SUPPLIER_CODE_LENGTH,
    MAX_TREE_SNAPSHOT_NODES: () => MAX_TREE_SNAPSHOT_NODES,
    buildCaptureDedupeKey: () => buildCaptureDedupeKey,
    buildCapturePayload: () => buildCapturePayload,
    classifyCaptureFailure: () => classifyCaptureFailure,
    createCaptureDedupeStore: () => createCaptureDedupeStore,
    createCaptureScheduler: () => createCaptureScheduler,
    createPassiveCaptureController: () => createPassiveCaptureController,
    createRetryPolicy: () => createRetryPolicy,
    createSlidingWindowLimiter: () => createSlidingWindowLimiter,
    diffNewItems: () => diffNewItems,
    fnv1a: () => fnv1a,
    hashItemNumbers: () => hashItemNumbers,
    normalizeCaptureResponse: () => normalizeCaptureResponse,
    normalizeTreeSnapshotResponse: () => normalizeTreeSnapshotResponse,
    parseRetryAfter: () => parseRetryAfter,
    runWithRetry: () => runWithRetry,
    splitIntoChunks: () => splitIntoChunks,
    truncatePageUrl: () => truncatePageUrl,
    validateCapturePayload: () => validateCapturePayload,
    validateTreeSnapshotPayload: () => validateTreeSnapshotPayload
  });
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
  function createCaptureScheduler({
    quietMs = CAPTURE_QUIET_MS,
    now = () => Date.now(),
    setTimer = (fn, ms) => setTimeout(fn, ms),
    clearTimer = (id) => clearTimeout(id),
    onStable
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
      }
    };
  }
  function diffNewItems(itemNumbers, sentSet) {
    const seen = /* @__PURE__ */ new Set();
    const out = [];
    for (const item of itemNumbers || []) {
      if (!item || seen.has(item) || sentSet?.has(item)) continue;
      seen.add(item);
      out.push(item);
    }
    return out;
  }
  function splitIntoChunks(items, size = CAPTURE_CHUNK_SIZE) {
    const chunkSize = Number.isInteger(size) && size > 0 ? size : CAPTURE_CHUNK_SIZE;
    const list = Array.isArray(items) ? items : [];
    const out = [];
    for (let index = 0; index < list.length; index += chunkSize) {
      out.push(list.slice(index, index + chunkSize));
    }
    return out;
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
  function buildCapturePayload({
    supplierCode,
    pageUrl,
    categoryPath,
    itemNumbers,
    capturedAt,
    mode,
    pageNumber,
    now = () => Date.now()
  }) {
    return {
      supplierCode: String(supplierCode || ""),
      pageUrl: truncatePageUrl(pageUrl),
      categoryPath: (Array.isArray(categoryPath) ? categoryPath : []).map((node) => ({
        name: node.name,
        key: node.key,
        url: node.url || null
      })),
      itemNumbers: normalizeCaptureItemNumbers(itemNumbers).slice(0, CAPTURE_CHUNK_SIZE),
      capturedAt: toIsoString(capturedAt, now),
      mode,
      ...Number.isInteger(pageNumber) && pageNumber >= 1 ? { pageNumber } : {}
    };
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
  function classifyCaptureFailure({ httpStatus, errorCode, networkError } = {}) {
    const status = Number(httpStatus) || 0;
    const code = errorCode || (status ? `HTTP_${status}` : "NETWORK_ERROR");
    const retryable = RETRYABLE_ERROR_CODES.has(code) || !!networkError || status === 0 || status === 408 || status === 409 || status === 429 || status >= 500 && status <= 599;
    const fatal = !retryable && (FATAL_ERROR_CODES.has(code) || status === 401 || status === 403 || status === 404);
    return { retryable, fatal, code };
  }
  function parseRetryAfter(value, now = () => Date.now()) {
    if (value == null || value === "") return null;
    const text = String(value).trim();
    if (/^\d+(?:\.\d+)?$/u.test(text)) return Math.min(Number(text) * 1e3, MAX_RETRY_AFTER_MS);
    const date = Date.parse(text);
    if (!Number.isFinite(date)) return null;
    return Math.min(Math.max(0, date - now()), MAX_RETRY_AFTER_MS);
  }
  function createRetryPolicy({
    maxAttempts = 3,
    baseDelayMs = 1e3,
    maxDelayMs = MAX_RETRY_AFTER_MS
  } = {}) {
    return {
      maxAttempts,
      shouldRetry(attempt, classification) {
        return !!classification?.retryable && attempt < maxAttempts;
      },
      delayFor(attempt, retryAfterMs) {
        if (Number.isFinite(retryAfterMs) && retryAfterMs >= 0) return Math.min(retryAfterMs, maxDelayMs);
        return Math.min(baseDelayMs * 2 ** Math.max(0, attempt - 1), maxDelayMs);
      }
    };
  }
  async function runWithRetry(task, { policy = createRetryPolicy(), sleep = defaultSleep, signal } = {}) {
    let attempt = 0;
    for (; ; ) {
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
        return { ...result || { ok: false }, ok: false, classification, attempts: attempt };
      }
      await sleep(policy.delayFor(attempt, result?.retryAfterMs), signal);
    }
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
  function createPassiveCaptureController({
    supplierCode,
    getConfig,
    readPageContext: readPageContext2,
    sendCapture,
    getCurrentHref = null,
    now = () => Date.now(),
    setTimer,
    clearTimer,
    sleep,
    quietMs = CAPTURE_QUIET_MS,
    retryPolicy = createRetryPolicy(),
    maxTrackedItems = 5e3,
    onResult = null
  }) {
    let disabled = false;
    let queue = Promise.resolve();
    const sentByLeaf = /* @__PURE__ */ new Map();
    let trackedCount = 0;
    const scheduler = createCaptureScheduler({
      quietMs,
      now,
      setTimer,
      clearTimer,
      onStable: (snapshot) => {
        queue = queue.then(() => capture(snapshot)).then((result) => onResult?.(result)).catch(() => void 0);
      }
    });
    function markSent(leafKey, items) {
      if (trackedCount + items.length > maxTrackedItems) {
        sentByLeaf.clear();
        trackedCount = 0;
      }
      let sent = sentByLeaf.get(leafKey);
      if (!sent) {
        sent = /* @__PURE__ */ new Set();
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
      if (disabled || !config?.enabled || !config.passiveEnabled) return { skipped: "disabled" };
      if (getCurrentHref && getCurrentHref() !== href) return { skipped: "navigated" };
      const context = readPageContext2() || {};
      const resolved = resolveCategoryPath({
        pageUrl: href,
        breadcrumbItems: context.breadcrumbItems,
        title: context.title,
        config
      });
      if (!resolved) return { skipped: "no-path" };
      if (isPromotionalPath(resolved.path, config.promotionalPatterns)) return { skipped: "promotional" };
      const newItems = diffNewItems(items, sentByLeaf.get(resolved.leafKey));
      if (newItems.length === 0) return { skipped: "no-new-items" };
      const pageNumber = detectPageNumber(href);
      let sentCount = 0;
      for (const chunk of splitIntoChunks(newItems, CAPTURE_CHUNK_SIZE)) {
        const payload = buildCapturePayload({
          supplierCode,
          pageUrl: href,
          categoryPath: resolved.path,
          itemNumbers: chunk,
          mode: "passive",
          pageNumber,
          now
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
          return "disabled";
        }
        if (!matchesCategoryPage(href, config)) {
          scheduler.reset();
          return "not-category";
        }
        const items = normalizeCaptureItemNumbers(itemNumbers);
        if (items.length === 0) {
          scheduler.reset();
          return "empty";
        }
        scheduler.notify(`${href}
${items.length}:${hashItemNumbers(items)}`, { href, items });
        return "scheduled";
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
      }
    };
  }
  var CAPTURE_CHUNK_SIZE, CAPTURE_DEDUPE_TTL_MS, CAPTURE_DEDUPE_MAX_ENTRIES, CAPTURE_QUIET_MS, CAPTURE_MODES, MAX_TREE_SNAPSHOT_NODES, MAX_RETRY_AFTER_MS, MAX_SUPPLIER_CODE_LENGTH, FATAL_ERROR_CODES, RETRYABLE_ERROR_CODES, defaultSleep;
  var init_category_capture = __esm({
    "src/lib/category-capture.js"() {
      init_category_path();
      init_item_number();
      CAPTURE_CHUNK_SIZE = 100;
      CAPTURE_DEDUPE_TTL_MS = 6 * 60 * 60 * 1e3;
      CAPTURE_DEDUPE_MAX_ENTRIES = 500;
      CAPTURE_QUIET_MS = 1500;
      CAPTURE_MODES = Object.freeze(["passive", "crawl"]);
      MAX_TREE_SNAPSHOT_NODES = 2e3;
      MAX_RETRY_AFTER_MS = 6e4;
      MAX_SUPPLIER_CODE_LENGTH = 50;
      FATAL_ERROR_CODES = /* @__PURE__ */ new Set([
        "FEATURE_DISABLED",
        "NOT_FOUND",
        "SUPPLIER_NOT_CAPTURABLE",
        "CATEGORY_CAPTURE_DISABLED",
        "WEBSITE_SESSION_REQUIRED",
        "WEBSITE_TAB_REQUIRED",
        "WEBSITE_BRIDGE_UNAVAILABLE",
        "API_ORIGIN_MISMATCH",
        "INVALID_TOKEN_RESPONSE",
        "CRAWL_JOB_MISMATCH"
      ]);
      RETRYABLE_ERROR_CODES = /* @__PURE__ */ new Set(["SUPPLIER_CATEGORY_BUSY", "LOCAL_RATE_LIMITED", "NETWORK_ERROR"]);
      defaultSleep = (ms) => new Promise((resolve) => {
        setTimeout(resolve, ms);
      });
    }
  });

  // src/lib/category-crawl.js
  var category_crawl_exports = {};
  __export(category_crawl_exports, {
    CRAWL_ERROR_CODES: () => CRAWL_ERROR_CODES,
    CRAWL_STATUSES: () => CRAWL_STATUSES,
    MAX_CONSECUTIVE_CATEGORY_FAILURES: () => MAX_CONSECUTIVE_CATEGORY_FAILURES,
    MAX_TRACKED_CRAWL_KEYS: () => MAX_TRACKED_CRAWL_KEYS,
    STALE_RUNNING_JOB_MS: () => STALE_RUNNING_JOB_MS,
    TERMINAL_CRAWL_STATUSES: () => TERMINAL_CRAWL_STATUSES,
    buildNavTree: () => buildNavTree,
    createCrawlJob: () => createCrawlJob,
    createCrawlRunner: () => createCrawlRunner,
    detectLoginPage: () => detectLoginPage,
    finalizeCrawlJob: () => finalizeCrawlJob,
    inferParentByPrefix: () => inferParentByPrefix,
    isCrawlJobStale: () => isCrawlJobStale,
    isTerminalCrawlStatus: () => isTerminalCrawlStatus,
    mergeCrawlProgress: () => mergeCrawlProgress,
    resolvePathForNode: () => resolvePathForNode,
    sanitizeCrawlNodes: () => sanitizeCrawlNodes,
    summarizeProgress: () => summarizeProgress,
    toCrawlHistoryEntry: () => toCrawlHistoryEntry,
    toTreeSnapshotNodes: () => toTreeSnapshotNodes
  });
  function parseUrl2(value) {
    if (typeof value !== "string" || !value) return null;
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
    const segments = String(key || "").split("?")[0].split("/").filter(Boolean);
    return humanizeSlug(segments[segments.length - 1] || "");
  }
  function inferParentByPrefix(key, keys) {
    if (typeof key !== "string" || key.includes("?")) return null;
    let current = key;
    for (; ; ) {
      const index = current.lastIndexOf("/");
      if (index <= 0) return null;
      current = current.slice(0, index);
      if (keys.has(current)) return current;
    }
  }
  function buildNavTree(anchors, { config, origin }) {
    const byKey = /* @__PURE__ */ new Map();
    const order = [];
    for (const anchor of Array.isArray(anchors) ? anchors : []) {
      const url = parseUrl2(anchor?.url);
      if (!url || !/^https?:$/u.test(url.protocol) || url.origin !== origin) continue;
      if (!matchesCategoryPage(url.href, config)) continue;
      const key = normalizeCategoryKey(url, config);
      if (!key || key === "/") continue;
      const domParentKey = anchor.domParentUrl ? normalizeCategoryKey(anchor.domParentUrl, config) : null;
      const existing = byKey.get(key);
      if (existing) {
        if (!existing.domParentKey && domParentKey && domParentKey !== key) existing.domParentKey = domParentKey;
        continue;
      }
      const name = normalizeCategoryName(anchor.name) || lastSegmentName(key);
      if (!name) continue;
      const node = {
        key,
        name,
        url: cleanHref(url),
        domParentKey: domParentKey && domParentKey !== key && domParentKey !== "/" ? domParentKey : null,
        parentKey: null
      };
      byKey.set(key, node);
      order.push(node);
    }
    const keys = new Set(byKey.keys());
    for (const node of order) {
      node.parentKey = node.domParentKey && keys.has(node.domParentKey) ? node.domParentKey : inferParentByPrefix(node.key, keys);
    }
    for (const node of order) {
      const seen = /* @__PURE__ */ new Set([node.key]);
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
    const children = /* @__PURE__ */ new Map();
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
      MAX_TREE_SNAPSHOT_NODES
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
        sortOrder
      });
      (children.get(node.key) || []).forEach((child, index) => {
        queue.push({ node: child, depth: depth + 1, sortOrder: index });
      });
    }
    return { nodes, truncated, droppedPromotional, droppedDepth };
  }
  function resolvePathForNode(nodesByKey, key) {
    const path = [];
    const seen = /* @__PURE__ */ new Set();
    let current = nodesByKey.get(key);
    while (current && !seen.has(current.key) && path.length < MAX_CATEGORY_PATH_DEPTH) {
      seen.add(current.key);
      path.unshift({ name: current.name, key: current.key, url: current.url || null });
      current = current.parentKey ? nodesByKey.get(current.parentKey) : null;
    }
    return path;
  }
  function toTreeSnapshotNodes(nodes) {
    return (Array.isArray(nodes) ? nodes : []).slice(0, MAX_TREE_SNAPSHOT_NODES).map((node) => ({
      key: node.key,
      name: node.name,
      parentKey: node.parentKey || null,
      url: node.url || null,
      sortOrder: Number.isInteger(node.sortOrder) ? node.sortOrder : null
    }));
  }
  function detectLoginPage({
    requestedUrl,
    finalUrl,
    hasPasswordField: hasPasswordField2 = false,
    cardCount = 0,
    breadcrumbCount = 0
  } = {}) {
    const requested = parseUrl2(requestedUrl);
    const final = parseUrl2(finalUrl);
    if (final && requested && LOGIN_PATH.test(final.pathname) && !LOGIN_PATH.test(requested.pathname)) {
      return true;
    }
    return !!hasPasswordField2 && cardCount === 0 && breadcrumbCount === 0;
  }
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
  function summarizeProgress(job) {
    const total = toCount2(job?.total);
    const done = toCount2(job?.done);
    const failed = toCount2(job?.failed);
    return {
      total,
      done,
      failed,
      remaining: Math.max(0, total - done - failed),
      percent: total > 0 ? Math.min(100, Math.round(done / total * 100)) : 0
    };
  }
  function createCrawlRunner({
    config,
    origin,
    supplierCode,
    fetchHtml,
    parsePage,
    onCapture,
    onProgress = () => void 0,
    sleep = defaultSleep2,
    now = () => Date.now(),
    signal = null,
    retryPolicy = createRetryPolicy(),
    progressIntervalMs = 1e3,
    maxConsecutiveFailures = MAX_CONSECUTIVE_CATEGORY_FAILURES,
    maxFetchRetries = 2
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
        const final = parseUrl2(finalUrl);
        if (final && final.origin !== origin) {
          return LOGIN_PATH.test(final.pathname) ? { ok: false, fatal: true, code: "LOGIN_REQUIRED" } : { ok: false, code: "CROSS_ORIGIN_REDIRECT" };
        }
        if (response?.ok && typeof response.html === "string" && response.html) {
          return { ok: true, html: response.html, finalUrl };
        }
        if (status === 401 || status === 407) return { ok: false, fatal: true, code: "LOGIN_REQUIRED" };
        const retryable = status === 0 || status === 429 || status >= 500;
        if (!retryable || attempt >= maxFetchRetries) {
          return { ok: false, code: status ? `HTTP_${status}` : "NETWORK_ERROR" };
        }
        const retryAfter = status === 429 || status === 503 ? parseRetryAfter(response?.retryAfter, now) : null;
        const backoff = retryAfter ?? delayMs * 2 ** (attempt + 1);
        await sleep(Math.min(backoff, MAX_RETRY_AFTER_MS), signal);
      }
      return { ok: false, code: "NETWORK_ERROR" };
    }
    function acceptSubcategory(link, parent, nodesByKey) {
      const url = parseUrl2(link?.url);
      if (!url || !/^https?:$/u.test(url.protocol) || url.origin !== origin) return null;
      if (!matchesCategoryPage(url.href, config)) return null;
      const key = normalizeCategoryKey(url, config);
      if (!key || key === "/" || key === parent.key || nodesByKey.has(key)) return null;
      const queryKeyed = (config.keyQueryParams || []).length > 0 || config.keySource === "hash";
      if (!queryKeyed && !key.startsWith(`${parent.key}/`)) return null;
      const name = normalizeCategoryName(link.name) || lastSegmentName(key);
      const node = { key, name, url: cleanHref(url), parentKey: parent.key, depth: (parent.depth || 0) + 1 };
      if (!name || node.depth >= maxDepth || isPromotionalNode(node, config.promotionalPatterns)) return null;
      return node;
    }
    async function crawlCategory(node, nodesByKey, stats) {
      let url = node.url;
      if (!url) return { ok: false, code: "NO_URL" };
      const visited = /* @__PURE__ */ new Set();
      const subcategories = [];
      let pageNumber = 1;
      let failureCode = null;
      while (url && pageNumber <= maxPages) {
        if (aborted()) return { aborted: true };
        const visitKey = url.split("#")[0];
        if (visited.has(visitKey)) break;
        visited.add(visitKey);
        const page = await fetchPage(url);
        if (page.aborted) return { aborted: true };
        if (!page.ok) {
          if (page.fatal) return { ok: false, fatal: true, code: page.code };
          if (pageNumber === 1) return { ok: false, code: page.code };
          break;
        }
        stats.pages += 1;
        let parsed;
        try {
          parsed = parsePage(page.html, page.finalUrl) || {};
        } catch {
          return { ok: false, code: "PARSE_FAILED" };
        }
        const itemNumbers = normalizeCaptureItemNumbers(parsed.itemNumbers);
        const breadcrumbItems = Array.isArray(parsed.breadcrumbItems) ? parsed.breadcrumbItems : [];
        if (detectLoginPage({
          requestedUrl: url,
          finalUrl: page.finalUrl,
          hasPasswordField: parsed.hasPasswordField,
          cardCount: itemNumbers.length,
          breadcrumbCount: breadcrumbItems.length
        })) {
          return { ok: false, fatal: true, code: "LOGIN_REQUIRED" };
        }
        const resolved = resolveCategoryPath({
          pageUrl: page.finalUrl,
          breadcrumbItems,
          title: parsed.title,
          config
        });
        const path = resolved && resolved.leafKey === node.key ? resolved.path : resolvePathForNode(nodesByKey, node.key);
        if (path.length > 0 && !isPromotionalPath(path, config.promotionalPatterns)) {
          for (const chunk of splitIntoChunks(itemNumbers, CAPTURE_CHUNK_SIZE)) {
            if (aborted()) return { aborted: true };
            const payload = buildCapturePayload({
              supplierCode,
              pageUrl: page.finalUrl,
              categoryPath: path,
              itemNumbers: chunk,
              mode: "crawl",
              pageNumber,
              now
            });
            const result = await runWithRetry(() => onCapture(payload), { policy: retryPolicy, sleep, signal });
            if (result.ok) {
              stats.itemsSent += chunk.length;
              continue;
            }
            if (aborted()) return { aborted: true };
            if (result.classification?.fatal) return { ok: false, fatal: true, code: result.classification.code };
            failureCode = result.classification?.code || "CAPTURE_FAILED";
          }
        }
        if (pageNumber === 1) {
          for (const link of Array.isArray(parsed.subcategoryLinks) ? parsed.subcategoryLinks : []) {
            const child = acceptSubcategory(link, node, nodesByKey);
            if (child && !subcategories.some((item) => item.key === child.key)) subcategories.push(child);
          }
        }
        const next = parseUrl2(parsed.nextUrl);
        if (!next || next.origin !== origin || normalizeCategoryKey(next, config) !== node.key) break;
        url = next.href;
        pageNumber += 1;
      }
      return failureCode ? { ok: false, code: failureCode, subcategories } : { ok: true, subcategories };
    }
    async function run({ nodes = [], completedKeys = [], onlyNodes = null } = {}) {
      if (running) return { status: CRAWL_STATUSES.FAILED, errorCode: "CRAWL_ALREADY_RUNNING" };
      running = true;
      try {
        const nodesByKey = /* @__PURE__ */ new Map();
        for (const node of Array.isArray(nodes) ? nodes : []) nodesByKey.set(node.key, { ...node });
        const scope = Array.isArray(onlyNodes) ? sanitizeCrawlNodes(onlyNodes) : [...nodesByKey.values()];
        for (const node of scope) {
          if (!nodesByKey.has(node.key)) nodesByKey.set(node.key, { ...node });
        }
        const queue = scope.map((node) => nodesByKey.get(node.key));
        const completed = new Set(sanitizeKeys(completedKeys));
        const failedNodes = /* @__PURE__ */ new Map();
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
            ...overrides
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
              depth: node.depth || 0
            });
            consecutiveFailures += 1;
            if (consecutiveFailures >= maxConsecutiveFailures) {
              status = CRAWL_STATUSES.FAILED;
              errorCode = "SITE_BLOCKING";
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
          discovered
        };
      } finally {
        running = false;
      }
    }
    return {
      run,
      get running() {
        return running;
      }
    };
  }
  var CRAWL_STATUSES, TERMINAL_CRAWL_STATUSES, CRAWL_ERROR_CODES, MAX_CONSECUTIVE_CATEGORY_FAILURES, MAX_TRACKED_CRAWL_KEYS, STALE_RUNNING_JOB_MS, LOGIN_PATH, defaultSleep2;
  var init_category_crawl = __esm({
    "src/lib/category-crawl.js"() {
      init_category_path();
      init_category_capture();
      init_item_number();
      CRAWL_STATUSES = Object.freeze({
        RUNNING: "running",
        COMPLETED: "completed",
        ABORTED: "aborted",
        INTERRUPTED: "interrupted",
        FAILED: "failed"
      });
      TERMINAL_CRAWL_STATUSES = /* @__PURE__ */ new Set(["completed", "aborted", "interrupted", "failed"]);
      CRAWL_ERROR_CODES = Object.freeze([
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
      MAX_CONSECUTIVE_CATEGORY_FAILURES = 5;
      MAX_TRACKED_CRAWL_KEYS = 2e3;
      STALE_RUNNING_JOB_MS = 5 * 60 * 1e3;
      LOGIN_PATH = /(?:^|\/)(?:login|log-in|signin|sign-in|logon|account\/login|customer\/account\/login|my-account)(?:[/.?#]|$)/iu;
      defaultSleep2 = (ms, signal) => new Promise((resolve) => {
        if (signal?.aborted) {
          resolve();
          return;
        }
        const timer = setTimeout(done, ms);
        function done() {
          clearTimeout(timer);
          signal?.removeEventListener?.("abort", done);
          resolve();
        }
        signal?.addEventListener?.("abort", done, { once: true });
      });
    }
  });

  // src/lib/category-dom.js
  var category_dom_exports = {};
  __export(category_dom_exports, {
    hasPasswordField: () => hasPasswordField,
    parseHtml: () => parseHtml,
    probeSelector: () => probeSelector,
    readBreadcrumbItems: () => readBreadcrumbItems,
    readCards: () => readCards,
    readNavAnchors: () => readNavAnchors,
    readNextPageUrl: () => readNextPageUrl,
    readPageContext: () => readPageContext,
    readSubcategoryLinks: () => readSubcategoryLinks,
    readTitle: () => readTitle,
    resolveDocumentBase: () => resolveDocumentBase,
    resolveLink: () => resolveLink
  });
  function parseHtml(html, DOMParserImpl = globalThis.DOMParser) {
    return new DOMParserImpl().parseFromString(String(html ?? ""), "text/html");
  }
  function probeSelector(root, selector) {
    if (typeof selector !== "string" || !selector) return null;
    try {
      root.querySelector(selector);
      return selector;
    } catch {
      return null;
    }
  }
  function safeQueryAll(root, selector) {
    if (!root || typeof selector !== "string" || !selector) return [];
    try {
      return Array.from(root.querySelectorAll(selector));
    } catch {
      return [];
    }
  }
  function safeQuery(root, selector) {
    if (!root || typeof selector !== "string" || !selector) return null;
    try {
      return root.querySelector(selector);
    } catch {
      return null;
    }
  }
  function normalizeText(value) {
    return String(value ?? "").replace(/\s+/gu, " ").trim();
  }
  function resolveDocumentBase(root, fetchedUrl) {
    const doc = root?.ownerDocument || root;
    const base = safeQuery(doc, "base[href]")?.getAttribute("href");
    if (base) {
      try {
        return new URL(base, fetchedUrl).href;
      } catch {
      }
    }
    return fetchedUrl;
  }
  function resolveLink(value, baseUrl) {
    if (typeof value !== "string") return null;
    const trimmed = value.trim();
    if (!trimmed || trimmed.startsWith("#")) return null;
    let url;
    try {
      url = new URL(trimmed, baseUrl);
    } catch {
      return null;
    }
    return /^https?:$/u.test(url.protocol) ? url.href : null;
  }
  function matches(el, selector) {
    try {
      return typeof el?.matches === "function" && el.matches(selector);
    } catch {
      return false;
    }
  }
  function readElementName(el) {
    const itemName = matches(el, '[itemprop="name"]') ? el : safeQuery(el, '[itemprop="name"]');
    if (itemName) {
      const content = itemName.getAttribute("content");
      const text2 = normalizeText(content != null && content.trim() ? content : itemName.textContent);
      if (text2) return text2;
    }
    const text = normalizeText(el?.textContent);
    if (text) return text;
    return normalizeText(el?.getAttribute?.("title") || el?.getAttribute?.("aria-label") || "");
  }
  function readElementHref(el) {
    if (matches(el, "a[href], link[href]")) return el.getAttribute("href");
    const anchor = safeQuery(el, "a[href]");
    if (anchor) return anchor.getAttribute("href");
    const item = safeQuery(el, '[itemprop="item"][href]');
    if (item) return item.getAttribute("href");
    const dataUrl = el?.getAttribute?.("data-url");
    return dataUrl || null;
  }
  function readBreadcrumbItems(root, selector, pageUrl) {
    const base = resolveDocumentBase(root, pageUrl);
    return safeQueryAll(root, selector).map((el) => ({ name: readElementName(el), url: resolveLink(readElementHref(el), base) })).filter((item) => item.name || item.url);
  }
  function readTitle(root, selector = "h1") {
    const el = safeQuery(root, selector || "h1");
    return el ? normalizeText(el.textContent) : "";
  }
  function toAnchor(el) {
    if (matches(el, "a[href]")) return el;
    return safeQuery(el, "a[href]");
  }
  function ownAnchorOfLi(li, valid) {
    for (const anchor of safeQueryAll(li, "a[href]")) {
      if (valid.has(anchor) && anchor.closest("li") === li) return anchor;
    }
    return null;
  }
  function headingAnchorOf(list, valid) {
    const previous = list.previousElementSibling;
    if (!previous) return null;
    if (valid.has(previous)) return previous;
    const tag = previous.tagName?.toLowerCase();
    if (tag === "li" || tag === "ul" || tag === "ol") return null;
    const anchors = safeQueryAll(previous, "a[href]").filter((anchor) => valid.has(anchor));
    return anchors.length === 1 ? anchors[0] : null;
  }
  function findDomParent(anchor, valid) {
    const ownLi = anchor.closest("li");
    let node = anchor.parentElement;
    while (node) {
      const tag = node.tagName?.toLowerCase();
      if (tag === "ul" || tag === "ol") {
        const heading = headingAnchorOf(node, valid);
        if (heading && heading !== anchor) return valid.get(heading).url;
      }
      if (tag === "li" && node !== ownLi) {
        const own = ownAnchorOfLi(node, valid);
        if (own && own !== anchor) return valid.get(own).url;
      }
      node = node.parentElement;
    }
    return null;
  }
  function readNavAnchors(root, selector, pageUrl) {
    const base = resolveDocumentBase(root, pageUrl);
    const anchors = [];
    const seen = /* @__PURE__ */ new Set();
    for (const el of safeQueryAll(root, selector)) {
      const anchor = toAnchor(el);
      if (!anchor || seen.has(anchor)) continue;
      seen.add(anchor);
      anchors.push(anchor);
    }
    const valid = /* @__PURE__ */ new Map();
    for (const anchor of anchors) {
      const url = resolveLink(anchor.getAttribute("href"), base);
      if (url) valid.set(anchor, { name: readElementName(anchor), url });
    }
    return anchors.filter((anchor) => valid.has(anchor)).map((anchor) => ({ ...valid.get(anchor), domParentUrl: findDomParent(anchor, valid) }));
  }
  function readSubcategoryLinks(root, selector, pageUrl) {
    const base = resolveDocumentBase(root, pageUrl);
    const out = [];
    const seen = /* @__PURE__ */ new Set();
    for (const el of safeQueryAll(root, selector)) {
      const anchor = toAnchor(el);
      const url = anchor ? resolveLink(anchor.getAttribute("href"), base) : null;
      if (!url || seen.has(url)) continue;
      seen.add(url);
      out.push({ name: readElementName(anchor), url });
    }
    return out;
  }
  function readNextPageUrl(root, selector, pageUrl) {
    const base = resolveDocumentBase(root, pageUrl);
    for (const el of safeQueryAll(root, selector)) {
      const href = matches(el, "a[href], link[href]") ? el.getAttribute("href") : toAnchor(el)?.getAttribute("href");
      const url = resolveLink(href, base);
      if (url) return url;
    }
    return null;
  }
  function hasPasswordField(root) {
    return !!safeQuery(root, 'input[type="password"]');
  }
  function readCards(root, cardSelector) {
    return safeQueryAll(root, cardSelector);
  }
  function readPageContext(root, config, pageUrl) {
    return {
      breadcrumbItems: config?.breadcrumbSelector ? readBreadcrumbItems(root, config.breadcrumbSelector, pageUrl) : [],
      title: readTitle(root, config?.titleSelector || "h1")
    };
  }
  var init_category_dom = __esm({
    "src/lib/category-dom.js"() {
    }
  });

  // src/lib/profiles.js
  var profiles_exports = {};
  __export(profiles_exports, {
    ALLOWED_MOUNT_POSITIONS: () => ALLOWED_MOUNT_POSITIONS,
    ALLOWED_SOURCES: () => ALLOWED_SOURCES,
    NON_CAPTURABLE_SUPPLIER_CODES: () => NON_CAPTURABLE_SUPPLIER_CODES,
    matchProfile: () => matchProfile,
    matchUrlPattern: () => matchUrlPattern,
    matchesListPage: () => matchesListPage,
    normalizeCategoryConfig: () => normalizeCategoryConfig,
    originMatchesAny: () => originMatchesAny,
    validateProfiles: () => validateProfiles
  });
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
  function matchesListPage(listPagePatterns, href) {
    return (listPagePatterns || []).some((pattern) => matchUrlPattern(pattern, href));
  }
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
  var ALLOWED_SOURCES, ALLOWED_MOUNT_POSITIONS, TXK_HTTP_PATTERN, NON_CAPTURABLE_SUPPLIER_CODES, CATEGORY_NUMBER_RULES, CATEGORY_SELECTOR_DEFAULTS, MAX_CATEGORY_SELECTOR_LENGTH, MAX_PROMOTIONAL_PATTERNS, MAX_PROMOTIONAL_PATTERN_LENGTH;
  var init_profiles = __esm({
    "src/lib/profiles.js"() {
      init_transforms();
      init_category_path();
      ALLOWED_SOURCES = /* @__PURE__ */ new Set(["attribute", "text"]);
      ALLOWED_MOUNT_POSITIONS = /* @__PURE__ */ new Set(["beforebegin", "afterbegin", "beforeend", "afterend"]);
      TXK_HTTP_PATTERN = /^http:\/\/txkorders\.inzantsales\.com(?<path>\/[^\s]*)$/i;
      NON_CAPTURABLE_SUPPLIER_CODES = /* @__PURE__ */ new Set(["200"]);
      CATEGORY_NUMBER_RULES = {
        breadcrumbSkip: { fallback: 1, min: 0, max: 5 },
        maxPages: { fallback: 20, min: 1, max: 50 },
        maxDepth: { fallback: 4, min: 1, max: 6 },
        maxCategories: { fallback: 400, min: 10, max: 2e3 },
        crawlDelayMs: { fallback: 1500, min: 500, max: 15e3 }
      };
      CATEGORY_SELECTOR_DEFAULTS = {
        breadcrumbSelector: null,
        titleSelector: "h1",
        navSelector: null,
        subcategoryLinkSelector: null,
        paginationNextSelector: 'a[rel="next"]'
      };
      MAX_CATEGORY_SELECTOR_LENGTH = 500;
      MAX_PROMOTIONAL_PATTERNS = 50;
      MAX_PROMOTIONAL_PATTERN_LENGTH = 100;
    }
  });

  // src/lib/batch.js
  var batch_exports = {};
  __export(batch_exports, {
    createBatchQueue: () => createBatchQueue
  });
  function createBatchQueue({
    flush,
    maxSize = 100,
    delayMs = 150,
    cacheTtlMs = 6e4,
    schedule = (fn) => setTimeout(fn, delayMs),
    cancel = clearTimeout
  } = {}) {
    const pending = /* @__PURE__ */ new Map();
    const cache = /* @__PURE__ */ new Map();
    let timer = null;
    let flushing = false;
    const now = () => Date.now();
    function readCache(key) {
      const c = cache.get(key);
      if (!c) return void 0;
      if (c.expiresAt <= now()) {
        cache.delete(key);
        return void 0;
      }
      return c.value;
    }
    function scheduleFlush() {
      if (timer !== null) return;
      timer = schedule(drain);
    }
    async function drain() {
      if (flushing) return;
      flushing = true;
      try {
        while (pending.size > 0) {
          const batch = [];
          for (const [key, entry] of pending) {
            if (batch.length >= maxSize) break;
            batch.push(entry);
            pending.delete(key);
          }
          let results;
          try {
            results = await flush(batch.map((e) => ({ key: e.key, item: e.item })));
          } catch (err) {
            for (const entry of batch) entry.reject(err);
            for (const entry of pending.values()) entry.reject(err);
            pending.clear();
            return;
          }
          for (const entry of batch) {
            const val = results instanceof Map ? results.get(entry.key) : results && results[entry.key];
            cache.set(entry.key, { value: val, expiresAt: now() + cacheTtlMs });
            entry.resolve(val);
          }
        }
      } finally {
        flushing = false;
        timer = null;
      }
    }
    function enqueue(key, item) {
      const cached = readCache(key);
      if (cached !== void 0) return Promise.resolve(cached);
      const existing = pending.get(key);
      if (existing) return existing.promise;
      let resolve;
      let reject;
      const promise = new Promise((res, rej) => {
        resolve = res;
        reject = rej;
      });
      pending.set(key, { key, item, resolve, reject, promise });
      scheduleFlush();
      return promise;
    }
    return {
      enqueue,
      flushNow: () => {
        if (timer !== null) {
          cancel(timer);
          timer = null;
        }
        return drain();
      },
      pendingSize: () => pending.size,
      cacheSize: () => cache.size,
      clearCache: () => cache.clear()
    };
  }
  var init_batch = __esm({
    "src/lib/batch.js"() {
    }
  });

  // src/lib/ranking.js
  var ranking_exports = {};
  __export(ranking_exports, {
    beginRankingLoad: () => beginRankingLoad,
    buildDefaultProductImageUrl: () => buildDefaultProductImageUrl,
    buildProductImageCandidates: () => buildProductImageCandidates,
    formatAverageSellingPrice: () => formatAverageSellingPrice,
    formatSalesRankBand: () => formatSalesRankBand,
    normalizeRankingDays: () => normalizeRankingDays,
    normalizeRankingPageSize: () => normalizeRankingPageSize,
    normalizeSalesRankBand: () => normalizeSalesRankBand,
    normalizeStoreOptions: () => normalizeStoreOptions,
    normalizeSupplierOptions: () => normalizeSupplierOptions,
    normalizeTopSalesPage: () => normalizeTopSalesPage,
    normalizeTopSalesRequest: () => normalizeTopSalesRequest,
    paginateRanking: () => paginateRanking,
    resolveRankingRetryTarget: () => resolveRankingRetryTarget,
    resolveRankingViewState: () => resolveRankingViewState,
    restoreRankingLoad: () => restoreRankingLoad,
    shouldPreserveManualSupplier: () => shouldPreserveManualSupplier,
    transitionRankingPagination: () => transitionRankingPagination
  });
  function normalizeStoreOptions(data) {
    const stores = data && Array.isArray(data.stores) ? data.stores : [];
    const seen = /* @__PURE__ */ new Set();
    const result = [];
    for (const store of stores) {
      if (!store || typeof store !== "object") continue;
      const code = String(store.storeCode ?? store.code ?? "").trim();
      if (!code || seen.has(code.toUpperCase())) continue;
      seen.add(code.toUpperCase());
      const name = String(store.storeName ?? store.name ?? code).trim() || code;
      result.push({ code, name });
    }
    return result;
  }
  function normalizeRankingDays(value) {
    return Number(value) === 90 ? 90 : 60;
  }
  function normalizeRankingPageSize(value) {
    const numericValue = Number(value);
    return RANKING_PAGE_SIZES.has(numericValue) ? numericValue : DEFAULT_RANKING_PAGE_SIZE;
  }
  function normalizeSalesRankBand(value) {
    const normalized = typeof value === "string" ? value.trim().toLowerCase() : "";
    return SALES_RANK_BANDS.has(normalized) ? normalized : null;
  }
  function formatSalesRankBand(value) {
    const normalized = normalizeSalesRankBand(value);
    if (!normalized) return "";
    return `TOP ${normalized.slice(4)}%`;
  }
  function transitionRankingPagination(state, action) {
    const pageSize = normalizeRankingPageSize(state?.pageSize);
    if (action?.type === "page-size") {
      return { page: 1, pageSize: normalizeRankingPageSize(action.pageSize) };
    }
    if (action?.type === "context") return { page: 1, pageSize };
    const requestedPage = Number(action?.page);
    const page = Number.isFinite(requestedPage) ? Math.max(1, Math.trunc(requestedPage)) : 1;
    return { page, pageSize };
  }
  function beginRankingLoad({
    page = 1,
    pageSize = DEFAULT_RANKING_PAGE_SIZE,
    data = null,
    legacyItems = null
  } = {}, { clear = false } = {}) {
    const requestedPage = Math.max(1, Math.trunc(Number(page)) || 1);
    const requestedPageSize = normalizeRankingPageSize(pageSize);
    const checkpoint = {
      page: Number.isInteger(data?.page) && data.page >= 1 ? data.page : requestedPage,
      // pageSize 是用户偏好；请求失败时保留新选择，旧页数据仅作为可见回退内容。
      pageSize: requestedPageSize,
      data,
      legacyItems
    };
    return {
      checkpoint,
      state: {
        page: requestedPage,
        pageSize: requestedPageSize,
        data: clear ? null : data,
        legacyItems: clear ? null : legacyItems,
        loading: true,
        error: null
      }
    };
  }
  function restoreRankingLoad(checkpoint, error) {
    return {
      page: Math.max(1, Math.trunc(Number(checkpoint?.page)) || 1),
      pageSize: normalizeRankingPageSize(checkpoint?.pageSize),
      data: checkpoint?.data ?? null,
      legacyItems: checkpoint?.legacyItems ?? null,
      loading: false,
      error: String(error && error.message || error || "")
    };
  }
  function resolveRankingRetryTarget(target, { supplierCode, days } = {}) {
    const targetSupplierCode = String(target?.supplierCode || "").trim().toUpperCase();
    const currentSupplierCode = String(supplierCode || "").trim().toUpperCase();
    const targetDays = Number(target?.days);
    const page = Number(target?.page);
    const pageSize = Number(target?.pageSize);
    if (!targetSupplierCode || targetSupplierCode !== currentSupplierCode || ![60, 90].includes(targetDays) || targetDays !== normalizeRankingDays(days) || !Number.isInteger(page) || page < 1 || normalizeRankingPageSize(pageSize) !== pageSize) {
      return null;
    }
    return { page, pageSize };
  }
  function resolveRankingViewState({
    hasSupplier,
    loading,
    error,
    totalRankedCount
  } = {}) {
    if (!hasSupplier) return "no-supplier";
    if (loading) return "loading";
    if (error) return "error";
    if (totalRankedCount === 0) return "empty";
    if (Number(totalRankedCount) > 0) return "content";
    return "idle";
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
  function normalizeSupplierOptions(profiles) {
    const seen = /* @__PURE__ */ new Set();
    const result = [];
    for (const profile of Array.isArray(profiles) ? profiles : []) {
      if (!profile || typeof profile !== "object") continue;
      const code = String(profile.supplierCode ?? "").trim();
      const key = code.toUpperCase();
      if (!code || seen.has(key)) continue;
      seen.add(key);
      const name = String(profile.displayName ?? code).trim() || code;
      result.push({ code, name });
    }
    return result;
  }
  function shouldPreserveManualSupplier({
    manualSupplierCode,
    detectedSupplierCode,
    previousDetectedSupplierCode
  }) {
    if (!String(manualSupplierCode || "").trim()) return false;
    const detectedCode = String(detectedSupplierCode || "").trim();
    const previousCode = String(previousDetectedSupplierCode || "").trim();
    return !detectedCode || detectedCode.toUpperCase() === previousCode.toUpperCase();
  }
  function paginateRanking(items, requestedPage, pageSize = DEFAULT_RANKING_PAGE_SIZE) {
    const source = Array.isArray(items) ? items : [];
    const normalizedPageSize = normalizeRankingPageSize(pageSize);
    const totalPages = Math.max(1, Math.ceil(source.length / normalizedPageSize));
    const numericPage = Number.isFinite(Number(requestedPage)) ? Math.trunc(Number(requestedPage)) : 1;
    const page = Math.min(totalPages, Math.max(1, numericPage));
    const start = (page - 1) * normalizedPageSize;
    return {
      items: source.slice(start, start + normalizedPageSize),
      page,
      totalPages,
      totalItems: source.length,
      pageSize: normalizedPageSize
    };
  }
  function normalizeTopSalesPage(raw, {
    requestedPage = 1,
    requestedPageSize = DEFAULT_RANKING_PAGE_SIZE,
    requestedSupplierCode = null,
    requestedDays = null
  } = {}) {
    if (!raw || typeof raw !== "object" || Array.isArray(raw) || !Array.isArray(raw.items)) {
      throw new Error("\u65E0\u6548\u7684\u70ED\u9500\u699C\u5206\u9875\u54CD\u5E94");
    }
    const data = raw;
    const source = data.items;
    const topPercent = data.topPercent;
    const responsePage = data.page;
    const responsePageSize = data.pageSize;
    const responseTotalPages = data.totalPages;
    const totalProductCount = data.totalProductCount;
    const expectedSupplierCode = String(requestedSupplierCode || "").trim().toUpperCase();
    const responseSupplierCode = String(data.supplierCode || "").trim().toUpperCase();
    const hasMatchingContext = (!expectedSupplierCode || responseSupplierCode === expectedSupplierCode) && (requestedDays == null || data.days === normalizeRankingDays(requestedDays));
    const isLegacy = (topPercent == null || topPercent === 10) && responsePage == null && responsePageSize == null && responseTotalPages == null;
    if (isLegacy) {
      const expectedLegacyTotal = Number.isInteger(totalProductCount) && totalProductCount >= 0 ? Math.ceil(totalProductCount * 0.1) : -1;
      const legacyTotal = data.totalRankedCount == null ? source.length : data.totalRankedCount;
      const hasValidRanks = source.every(
        (item, index) => item && typeof item === "object" && item.rank === index + 1 && (item.salesRankBand == null || item.salesRankBand === "top-10")
      );
      if (!hasMatchingContext || expectedLegacyTotal < 0 || !Number.isInteger(legacyTotal) || legacyTotal !== expectedLegacyTotal || source.length !== expectedLegacyTotal || !hasValidRanks) {
        throw new Error("\u65E0\u6548\u7684\u70ED\u9500\u699C\u5206\u9875\u54CD\u5E94");
      }
      const paged = paginateRanking(source, requestedPage, requestedPageSize);
      return {
        mode: "legacy",
        topPercent: 10,
        items: paged.items,
        totalRankedCount: paged.totalItems,
        page: paged.page,
        pageSize: paged.pageSize,
        totalPages: paged.totalPages
      };
    }
    if (topPercent !== 30 || !hasMatchingContext || !Number.isInteger(totalProductCount) || totalProductCount < 0 || !Number.isInteger(data.totalRankedCount) || data.totalRankedCount < 0 || !Number.isInteger(responsePage) || responsePage < 1 || !Number.isInteger(responsePageSize) || normalizeRankingPageSize(responsePageSize) !== responsePageSize || !Number.isInteger(responseTotalPages) || responseTotalPages < 0 || normalizeRankingPageSize(requestedPageSize) !== Number(requestedPageSize) || responsePageSize !== Number(requestedPageSize) || !Number.isInteger(Number(requestedPage)) || Number(requestedPage) < 1) {
      throw new Error("\u65E0\u6548\u7684\u70ED\u9500\u699C\u5206\u9875\u54CD\u5E94");
    }
    const totalRankedCount = data.totalRankedCount;
    const expectedTotalRankedCount = Math.ceil(totalProductCount * 0.3);
    const expectedTotalPages = totalRankedCount === 0 ? 0 : Math.ceil(totalRankedCount / responsePageSize);
    const expectedPage = expectedTotalPages === 0 ? 1 : Math.min(Number(requestedPage), expectedTotalPages);
    const expectedItemCount = totalRankedCount === 0 ? 0 : Math.min(responsePageSize, totalRankedCount - (responsePage - 1) * responsePageSize);
    const firstExpectedRank = (responsePage - 1) * responsePageSize + 1;
    const hasValidItems = source.length === expectedItemCount && source.every(
      (item, index) => item && typeof item === "object" && item.rank === firstExpectedRank + index && item.salesRankBand === (item.rank <= Math.ceil(totalProductCount * 0.1) ? "top-10" : item.rank <= Math.ceil(totalProductCount * 0.2) ? "top-20" : "top-30")
    );
    if (totalRankedCount !== expectedTotalRankedCount || responseTotalPages !== expectedTotalPages || responsePage !== expectedPage || !hasValidItems) {
      throw new Error("\u65E0\u6548\u7684\u70ED\u9500\u699C\u5206\u9875\u54CD\u5E94");
    }
    return {
      mode: "server",
      topPercent: 30,
      items: source,
      totalRankedCount,
      page: responsePage,
      pageSize: responsePageSize,
      totalPages: responseTotalPages
    };
  }
  function formatAverageSellingPrice(value) {
    if (value == null || value === "") return "\u2014";
    const numericValue = Number(value);
    if (!Number.isFinite(numericValue)) return "\u2014";
    return new Intl.NumberFormat("en-AU", {
      style: "currency",
      currency: "AUD",
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(numericValue);
  }
  function buildDefaultProductImageUrl(itemNumber, productCode) {
    const imageKey = String(itemNumber || productCode || "").trim();
    return imageKey ? `${DEFAULT_PRODUCT_IMAGE_BASE_URL}/${encodeURIComponent(imageKey)}.jpg` : "";
  }
  function toAbsoluteUrl(value, apiOrigin) {
    const raw = String(value || "").trim();
    if (!raw) return "";
    try {
      return new URL(raw, `${String(apiOrigin || "").replace(/\/$/, "")}/`).href;
    } catch {
      return "";
    }
  }
  function buildProductImageCandidates(item, apiOrigin) {
    const candidates = [
      toAbsoluteUrl(item && item.imageUrl, apiOrigin),
      buildDefaultProductImageUrl(item && item.itemNumber),
      buildDefaultProductImageUrl(item && item.productCode)
    ].filter(Boolean);
    return [...new Set(candidates)];
  }
  var DEFAULT_PRODUCT_IMAGE_BASE_URL, DEFAULT_RANKING_PAGE_SIZE, RANKING_PAGE_SIZES, SALES_RANK_BANDS;
  var init_ranking = __esm({
    "src/lib/ranking.js"() {
      DEFAULT_PRODUCT_IMAGE_BASE_URL = "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200";
      DEFAULT_RANKING_PAGE_SIZE = 50;
      RANKING_PAGE_SIZES = /* @__PURE__ */ new Set([50, 100, 200]);
      SALES_RANK_BANDS = /* @__PURE__ */ new Set(["top-10", "top-20", "top-30"]);
    }
  });

  // src/lib/dats-state.js
  var dats_state_exports = {};
  __export(dats_state_exports, {
    buildSummaryCacheKey: () => buildSummaryCacheKey,
    computeButtonState: () => computeButtonState,
    createGenerationGuard: () => createGenerationGuard,
    createNodeStateRegistry: () => createNodeStateRegistry,
    needsProcessing: () => needsProcessing,
    normalizeSummaryItem: () => normalizeSummaryItem,
    normalizeSummaryMap: () => normalizeSummaryMap,
    shouldInjectList: () => shouldInjectList
  });
  function createGenerationGuard(initial = 0) {
    let gen = initial;
    return {
      current: () => gen,
      advance: () => ++gen,
      isCurrent: (g) => g === gen
    };
  }
  function createNodeStateRegistry() {
    const map = /* @__PURE__ */ new WeakMap();
    return {
      set: (node, state) => map.set(node, state),
      get: (node) => map.get(node),
      has: (node) => map.has(node),
      delete: (node) => map.delete(node)
    };
  }
  function needsProcessing(node, registry, generation) {
    const state = registry.get(node);
    return !state || state.generation !== generation;
  }
  function shouldInjectList({ href, listPagePatterns, cardCount, isDetailPage = false }) {
    if (isDetailPage || cardCount < 1) return false;
    const patterns = Array.isArray(listPagePatterns) ? listPagePatterns : [];
    if (patterns.length > 0) {
      return matchesListPage(patterns, href);
    }
    return cardCount > 1;
  }
  function computeButtonState(item) {
    if (!item) return { kind: "none", reason: "noMatch" };
    if (item.error) return { kind: "error", reason: "error" };
    if (item.hasMatch === false) return { kind: "none", reason: "noMatch" };
    const salesRankBand = normalizeSalesRankBand(item.salesRankBand);
    const salesRankingDays = salesRankBand ? normalizeRankingDays(item.salesRankingDays) : null;
    if (item.hasPurchase === false) {
      return {
        kind: "none",
        reason: "noPurchase",
        ...salesRankBand ? { salesRankBand, salesRankingDays } : {}
      };
    }
    return {
      kind: "ok",
      lastOrderDate: item.lastOrderDate,
      lastOrderQuantity: item.lastOrderQuantity,
      salesToDate: item.salesToDate,
      ...salesRankBand ? { salesRankBand, salesRankingDays } : {}
    };
  }
  function buildSummaryCacheKey(storeCode, itemNumber, salesRankingDays) {
    const normalizedStoreCode = String(storeCode || "none").trim() || "none";
    const normalizedItemNumber = String(itemNumber || "").trim();
    return `${normalizedStoreCode}:${normalizeRankingDays(salesRankingDays)}:${normalizedItemNumber}`;
  }
  function normalizeSummaryItem(raw, { salesRankingAvailable = false } = {}) {
    if (!raw || typeof raw !== "object") return { hasMatch: false };
    if (raw.error) return { error: raw.error };
    const matchStatus = typeof raw.matchStatus === "string" ? raw.matchStatus.toLowerCase() : null;
    const hasMatch = matchStatus ? matchStatus !== "unmatched" : raw.hasMatch !== false;
    const hasPurchase = matchStatus ? matchStatus === "matched" : raw.hasPurchase != null ? raw.hasPurchase : raw.lastOrderDate != null || raw.latestPurchaseDate != null || raw.lastOrderQuantity != null || raw.latestPurchaseQuantity != null || raw.orderCount > 0;
    const normalized = {
      hasMatch,
      hasPurchase: !!hasPurchase,
      lastOrderDate: raw.latestPurchaseDate ?? raw.lastOrderDate ?? raw.lastOrderDateStr ?? null,
      lastOrderQuantity: raw.latestPurchaseQuantity ?? raw.lastOrderQuantity ?? raw.lastOrderQty ?? null,
      salesToDate: raw.salesSinceLatestPurchase ?? raw.salesToDate ?? raw.salesQty ?? null
    };
    const salesRankBand = normalizeSalesRankBand(raw.salesRankBand);
    if (salesRankingAvailable === true && hasMatch && salesRankBand) {
      normalized.salesRankBand = salesRankBand;
    }
    return normalized;
  }
  function normalizeSummaryMap(rawData) {
    const out = {};
    if (!rawData) return out;
    if (Array.isArray(rawData)) {
      for (const item of rawData) {
        if (item && item.itemNumber) out[item.itemNumber] = normalizeSummaryItem(item);
      }
      return out;
    }
    if (Array.isArray(rawData.items)) {
      const options = { salesRankingAvailable: rawData.salesRankingAvailable === true };
      for (const item of rawData.items) {
        if (item && item.itemNumber) out[item.itemNumber] = normalizeSummaryItem(item, options);
      }
      return out;
    }
    if (typeof rawData === "object") {
      for (const [k, v] of Object.entries(rawData)) {
        out[k] = normalizeSummaryItem(v);
      }
      return out;
    }
    return out;
  }
  var init_dats_state = __esm({
    "src/lib/dats-state.js"() {
      init_profiles();
      init_ranking();
    }
  });

  // src/lib/i18n.js
  var i18n_exports = {};
  __export(i18n_exports, {
    CATEGORY_ERROR_MESSAGE_KEYS: () => CATEGORY_ERROR_MESSAGE_KEYS,
    DEFAULT_LOCALE: () => DEFAULT_LOCALE,
    MESSAGES: () => MESSAGES,
    SUPPORTED_LOCALES: () => SUPPORTED_LOCALES,
    categoryErrorMessageKey: () => categoryErrorMessageKey,
    normalizeLocale: () => normalizeLocale,
    resolveInitialLocale: () => resolveInitialLocale,
    t: () => t
  });
  function categoryErrorMessageKey(code) {
    return CATEGORY_ERROR_MESSAGE_KEYS[code] || "categoryErrorGeneric";
  }
  function normalizeLocale(locale) {
    return SUPPORTED_LOCALES.includes(locale) ? locale : DEFAULT_LOCALE;
  }
  function resolveInitialLocale(storedLocale, preferredLanguages = []) {
    if (SUPPORTED_LOCALES.includes(storedLocale)) {
      return storedLocale;
    }
    const languages = Array.isArray(preferredLanguages) ? preferredLanguages : [preferredLanguages];
    const systemLocale = languages.find(
      (language) => typeof language === "string" && language.trim()
    );
    return /^zh(?:-|$)/i.test(systemLocale?.trim() || "") ? "zh" : "en";
  }
  function t(locale, key) {
    const dict = MESSAGES[locale] || MESSAGES[DEFAULT_LOCALE];
    return dict[key] != null ? dict[key] : key;
  }
  var SUPPORTED_LOCALES, DEFAULT_LOCALE, MESSAGES, CATEGORY_ERROR_MESSAGE_KEYS;
  var init_i18n = __esm({
    "src/lib/i18n.js"() {
      SUPPORTED_LOCALES = ["zh", "en"];
      DEFAULT_LOCALE = "zh";
      MESSAGES = {
        zh: {
          title: "HB \u4F9B\u5E94\u5546\u8BA2\u8D27\u52A9\u624B",
          apiTitle: "\u540E\u7AEF\u63A5\u53E3",
          apiRemote: "\u8FDC\u7AEF /",
          apiLocal: "\u672C\u5730 5002",
          apiApply: "\u5E94\u7528",
          apiPlaceholder: "https://api.example.com",
          apiHint: "\u4EC5\u4F7F\u7528\u53EF\u4FE1 HB \u63A5\u53E3\uFF1B\u8F93\u5165 / \u6062\u590D\u8FDC\u7AEF",
          apiSaved: "\u540E\u7AEF\u63A5\u53E3\u5DF2\u4FDD\u5B58",
          apiSwitched: "\u540E\u7AEF\u63A5\u53E3\u5DF2\u5207\u6362\uFF0C\u6B63\u5728\u91CD\u65B0\u68C0\u67E5\u7F51\u7AD9\u4F1A\u8BDD",
          apiInvalid: "\u8BF7\u8F93\u5165\u6709\u6548\u7684 HTTPS \u5730\u5740\u6216\u672C\u673A HTTP \u5730\u5740",
          apiPermissionDenied: "\u672A\u6388\u6743\u8BBF\u95EE\u8BE5\u63A5\u53E3\u5730\u5740",
          sessionCheckingTitle: "\u6B63\u5728\u68C0\u67E5\u7F51\u7AD9\u767B\u5F55\u72B6\u6001",
          sessionCheckingDescription: "\u5C06\u4F7F\u7528\u5F53\u524D HB SHOP \u7F51\u7AD9\u8D26\u53F7\u8FDE\u63A5\u6269\u5C55\u3002",
          sessionNeedsWebsiteTitle: "\u8BF7\u6253\u5F00\u6216\u767B\u5F55 HB SHOP",
          sessionNeedsWebsiteDescription: "\u7F51\u7AD9\u767B\u5F55\u540E\u8FD4\u56DE\u6269\u5C55\u5E76\u91CD\u65B0\u68C0\u67E5\uFF0C\u65E0\u9700\u5728\u6269\u5C55\u8F93\u5165\u5BC6\u7801\u3002",
          sessionConnectedTitle: "\u5DF2\u8FDE\u63A5\u7F51\u7AD9\u8D26\u53F7",
          openShop: "\u6253\u5F00 HB SHOP",
          recheckSession: "\u91CD\u65B0\u68C0\u67E5",
          disconnectExtension: "\u65AD\u5F00\u6269\u5C55",
          apiOriginMismatch: "\u5F53\u524D\u63A5\u53E3\u4E0E HB SHOP \u7F51\u9875\u4E0D\u540C\u6E90\uFF0C\u8BF7\u5207\u56DE\u8FDC\u7AEF\u63A5\u53E3\u6216\u5728\u5BF9\u5E94\u7F51\u9875\u73AF\u5883\u767B\u5F55\u3002",
          save: "\u4FDD\u5B58",
          store: "\u95E8\u5E97",
          storeCode: "\u95E8\u5E97\u7F16\u7801",
          supplier: "\u4F9B\u5E94\u5546",
          supplierExpand: "\u5C55\u5F00\u5DF2\u6388\u6743\uFF08{count}\uFF09",
          supplierCollapse: "\u6536\u8D77\u5DF2\u6388\u6743",
          supplierCollapsedHint: "\u5DF2\u9690\u85CF {count} \u4E2A\u5DF2\u6388\u6743\u7F51\u7AD9\uFF0C\u672A\u6388\u6743\u7F51\u7AD9\u59CB\u7EC8\u663E\u793A\u3002",
          grant: "\u6388\u6743",
          granted: "\u5DF2\u6388\u6743",
          grantSuccess: "\u6388\u6743\u6210\u529F",
          grantDenied: "\u672A\u6388\u6743",
          grantFailed: "\u6388\u6743\u5931\u8D25",
          storeSaved: "\u5DF2\u4FDD\u5B58\u95E8\u5E97",
          noPosStore: "\u6682\u65E0\u5DF2\u542F\u7528 POS \u7684\u5173\u8054\u95E8\u5E97",
          historyTab: "\u5546\u54C1\u8BB0\u5F55",
          rankingTab: "\u70ED\u9500 TOP {percent}%",
          rankingTitle: "\u4F9B\u5E94\u5546\u70ED\u9500 TOP {percent}%",
          rankingSupplier: "\u6392\u540D\u4F9B\u5E94\u5546",
          rankingChooseSupplier: "\u9009\u62E9\u4F9B\u5E94\u5546",
          rankingPeriod: "\u7EDF\u8BA1\u5468\u671F",
          days: "\u5929",
          rankingScope: "\u5168\u516C\u53F8 {stores} \u5BB6\u542F\u7528 POS \u95E8\u5E97 \xB7 {products} \u4E2A\u6709\u9500\u91CF\u5546\u54C1",
          rankingNoSupplier: "\u8BF7\u6253\u5F00\u53D7\u652F\u6301\u7684\u4F9B\u5E94\u5546\u5546\u54C1\u5217\u8868\u9875",
          rankingNoData: "\u8BE5\u4F9B\u5E94\u5546\u5728\u6240\u9009\u5468\u671F\u5185\u6682\u65E0\u9500\u552E\u6570\u636E",
          rankingPageSize: "\u6BCF\u9875",
          rankingPageSummary: "\u7B2C {page} / {totalPages} \u9875 \xB7 \u5171 {total} \u4E2A\u5546\u54C1",
          rankingLoading: "\u6B63\u5728\u52A0\u8F7D\u70ED\u9500\u699C\u2026",
          rankingLoadFailed: "\u70ED\u9500\u699C\u52A0\u8F7D\u5931\u8D25",
          rankingRetry: "\u91CD\u8BD5",
          rankingLegacyHint: "\u5F53\u524D\u670D\u52A1\u6682\u4EC5\u63D0\u4F9B TOP 10%\uFF0C\u5347\u7EA7\u540E\u53EF\u67E5\u770B TOP 30%\u3002",
          rankingPageChanged: "\u5DF2\u663E\u793A\u7B2C {page} / {totalPages} \u9875",
          storeSalesOpen: "\u67E5\u770B {product} \u7684\u5206\u5E97\u9500\u91CF",
          storeSalesBack: "\u8FD4\u56DE\u6392\u884C\u699C",
          storeSalesTitle: "\u5206\u5E97\u9500\u91CF",
          storeSalesTotal: "\u5168\u516C\u53F8\u603B\u9500\u91CF",
          storeSalesStoreCount: "{count} \u5BB6\u5206\u5E97",
          storeSalesSearch: "\u641C\u7D22\u5206\u5E97",
          storeSalesStore: "\u5206\u5E97",
          storeSalesQuantity: "\u9500\u91CF",
          storeSalesLoading: "\u6B63\u5728\u52A0\u8F7D\u5206\u5E97\u9500\u91CF\u2026",
          storeSalesLoadFailed: "\u5206\u5E97\u9500\u91CF\u52A0\u8F7D\u5931\u8D25",
          storeSalesStale: "\u6392\u884C\u699C\u6570\u636E\u5DF2\u66F4\u65B0\uFF0C\u8BF7\u8FD4\u56DE\u5237\u65B0\u540E\u518D\u67E5\u770B",
          storeSalesRefreshRanking: "\u8FD4\u56DE\u5E76\u5237\u65B0\u6392\u884C\u699C",
          storeSalesNoMatch: "\u672A\u627E\u5230\u5339\u914D\u7684\u5206\u5E97",
          storeSalesHelper: "\u6309\u9500\u91CF\u4ECE\u9AD8\u5230\u4F4E\u6392\u5217 \xB7 \u5305\u542B\u96F6\u9500\u91CF\u5206\u5E97",
          storeSalesFooter: "\u5408\u8BA1\uFF08{count} \u5BB6\u5206\u5E97\uFF09",
          copy: "\u590D\u5236",
          copied: "\u5DF2\u590D\u5236",
          averageSellingPrice: "\u5747\u4EF7",
          historyNoItem: "\u8BF7\u70B9\u51FB\u4F9B\u5E94\u5546\u5546\u54C1\u65C1\u7684\u8BB0\u5F55\u6309\u94AE\u67E5\u770B\u5546\u54C1\u5386\u53F2",
          all: "\u5168\u90E8",
          order: "\u8BA2\u8D27",
          sales: "\u9500\u552E",
          type: "\u7C7B\u578B",
          date: "\u65E5\u671F",
          orderNo: "\u5355\u53F7",
          quantity: "\u6570\u91CF",
          price: "\u5E73\u5747\u4EF7\u683C",
          page: "\u9875",
          prev: "\u4E0A\u4E00\u9875",
          next: "\u4E0B\u4E00\u9875",
          noData: "\u6682\u65E0\u6570\u636E",
          loading: "\u52A0\u8F7D\u4E2D\u2026",
          error: "\u52A0\u8F7D\u5931\u8D25",
          noMatch: "\u65E0\u5339\u914D",
          noPurchase: "\u65E0\u91C7\u8D2D",
          noStore: "\u8BF7\u5148\u9009\u62E9\u95E8\u5E97",
          lastOrder: "\u4E0A\u6B21\u8BA2\u8D27",
          salesToDate: "\u81F3\u4ECA\u9500\u91CF",
          salesRankBand: "\u8FD1 {days} \u5929\u9500\u91CF\uFF1A{band}",
          categoryTitle: "\u4F9B\u5E94\u5546\u5206\u7C7B\u91C7\u96C6",
          categoryPassiveLabel: "\u6D4F\u89C8\u5206\u7C7B\u9875\u65F6\u81EA\u52A8\u91C7\u96C6",
          categoryPassiveHint: "\u4EC5\u56DE\u4F20\u5206\u7C7B\u8DEF\u5F84\u4E0E\u9875\u9762\u4E0A\u7684\u5546\u54C1\u8D27\u53F7\u3002",
          categoryCrawlAll: "\u91C7\u96C6\u5168\u90E8\u5206\u7C7B",
          categoryAbort: "\u4E2D\u6B62",
          categoryResume: "\u7EE7\u7EED",
          categoryRetryFailed: "\u91CD\u8BD5\u5931\u8D25",
          categoryProgress: "\u5DF2\u5B8C\u6210 {done} / {total} \u4E2A\u5206\u7C7B \xB7 \u5931\u8D25 {failed}",
          categoryCurrent: "\u6B63\u5728\u91C7\u96C6\uFF1A{name}",
          categoryItemsSent: "\u5DF2\u56DE\u4F20 {items} \u4E2A\u8D27\u53F7 \xB7 {pages} \u9875",
          categoryStarting: "\u6B63\u5728\u542F\u52A8\u91C7\u96C6\u2026",
          categoryKeepTabOpen: "\u91C7\u96C6\u671F\u95F4\u8BF7\u4FDD\u6301\u8BE5\u4F9B\u5E94\u5546\u6807\u7B7E\u9875\u6253\u5F00\uFF0C\u5173\u95ED\u6216\u8DF3\u8F6C\u4F1A\u4E2D\u65AD\u91C7\u96C6\u3002",
          categorySafariHint: "iOS \u53EF\u80FD\u6302\u8D77\u540E\u53F0\u6807\u7B7E\u9875\uFF1B\u91C7\u96C6\u4E2D\u65AD\u540E\u8FD4\u56DE\u8BE5\u4F9B\u5E94\u5546\u6807\u7B7E\u9875\u70B9\u51FB\u201C\u7EE7\u7EED\u201D\u5373\u53EF\u63A5\u7740\u91C7\u96C6\u3002",
          categoryNoSupplier: "\u8BF7\u5148\u6253\u5F00\u5DF2\u6388\u6743\u7684\u4F9B\u5E94\u5546\u7F51\u7AD9\u6807\u7B7E\u9875",
          categoryNotEnabled: "\u8BE5\u4F9B\u5E94\u5546\u6682\u672A\u5F00\u542F\u5206\u7C7B\u91C7\u96C6",
          categoryCrawlUnavailable: "\u8BE5\u4F9B\u5E94\u5546\u4EC5\u652F\u6301\u6D4F\u89C8\u5206\u7C7B\u9875\u65F6\u81EA\u52A8\u91C7\u96C6",
          categoryOtherJobRunning: "{supplier} \u6B63\u5728\u91C7\u96C6\u5206\u7C7B\uFF0C\u8BF7\u7B49\u5F85\u5B8C\u6210\u6216\u5148\u4E2D\u6B62",
          categoryLastRun: "\u4E0A\u6B21\u91C7\u96C6\uFF1A{time} \xB7 {status} \xB7 {done}/{total} \u4E2A\u5206\u7C7B \xB7 \u5931\u8D25 {failed}",
          categoryNever: "\u5C1A\u672A\u4E3B\u52A8\u91C7\u96C6\u8FC7\u8BE5\u4F9B\u5E94\u5546",
          categoryStatusRunning: "\u91C7\u96C6\u4E2D",
          categoryStatusCompleted: "\u91C7\u96C6\u5B8C\u6210",
          categoryStatusAborted: "\u5DF2\u4E2D\u6B62",
          categoryStatusInterrupted: "\u5DF2\u4E2D\u65AD\uFF0C\u53EF\u70B9\u51FB\u201C\u7EE7\u7EED\u201D\u4ECE\u65AD\u70B9\u63A5\u7740\u91C7\u96C6",
          categoryStatusFailed: "\u91C7\u96C6\u5931\u8D25",
          categoryErrorSupplierTabRequired: "\u8BF7\u5148\u5207\u6362\u5230\u8BE5\u4F9B\u5E94\u5546\u7F51\u7AD9\u7684\u6807\u7B7E\u9875",
          categoryErrorContentScriptUnavailable: "\u4F9B\u5E94\u5546\u9875\u9762\u5C1A\u672A\u5C31\u7EEA\uFF0C\u8BF7\u5237\u65B0\u8BE5\u9875\u9762\u540E\u91CD\u8BD5",
          categoryErrorCrawlAlreadyRunning: "\u5DF2\u6709\u5206\u7C7B\u91C7\u96C6\u4EFB\u52A1\u5728\u8FD0\u884C",
          categoryErrorCrawlDisabled: "\u8BE5\u4F9B\u5E94\u5546\u672A\u5F00\u542F\u4E3B\u52A8\u5206\u7C7B\u91C7\u96C6",
          categoryErrorLoginRequired: "\u4F9B\u5E94\u5546\u7F51\u7AD9\u767B\u5F55\u5DF2\u5931\u6548\uFF0C\u8BF7\u5148\u767B\u5F55\u540E\u518D\u7EE7\u7EED",
          categoryErrorSiteBlocking: "\u4F9B\u5E94\u5546\u7F51\u7AD9\u8FDE\u7EED\u62D2\u7EDD\u8BBF\u95EE\uFF0C\u5DF2\u6682\u505C\u91C7\u96C6\uFF0C\u8BF7\u7A0D\u540E\u518D\u7EE7\u7EED",
          categoryErrorNavNotFound: "\u672A\u627E\u5230\u8BE5\u4F9B\u5E94\u5546\u7684\u5206\u7C7B\u5BFC\u822A",
          categoryErrorFeatureDisabled: "\u540E\u53F0\u5DF2\u6682\u505C\u5206\u7C7B\u91C7\u96C6",
          categoryErrorWebsiteSessionRequired: "\u8BF7\u5148\u8FDE\u63A5 HB SHOP \u7F51\u7AD9\u8D26\u53F7",
          categoryErrorNothingToRetry: "\u6CA1\u6709\u9700\u8981\u91CD\u8BD5\u7684\u5931\u8D25\u5206\u7C7B",
          categoryErrorGeneric: "\u5206\u7C7B\u91C7\u96C6\u51FA\u9519\uFF08{code}\uFF09"
        },
        en: {
          title: "HB Supplier Ordering Assistant",
          apiTitle: "Backend API",
          apiRemote: "Remote /",
          apiLocal: "Local 5002",
          apiApply: "Apply",
          apiPlaceholder: "https://api.example.com",
          apiHint: "Use trusted HB APIs only; enter / for remote",
          apiSaved: "Backend API saved",
          apiSwitched: "Backend API changed. Checking the website session again.",
          apiInvalid: "Enter a valid HTTPS or local HTTP origin",
          apiPermissionDenied: "Access to this API origin was not granted",
          sessionCheckingTitle: "Checking website sign-in",
          sessionCheckingDescription: "The extension will connect with the current HB SHOP website account.",
          sessionNeedsWebsiteTitle: "Open or sign in to HB SHOP",
          sessionNeedsWebsiteDescription: "After signing in on the website, return here and check again. No extension password is needed.",
          sessionConnectedTitle: "Connected to website account",
          openShop: "Open HB SHOP",
          recheckSession: "Check again",
          disconnectExtension: "Disconnect extension",
          apiOriginMismatch: "This API is not the same origin as HB SHOP. Switch back to Remote or sign in in the matching web environment.",
          save: "Save",
          store: "Store",
          storeCode: "Store code",
          supplier: "Supplier",
          supplierExpand: "Show granted ({count})",
          supplierCollapse: "Hide granted",
          supplierCollapsedHint: "{count} granted sites hidden. Sites awaiting permission always remain visible.",
          grant: "Grant",
          granted: "Granted",
          grantSuccess: "Permission granted",
          grantDenied: "Permission not granted",
          grantFailed: "Permission failed",
          storeSaved: "Store saved",
          noPosStore: "No related store has an enabled POS",
          historyTab: "Item history",
          rankingTab: "Top {percent}% sellers",
          rankingTitle: "Supplier top {percent}% sellers",
          rankingSupplier: "Ranking supplier",
          rankingChooseSupplier: "Choose supplier",
          rankingPeriod: "Period",
          days: "days",
          rankingScope: "{stores} enabled POS stores company-wide \xB7 {products} selling products",
          rankingNoSupplier: "Open a supported supplier product list",
          rankingNoData: "No sales data for this supplier in the selected period",
          rankingPageSize: "Per page",
          rankingPageSummary: "Page {page} of {totalPages} \xB7 {total} products",
          rankingLoading: "Loading ranking\u2026",
          rankingLoadFailed: "Could not load the ranking",
          rankingRetry: "Retry",
          rankingLegacyHint: "This service currently provides TOP 10% only. Upgrade it to view TOP 30%.",
          rankingPageChanged: "Showing page {page} of {totalPages}",
          storeSalesOpen: "View store sales for {product}",
          storeSalesBack: "Back to ranking",
          storeSalesTitle: "Store sales",
          storeSalesTotal: "Company total sales",
          storeSalesStoreCount: "{count} stores",
          storeSalesSearch: "Search stores",
          storeSalesStore: "Store",
          storeSalesQuantity: "Sales",
          storeSalesLoading: "Loading store sales\u2026",
          storeSalesLoadFailed: "Could not load store sales",
          storeSalesStale: "The ranking has changed. Refresh it before viewing store sales.",
          storeSalesRefreshRanking: "Back and refresh ranking",
          storeSalesNoMatch: "No matching stores",
          storeSalesHelper: "Sorted by sales \xB7 Includes zero-sales stores",
          storeSalesFooter: "Total ({count} stores)",
          copy: "Copy",
          copied: "Copied",
          averageSellingPrice: "Avg price",
          historyNoItem: "Select the history button beside a supplier product to view item history",
          all: "All",
          order: "Order",
          sales: "Sales",
          type: "Type",
          date: "Date",
          orderNo: "Reference no.",
          quantity: "Qty",
          price: "Average price",
          page: "Page",
          prev: "Prev",
          next: "Next",
          noData: "No data",
          loading: "Loading\u2026",
          error: "Load failed",
          noMatch: "No match",
          noPurchase: "No purchase",
          noStore: "Select a store first",
          lastOrder: "Last order",
          salesToDate: "Sales to date",
          salesRankBand: "Sales in the last {days} days: {band}",
          categoryTitle: "Supplier categories",
          categoryPassiveLabel: "Capture while browsing category pages",
          categoryPassiveHint: "Sends only the category path and the item numbers shown on the page.",
          categoryCrawlAll: "Capture all categories",
          categoryAbort: "Stop",
          categoryResume: "Continue",
          categoryRetryFailed: "Retry failed",
          categoryProgress: "{done} of {total} categories done \xB7 {failed} failed",
          categoryCurrent: "Capturing: {name}",
          categoryItemsSent: "{items} item numbers sent \xB7 {pages} pages",
          categoryStarting: "Starting capture\u2026",
          categoryKeepTabOpen: "Keep the supplier tab open while capturing. Closing or leaving the page interrupts the capture.",
          categorySafariHint: "iOS may suspend background tabs. If the capture is interrupted, return to the supplier tab and tap Continue.",
          categoryNoSupplier: "Open an authorised supplier website tab first",
          categoryNotEnabled: "Category capture is not enabled for this supplier",
          categoryCrawlUnavailable: "This supplier supports capture only while browsing category pages",
          categoryOtherJobRunning: "{supplier} is capturing categories. Wait for it to finish or stop it first.",
          categoryLastRun: "Last capture: {time} \xB7 {status} \xB7 {done}/{total} categories \xB7 {failed} failed",
          categoryNever: "No full capture has been run for this supplier yet",
          categoryStatusRunning: "Capturing",
          categoryStatusCompleted: "Completed",
          categoryStatusAborted: "Stopped",
          categoryStatusInterrupted: "Interrupted. Tap Continue to resume from where it stopped.",
          categoryStatusFailed: "Failed",
          categoryErrorSupplierTabRequired: "Switch to a tab showing this supplier website first",
          categoryErrorContentScriptUnavailable: "The supplier page is not ready. Reload the page and try again.",
          categoryErrorCrawlAlreadyRunning: "A category capture is already running",
          categoryErrorCrawlDisabled: "Full category capture is not enabled for this supplier",
          categoryErrorLoginRequired: "The supplier website sign-in has expired. Sign in, then continue.",
          categoryErrorSiteBlocking: "The supplier website kept refusing requests, so capture was paused. Try again later.",
          categoryErrorNavNotFound: "The supplier category menu could not be found",
          categoryErrorFeatureDisabled: "Category capture is paused by HB",
          categoryErrorWebsiteSessionRequired: "Connect your HB SHOP website account first",
          categoryErrorNothingToRetry: "There are no failed categories to retry",
          categoryErrorGeneric: "Category capture error ({code})"
        }
      };
      CATEGORY_ERROR_MESSAGE_KEYS = Object.freeze({
        SUPPLIER_TAB_REQUIRED: "categoryErrorSupplierTabRequired",
        CONTENT_SCRIPT_UNAVAILABLE: "categoryErrorContentScriptUnavailable",
        CRAWL_ALREADY_RUNNING: "categoryErrorCrawlAlreadyRunning",
        CRAWL_DISABLED: "categoryErrorCrawlDisabled",
        LOGIN_REQUIRED: "categoryErrorLoginRequired",
        SITE_BLOCKING: "categoryErrorSiteBlocking",
        NAV_NOT_FOUND: "categoryErrorNavNotFound",
        FEATURE_DISABLED: "categoryErrorFeatureDisabled",
        CATEGORY_CAPTURE_DISABLED: "categoryErrorCrawlDisabled",
        WEBSITE_SESSION_REQUIRED: "categoryErrorWebsiteSessionRequired",
        WEBSITE_TAB_REQUIRED: "categoryErrorWebsiteSessionRequired",
        NOTHING_TO_RETRY: "categoryErrorNothingToRetry"
      });
    }
  });

  // src/lib/list-recovery.js
  var list_recovery_exports = {};
  __export(list_recovery_exports, {
    markSummaryRequestFailed: () => markSummaryRequestFailed,
    needsHostRemount: () => needsHostRemount,
    resetSummaryRetry: () => resetSummaryRetry,
    shouldRequestVisibleSummary: () => shouldRequestVisibleSummary
  });
  function needsHostRemount(entry) {
    return !entry?.host?.isConnected;
  }
  function resetSummaryRetry(entry) {
    entry.retryCount = 0;
    entry.nextRetryAt = 0;
  }
  function markSummaryRequestFailed(entry, now = Date.now()) {
    const retryCount = (entry.retryCount || 0) + 1;
    const retryable = retryCount <= MAX_SUMMARY_RETRIES;
    const state = { kind: "error", reason: "error", retryable };
    entry.requested = false;
    entry.retryCount = retryCount;
    entry.nextRetryAt = retryable ? now + SUMMARY_RETRY_BASE_MS * 2 ** (retryCount - 1) : 0;
    entry.state = state;
    return state;
  }
  function shouldRequestVisibleSummary(entry, now = Date.now()) {
    if (!entry || !entry.isVisible || entry.requested) return false;
    if (entry.state?.kind === "loading") return true;
    return entry.state?.kind === "error" && entry.state.retryable === true && (entry.nextRetryAt || 0) <= now;
  }
  var MAX_SUMMARY_RETRIES, SUMMARY_RETRY_BASE_MS;
  var init_list_recovery = __esm({
    "src/lib/list-recovery.js"() {
      MAX_SUMMARY_RETRIES = 3;
      SUMMARY_RETRY_BASE_MS = 2e3;
    }
  });

  // src/lib/storage-compat.js
  var storage_compat_exports = {};
  __export(storage_compat_exports, {
    getPendingLocateChange: () => getPendingLocateChange,
    matchesStorageArea: () => matchesStorageArea
  });
  function matchesStorageArea(areaName, expectedArea) {
    return areaName === void 0 || areaName === expectedArea;
  }
  function getPendingLocateChange(changes, areaName) {
    if (!matchesStorageArea(areaName, "session")) return null;
    return changes?.pendingLocate?.newValue ?? null;
  }
  var init_storage_compat = __esm({
    "src/lib/storage-compat.js"() {
    }
  });

  // src/content/list.js
  (async () => {
    const categoryModulesPromise = Promise.all([
      Promise.resolve().then(() => (init_category_path(), category_path_exports)),
      Promise.resolve().then(() => (init_category_capture(), category_capture_exports)),
      Promise.resolve().then(() => (init_category_crawl(), category_crawl_exports)),
      Promise.resolve().then(() => (init_category_dom(), category_dom_exports))
    ]).then(([path, capture, crawl, dom]) => ({ path, capture, crawl, dom })).catch(() => null);
    const [
      profilesMod,
      batchMod,
      itemNumberMod,
      stateMod,
      i18nMod,
      recoveryMod,
      storageCompatMod,
      rankingMod
    ] = await Promise.all([
      Promise.resolve().then(() => (init_profiles(), profiles_exports)),
      Promise.resolve().then(() => (init_batch(), batch_exports)),
      Promise.resolve().then(() => (init_item_number(), item_number_exports)),
      Promise.resolve().then(() => (init_dats_state(), dats_state_exports)),
      Promise.resolve().then(() => (init_i18n(), i18n_exports)),
      Promise.resolve().then(() => (init_list_recovery(), list_recovery_exports)),
      Promise.resolve().then(() => (init_storage_compat(), storage_compat_exports)),
      Promise.resolve().then(() => (init_ranking(), ranking_exports))
    ]);
    const { matchProfile: matchProfile2, normalizeCategoryConfig: normalizeCategoryConfig2 } = profilesMod;
    const { createBatchQueue: createBatchQueue2 } = batchMod;
    const { readItemNumberFrom: readItemNumberFrom2, readItemNumbersFromCards: readItemNumbersFromCards2 } = itemNumberMod;
    const {
      createGenerationGuard: createGenerationGuard2,
      createNodeStateRegistry: createNodeStateRegistry2,
      shouldInjectList: shouldInjectList2,
      computeButtonState: computeButtonState2,
      buildSummaryCacheKey: buildSummaryCacheKey2,
      normalizeSummaryMap: normalizeSummaryMap2
    } = stateMod;
    const { normalizeLocale: normalizeLocale2, t: t2 } = i18nMod;
    const {
      markSummaryRequestFailed: markSummaryRequestFailed2,
      needsHostRemount: needsHostRemount2,
      resetSummaryRetry: resetSummaryRetry2,
      shouldRequestVisibleSummary: shouldRequestVisibleSummary2
    } = recoveryMod;
    const { matchesStorageArea: matchesStorageArea2 } = storageCompatMod;
    const { formatSalesRankBand: formatSalesRankBand2, normalizeRankingDays: normalizeRankingDays2 } = rankingMod;
    const origin = location.origin;
    const stored = await chrome.storage.local.get([
      "supplierProfiles",
      "selectedStoreCode",
      "locale",
      "salesRankingDays",
      "categoryCaptureSettings"
    ]);
    const { supplierProfiles } = stored;
    let selectedStoreCode = stored.selectedStoreCode || null;
    let locale = normalizeLocale2(stored.locale);
    let salesRankingDays = normalizeRankingDays2(stored.salesRankingDays);
    const profiles = supplierProfiles && supplierProfiles.profiles || [];
    const profile = matchProfile2(profiles, { origin, pathname: location.pathname });
    if (!profile) return;
    function formatMessage(key, values = {}) {
      return Object.entries(values).reduce(
        (message, [name, value]) => message.replaceAll(`{${name}}`, String(value)),
        t2(locale, key)
      );
    }
    const cardSelector = profile.cardSelector;
    const itemCfg = profile.itemNumber;
    const mountSelector = profile.mountSelector;
    const mountPosition = profile.mountPosition;
    try {
      document.querySelector(cardSelector);
      if (itemCfg.selector) document.querySelector(itemCfg.selector);
      if (mountSelector) document.querySelector(mountSelector);
    } catch {
      return;
    }
    const generation = createGenerationGuard2(0);
    const registry = createNodeStateRegistry2();
    const trackedCards = /* @__PURE__ */ new Set();
    let active = true;
    let cardObserver = null;
    let visibilityObserver = null;
    let scanTimer = null;
    let scanInterval = null;
    let gfaLayoutStyle = null;
    function readItemNumber(card) {
      return readItemNumberFrom2(card, itemCfg);
    }
    const categoryModules = await categoryModulesPromise;
    const CATEGORY_SELECTOR_FIELDS = [
      "breadcrumbSelector",
      "titleSelector",
      "navSelector",
      "subcategoryLinkSelector",
      "paginationNextSelector"
    ];
    const MAX_CATEGORY_HTML_LENGTH = 5 * 1024 * 1024;
    let categoryConfig = null;
    let categorySettings = stored.categoryCaptureSettings || {};
    let passiveCapture = null;
    let crawlSession = null;
    function resolveCategoryConfig(sourceProfile) {
      try {
        if (!categoryModules || !sourceProfile) return null;
        const { config } = normalizeCategoryConfig2(sourceProfile.category, sourceProfile);
        if (!config.enabled) return null;
        const probed = { ...config };
        for (const field of CATEGORY_SELECTOR_FIELDS) {
          if (probed[field]) probed[field] = categoryModules.dom.probeSelector(document, probed[field]);
        }
        if (!probed.titleSelector) probed.titleSelector = "h1";
        return probed;
      } catch {
        return null;
      }
    }
    function isPassiveCaptureEnabled() {
      return !!categoryConfig?.enabled && categoryConfig.passiveEnabled && categorySettings?.[profile.supplierCode]?.passiveEnabled !== false;
    }
    function isCrawlEnabled() {
      return !!categoryConfig?.enabled && categoryConfig.crawlEnabled;
    }
    function abortableSleep(ms, signal) {
      return new Promise((resolve) => {
        if (signal?.aborted) {
          resolve();
          return;
        }
        const timer = setTimeout(done, Math.max(0, ms));
        function done() {
          clearTimeout(timer);
          signal?.removeEventListener("abort", done);
          resolve();
        }
        signal?.addEventListener("abort", done, { once: true });
      });
    }
    async function sendCategoryMessage(message) {
      try {
        const response = await chrome.runtime.sendMessage(message);
        return response || { ok: false, networkError: true };
      } catch (error) {
        return { ok: false, networkError: true, error: String(error?.message || error) };
      }
    }
    function rebuildPassiveCapture() {
      try {
        passiveCapture?.dispose();
        passiveCapture = null;
        if (!active || !categoryModules || !isPassiveCaptureEnabled()) return;
        passiveCapture = categoryModules.capture.createPassiveCaptureController({
          supplierCode: profile.supplierCode,
          getConfig: () => isPassiveCaptureEnabled() ? categoryConfig : null,
          readPageContext: () => categoryModules.dom.readPageContext(document, categoryConfig, location.href),
          getCurrentHref: () => location.href,
          sendCapture: (payload) => sendCategoryMessage({ type: "CATEGORY_CAPTURE", payload }),
          sleep: abortableSleep
        });
      } catch {
        passiveCapture = null;
      }
    }
    function notifyCategoryCapture(cards) {
      if (!passiveCapture) return;
      try {
        const itemNumbers = [];
        for (const card of cards) {
          const entry = registry.get(card);
          if (entry?.itemNumber) itemNumbers.push(entry.itemNumber);
        }
        passiveCapture.notify({ href: location.href, itemNumbers });
      } catch {
      }
    }
    function resetCategoryCapture() {
      try {
        passiveCapture?.reset();
      } catch {
      }
    }
    async function fetchCategoryHtml(url, { signal } = {}) {
      const target = new URL(url, location.href);
      if (target.origin !== location.origin) return { ok: false, status: 0, finalUrl: target.href };
      const response = await fetch(target.href, {
        method: "GET",
        credentials: "include",
        redirect: "follow",
        signal,
        headers: { Accept: "text/html,application/xhtml+xml" }
      });
      const contentType = response.headers.get("content-type") || "";
      let html = "";
      if (response.ok && /html|xml/iu.test(contentType)) {
        html = await response.text();
        if (html.length > MAX_CATEGORY_HTML_LENGTH) html = "";
      }
      return {
        ok: response.ok && !!html,
        status: response.status,
        html,
        finalUrl: response.url || target.href,
        retryAfter: response.headers.get("Retry-After")
      };
    }
    function parseCrawlPage(html, pageUrl, config) {
      const { dom } = categoryModules;
      const doc = dom.parseHtml(html);
      return {
        breadcrumbItems: config.breadcrumbSelector ? dom.readBreadcrumbItems(doc, config.breadcrumbSelector, pageUrl) : [],
        title: dom.readTitle(doc, config.titleSelector),
        itemNumbers: readItemNumbersFromCards2(dom.readCards(doc, cardSelector), itemCfg),
        nextUrl: config.paginationNextSelector ? dom.readNextPageUrl(doc, config.paginationNextSelector, pageUrl) : null,
        subcategoryLinks: config.subcategoryLinkSelector ? dom.readSubcategoryLinks(doc, config.subcategoryLinkSelector, pageUrl) : [],
        hasPasswordField: dom.hasPasswordField(doc)
      };
    }
    async function loadCategoryNavigation(config, signal) {
      const { dom, crawl } = categoryModules;
      if (config.navSelector && document.querySelector(config.navSelector)) {
        const anchors = dom.readNavAnchors(document, config.navSelector, location.href);
        if (anchors.length > 0) return { anchors, sourceUrl: location.href };
      }
      if (config.navSelector && config.navRootUrl) {
        const target = new URL(config.navRootUrl, location.href);
        if (target.origin !== location.origin) return { errorCode: "NAV_NOT_FOUND" };
        let response;
        try {
          response = await fetchCategoryHtml(target.href, { signal });
        } catch {
          return { errorCode: signal?.aborted ? "ABORTED" : "NAV_NOT_FOUND" };
        }
        if (crawl.detectLoginPage({ requestedUrl: target.href, finalUrl: response.finalUrl })) {
          return { errorCode: "LOGIN_REQUIRED" };
        }
        if (response.status === 401) return { errorCode: "LOGIN_REQUIRED" };
        if (response.ok) {
          const doc = dom.parseHtml(response.html);
          const anchors = dom.readNavAnchors(doc, config.navSelector, response.finalUrl);
          if (anchors.length > 0) return { anchors, sourceUrl: response.finalUrl };
          if (dom.hasPasswordField(doc)) return { errorCode: "LOGIN_REQUIRED" };
        }
      }
      if (config.subcategoryLinkSelector) {
        const anchors = dom.readSubcategoryLinks(document, config.subcategoryLinkSelector, location.href);
        if (anchors.length > 0) return { anchors, sourceUrl: location.href };
      }
      return { errorCode: "NAV_NOT_FOUND" };
    }
    async function sendTreeSnapshot(jobId, sourceUrl, nodes, signal) {
      const { capture, crawl } = categoryModules;
      const payload = {
        supplierCode: profile.supplierCode,
        sourceUrl,
        nodes: crawl.toTreeSnapshotNodes(nodes)
      };
      if (payload.nodes.length === 0) return { ok: true, skipped: true };
      return capture.runWithRetry(
        () => sendCategoryMessage({ type: "CATEGORY_TREE_SNAPSHOT", jobId, payload }),
        { sleep: abortableSleep, signal }
      );
    }
    async function runCategoryCrawl({ jobId, completedKeys, onlyNodes }, controller) {
      const { crawl } = categoryModules;
      const config = categoryConfig;
      const signal = controller.signal;
      const report = (progress) => {
        void sendCategoryMessage({
          type: "CATEGORY_CRAWL_PROGRESS",
          jobId,
          progress: { ...progress, jobId }
        });
      };
      const fail = (errorCode) => report({ status: "failed", errorCode });
      try {
        const nav = await loadCategoryNavigation(config, signal);
        if (signal.aborted) {
          report({ status: "aborted" });
          return;
        }
        const retryOnly = Array.isArray(onlyNodes) && onlyNodes.length > 0;
        if (nav.errorCode && (!retryOnly || nav.errorCode === "LOGIN_REQUIRED")) {
          fail(nav.errorCode);
          return;
        }
        const tree = nav.anchors ? crawl.buildNavTree(nav.anchors, { config, origin: location.origin }) : { nodes: [] };
        if (tree.nodes.length === 0 && !retryOnly) {
          fail("NAV_NOT_FOUND");
          return;
        }
        if (!retryOnly) {
          const snapshot = await sendTreeSnapshot(jobId, nav.sourceUrl, tree.nodes, signal);
          if (!snapshot.ok && snapshot.classification?.fatal) {
            fail(snapshot.classification.code);
            return;
          }
        }
        const runner = crawl.createCrawlRunner({
          config,
          origin: location.origin,
          supplierCode: profile.supplierCode,
          fetchHtml: fetchCategoryHtml,
          parsePage: (html, pageUrl) => parseCrawlPage(html, pageUrl, config),
          onCapture: (payload) => sendCategoryMessage({ type: "CATEGORY_CAPTURE", jobId, payload }),
          onProgress: report,
          sleep: abortableSleep,
          signal
        });
        const result = await runner.run({
          nodes: tree.nodes,
          completedKeys: Array.isArray(completedKeys) ? completedKeys : [],
          onlyNodes: retryOnly ? onlyNodes : null
        });
        if (!retryOnly && result.discovered.length > 0 && !signal.aborted) {
          await sendTreeSnapshot(jobId, nav.sourceUrl, [...tree.nodes, ...result.discovered], signal);
        }
      } catch {
        if (signal.aborted) report({ status: "aborted" });
        else fail("CRAWL_FAILED");
      }
    }
    function startCategoryCrawl(message) {
      if (!active || !categoryModules) return { ok: false, errorCode: "CONTENT_SCRIPT_UNAVAILABLE" };
      if (message.supplierCode !== profile.supplierCode || !isCrawlEnabled()) {
        return { ok: false, errorCode: "CRAWL_DISABLED" };
      }
      if (crawlSession) return { ok: false, errorCode: "CRAWL_ALREADY_RUNNING" };
      if (typeof message.jobId !== "string" || !message.jobId) return { ok: false, errorCode: "INVALID_JOB" };
      const controller = new AbortController();
      crawlSession = { jobId: message.jobId, controller };
      void runCategoryCrawl(message, controller).catch(() => void 0).finally(() => {
        if (crawlSession?.controller === controller) crawlSession = null;
      });
      return { ok: true, accepted: true };
    }
    function handleCategoryPageHide() {
      if (!crawlSession) return;
      const { jobId, controller } = crawlSession;
      try {
        void chrome.runtime.sendMessage({
          type: "CATEGORY_CRAWL_PROGRESS",
          jobId,
          progress: { jobId, status: "interrupted" }
        }).catch(() => void 0);
      } catch {
      }
      controller.abort();
    }
    categoryConfig = resolveCategoryConfig(profile);
    rebuildPassiveCapture();
    if (categoryModules) {
      chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
        const type = message && message.type;
        if (type !== "CATEGORY_CRAWL_RUN" && type !== "CATEGORY_CRAWL_STOP") return false;
        if (sender?.id !== chrome.runtime.id || sender?.tab) {
          sendResponse({ ok: false, errorCode: "FORBIDDEN" });
          return false;
        }
        try {
          if (type === "CATEGORY_CRAWL_STOP") {
            if (crawlSession && (!message.jobId || message.jobId === crawlSession.jobId)) {
              crawlSession.controller.abort();
            }
            sendResponse({ ok: true });
            return false;
          }
          sendResponse(startCategoryCrawl(message));
        } catch {
          sendResponse({ ok: false, errorCode: "CONTENT_SCRIPT_UNAVAILABLE" });
        }
        return false;
      });
      window.addEventListener("pagehide", handleCategoryPageHide);
    }
    function ensureGfaLayoutStyle() {
      if (gfaLayoutStyle?.isConnected) return;
      const existing = document.querySelector("style[data-hb-sro-gfa-layout]");
      if (existing) {
        gfaLayoutStyle = existing;
        return;
      }
      gfaLayoutStyle = document.createElement("style");
      gfaLayoutStyle.setAttribute("data-hb-sro-gfa-layout", "");
      gfaLayoutStyle.textContent = `
.list-row[data-product]:has(> .content > [data-hb-sro-host]) > .content {
  height: auto !important;
  min-height: 100px;
}
.list-row[data-product]:has(> .content > [data-hb-sro-host]) > .content > a[href*="/product/view?id="] > .list-content {
  height: auto !important;
}
.list-row[data-product]:has(> .content > [data-hb-sro-host]) > .content > a[href*="/product/view?id="] > .list-content .list-detail {
  height: auto !important;
}
@media (max-width: 500px) {
  .list-row[data-product]:has(> .content > [data-hb-sro-host]) > .content {
    padding-bottom: 46px !important;
  }
  .list-row[data-product] > .content > [data-hb-sro-host] {
    margin-right: 0 !important;
  }
}`;
      (document.head || document.documentElement).appendChild(gfaLayoutStyle);
    }
    function mountHost(card) {
      let mountEl = card;
      let pos = "beforeend";
      if (mountSelector) {
        const found = card.querySelector(mountSelector);
        if (found) {
          mountEl = found;
          pos = mountPosition || "afterend";
        }
      }
      const host = document.createElement("div");
      host.setAttribute("data-hb-sro-host", "");
      const isGfaFixedHeightRow = profile.supplierCode === "236" && card.matches(".list-row[data-product]");
      if (isGfaFixedHeightRow) ensureGfaLayoutStyle();
      host.style.cssText = isGfaFixedHeightRow ? "display:block;margin:4px 235px 0 0;position:relative;z-index:2;pointer-events:none;" : "display:block;margin:4px 0;";
      mountEl.insertAdjacentElement(pos, host);
      return host;
    }
    function createShadowButton(host) {
      const root = host.attachShadow({ mode: "closed" });
      const style = document.createElement("style");
      style.textContent = [
        ".hb-btn{all:unset;box-sizing:border-box;display:inline-block;max-width:100%;padding:4px 8px;border-radius:4px;border:1px solid #d5d5d5;background:#fafafa;color:#333;cursor:pointer;font:12px/1.5 system-ui,sans-serif;white-space:normal;overflow-wrap:anywhere;pointer-events:auto;}",
        ".hb-btn:focus-visible{outline:2px solid #2563eb;outline-offset:2px;}",
        ".hb-order{color:#c62828;font-weight:600;}",
        ".hb-sales{color:#1565c0;font-weight:600;}",
        ".hb-muted{color:#757575;}",
        ".hb-rank-line{display:block;width:max-content;max-width:100%;box-sizing:border-box;margin-top:2px;padding:1px 6px;border:1px solid #b8d8ff;border-radius:999px;background:#eaf3ff;color:#1565c0;font-size:10px;font-weight:700;line-height:1.5;overflow-wrap:anywhere;white-space:normal;}",
        ".hb-rank-line-top-20{border-color:#c7e3ca;background:#eef7ef;color:#2e7d32;}",
        ".hb-rank-line-top-30{border-color:#ddd0ef;background:#f5f1fb;color:#6f3cc3;}"
      ].join("");
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "hb-btn";
      root.appendChild(style);
      root.appendChild(btn);
      return btn;
    }
    function renderButton(entry, state) {
      const btn = entry.btn;
      btn.replaceChildren();
      if (state.kind === "loading") {
        btn.textContent = t2(locale, "loading");
      } else if (state.kind === "none" || state.kind === "error") {
        const span = document.createElement("span");
        span.className = "hb-muted";
        span.textContent = shortStatus(state);
        btn.appendChild(span);
      } else if (state.kind === "noStore") {
        const span = document.createElement("span");
        span.className = "hb-muted";
        span.textContent = t2(locale, "noStore");
        btn.appendChild(span);
      } else {
        const order = document.createElement("span");
        order.className = "hb-order";
        order.textContent = `${t2(locale, "lastOrder")} ${state.lastOrderDate || "\u2014"} \xD7 ${state.lastOrderQuantity ?? 0}`;
        const sales = document.createElement("span");
        sales.className = "hb-sales";
        sales.textContent = `${t2(locale, "salesToDate")} ${state.salesToDate ?? 0}`;
        btn.appendChild(order);
        btn.appendChild(document.createTextNode(" \xB7 "));
        btn.appendChild(sales);
      }
      const rankLabel = formatSalesRankBand2(state.salesRankBand);
      if ((state.kind === "ok" || state.reason === "noPurchase") && rankLabel) {
        const rankLine = document.createElement("span");
        rankLine.className = "hb-rank-line";
        rankLine.classList.add(`hb-rank-line-${state.salesRankBand}`);
        rankLine.textContent = formatMessage("salesRankBand", {
          days: state.salesRankingDays,
          band: rankLabel
        });
        btn.appendChild(rankLine);
      }
    }
    function shortStatus(state) {
      if (state.kind === "error") return t2(locale, "error");
      if (state.reason === "noPurchase") return t2(locale, "noPurchase");
      return t2(locale, "noMatch");
    }
    function requestSummary(entry) {
      if (!active || entry.requested) return;
      if (entry.state?.kind === "loading") resetSummaryRetry2(entry);
      entry.requested = true;
      const requestedGeneration = entry.generation;
      const requestedItemNumber = entry.itemNumber;
      const requestedCard = entry.card;
      const requestedRankingDays = salesRankingDays;
      batch.enqueue(
        buildSummaryCacheKey2(selectedStoreCode, requestedItemNumber, salesRankingDays),
        requestedItemNumber
      ).then((summary) => {
        if (!active || !generation.isCurrent(requestedGeneration) || registry.get(requestedCard) !== entry || entry.itemNumber !== requestedItemNumber || !requestedCard.isConnected) {
          return;
        }
        const state = summary && summary.storeMissing ? { kind: "noStore" } : computeButtonState2({ ...summary, salesRankingDays: requestedRankingDays });
        resetSummaryRetry2(entry);
        entry.state = state;
        renderButton(entry, state);
      }).catch(() => {
        if (!active || !generation.isCurrent(requestedGeneration) || registry.get(requestedCard) !== entry || entry.itemNumber !== requestedItemNumber) {
          return;
        }
        const state = markSummaryRequestFailed2(entry);
        renderButton(entry, state);
      });
    }
    function createSummaryBatch(storeCode, rankingDays) {
      return createBatchQueue2({
        maxSize: 100,
        delayMs: 150,
        cacheTtlMs: 6e4,
        flush: async (entries) => {
          if (!storeCode) {
            const out2 = {};
            for (const e of entries) out2[e.key] = { storeMissing: true };
            return out2;
          }
          const itemNumbers = entries.map((e) => e.item);
          const resp = await chrome.runtime.sendMessage({
            type: "SUMMARY_BATCH",
            storeCode,
            supplierCode: profile.supplierCode,
            itemNumbers,
            salesRankingDays: rankingDays
          });
          if (!resp || !resp.ok) {
            throw new Error(resp && resp.error || "summary request failed");
          }
          const map = normalizeSummaryMap2(resp && resp.data);
          const out = {};
          for (const e of entries) out[e.key] = map[e.item] || { hasMatch: false };
          return out;
        }
      });
    }
    let batch = createSummaryBatch(selectedStoreCode, salesRankingDays);
    function attachEntryButton(entry) {
      entry.host?.remove();
      entry.card.querySelector("[data-hb-sro-host]")?.remove();
      entry.host = mountHost(entry.card);
      entry.btn = createShadowButton(entry.host);
      entry.btn.addEventListener("click", () => {
        chrome.runtime.sendMessage({
          type: "LOCATE_ITEM",
          storeCode: selectedStoreCode || null,
          supplierCode: profile.supplierCode,
          itemNumber: entry.itemNumber
        });
      });
    }
    function ensureCard(card) {
      const itemNumber = readItemNumber(card);
      const existing = registry.get(card);
      if (!itemNumber) {
        if (existing) {
          visibilityObserver?.unobserve(card);
          existing.host?.remove();
          registry.delete(card);
          trackedCards.delete(card);
        }
        return null;
      }
      let entry = existing;
      if (!entry) {
        entry = {
          generation: generation.current(),
          card,
          itemNumber,
          host: null,
          btn: null,
          state: { kind: "loading" },
          requested: false,
          isVisible: false
        };
        attachEntryButton(entry);
        registry.set(card, entry);
        trackedCards.add(card);
        if (visibilityObserver) visibilityObserver.observe(card);
      } else {
        entry.generation = generation.current();
        if (needsHostRemount2(entry)) attachEntryButton(entry);
        if (entry.itemNumber !== itemNumber) {
          entry.itemNumber = itemNumber;
          entry.requested = false;
          entry.state = { kind: "loading" };
          if (entry.isVisible) requestSummary(entry);
        }
      }
      renderButton(entry, entry.state);
      if (shouldRequestVisibleSummary2(entry)) requestSummary(entry);
      return entry;
    }
    function scan() {
      if (!active) return;
      for (const card of trackedCards) {
        if (card.isConnected) continue;
        visibilityObserver?.unobserve(card);
        registry.delete(card);
        trackedCards.delete(card);
      }
      const cards = Array.from(document.querySelectorAll(cardSelector));
      const pageEligible = shouldInjectList2({
        href: location.href,
        listPagePatterns: profile.listPagePatterns,
        cardCount: cards.length,
        isDetailPage: document.body.classList.contains("catalog-product-view") || document.body.classList.contains("page-ProductDetail") || !!document.querySelector('.product-info-main, [data-role="product-info-main"]')
      });
      if (!pageEligible) {
        for (const card of trackedCards) {
          const entry = registry.get(card);
          visibilityObserver?.unobserve(card);
          entry?.host?.remove();
          registry.delete(card);
        }
        trackedCards.clear();
        resetCategoryCapture();
        return;
      }
      for (const card of cards) {
        ensureCard(card);
      }
      notifyCategoryCapture(cards);
    }
    visibilityObserver = new IntersectionObserver(
      (entries) => {
        for (const e of entries) {
          const entry = registry.get(e.target);
          if (!entry) continue;
          entry.isVisible = e.isIntersecting;
          if (e.isIntersecting && shouldRequestVisibleSummary2(entry)) requestSummary(entry);
        }
      },
      { rootMargin: "600px" }
    );
    const attributeFilter = itemCfg.source === "attribute" && itemCfg.attribute ? [itemCfg.attribute] : [];
    cardObserver = new MutationObserver((mutations) => {
      let shouldScan = false;
      for (const m of mutations) {
        if (m.type === "childList") {
          const target = m.target?.nodeType === 1 ? m.target : m.target?.parentElement;
          if (target && (target.matches?.(cardSelector) || target.closest?.(cardSelector))) {
            shouldScan = true;
          }
          for (const node of m.addedNodes) {
            if (node && node.nodeType === 1) {
              const el = node;
              if (typeof el.matches === "function" && (el.matches(cardSelector) || el.querySelector(cardSelector))) {
                shouldScan = true;
                break;
              }
            }
          }
        } else if (m.type === "attributes" || m.type === "characterData") {
          const target = m.target?.nodeType === 1 ? m.target : m.target?.parentElement;
          if (target && (target.matches?.(cardSelector) || target.closest?.(cardSelector))) {
            shouldScan = true;
          }
        }
        if (shouldScan) break;
      }
      if (shouldScan) scheduleScan();
    });
    const observerOptions = {
      childList: true,
      subtree: true
    };
    if (attributeFilter.length > 0) {
      observerOptions.attributes = true;
      observerOptions.attributeFilter = attributeFilter;
    }
    if (itemCfg.source === "text") {
      observerOptions.characterData = true;
    }
    cardObserver.observe(document.body, observerOptions);
    function scheduleScan() {
      if (scanTimer !== null) return;
      scanTimer = setTimeout(() => {
        scanTimer = null;
        scan();
      }, 50);
    }
    const handleNavigation = () => {
      generation.advance();
      resetCategoryCapture();
      for (const card of trackedCards) {
        const entry = registry.get(card);
        if (entry) {
          entry.generation = generation.current();
          entry.requested = false;
          entry.state = { kind: "loading" };
        }
      }
      scan();
    };
    window.addEventListener("popstate", handleNavigation);
    window.addEventListener("hashchange", handleNavigation);
    function refreshForStore(storeCode) {
      refreshSummaryContext({ storeCode });
    }
    function refreshSummaryContext({
      storeCode = selectedStoreCode,
      rankingDays = salesRankingDays
    } = {}) {
      selectedStoreCode = storeCode || null;
      salesRankingDays = normalizeRankingDays2(rankingDays);
      generation.advance();
      resetCategoryCapture();
      batch.clearCache();
      batch = createSummaryBatch(selectedStoreCode, salesRankingDays);
      for (const card of trackedCards) {
        const entry = registry.get(card);
        if (!entry || !card.isConnected) continue;
        entry.generation = generation.current();
        entry.requested = false;
        entry.state = { kind: "loading" };
        renderButton(entry, entry.state);
        if (entry.isVisible) requestSummary(entry);
      }
    }
    function teardown() {
      if (!active) return;
      active = false;
      cardObserver?.disconnect();
      visibilityObserver?.disconnect();
      if (scanTimer !== null) clearTimeout(scanTimer);
      if (scanInterval !== null) clearInterval(scanInterval);
      window.removeEventListener("popstate", handleNavigation);
      window.removeEventListener("hashchange", handleNavigation);
      for (const card of trackedCards) {
        registry.get(card)?.host?.remove();
      }
      trackedCards.clear();
      try {
        passiveCapture?.dispose();
        passiveCapture = null;
        crawlSession?.controller.abort();
        window.removeEventListener("pagehide", handleCategoryPageHide);
      } catch {
      }
    }
    chrome.storage.onChanged.addListener((changes, areaName) => {
      if (!matchesStorageArea2(areaName, "local") || !active) return;
      if (changes.selectedStoreCode && !changes.salesRankingDays) {
        refreshForStore(changes.selectedStoreCode.newValue);
      } else if (changes.selectedStoreCode || changes.salesRankingDays) {
        refreshSummaryContext({
          storeCode: changes.selectedStoreCode ? changes.selectedStoreCode.newValue : selectedStoreCode,
          rankingDays: changes.salesRankingDays ? changes.salesRankingDays.newValue : salesRankingDays
        });
      }
      if (changes.locale) {
        locale = normalizeLocale2(changes.locale.newValue);
        for (const card of trackedCards) {
          const entry = registry.get(card);
          if (entry) renderButton(entry, entry.state);
        }
      }
      if (changes.supplierProfiles) {
        const updatedProfiles = changes.supplierProfiles.newValue?.profiles || [];
        const updatedProfile = matchProfile2(updatedProfiles, { origin, pathname: location.pathname });
        if (!updatedProfile || updatedProfile.supplierCode !== profile.supplierCode) {
          teardown();
        } else {
          categoryConfig = resolveCategoryConfig(updatedProfile);
          if (!isCrawlEnabled()) crawlSession?.controller.abort();
          rebuildPassiveCapture();
        }
      }
      if (changes.categoryCaptureSettings && active) {
        categorySettings = changes.categoryCaptureSettings.newValue || {};
        rebuildPassiveCapture();
      }
    });
    scanInterval = setInterval(scan, 2e3);
    scan();
  })();
})();
