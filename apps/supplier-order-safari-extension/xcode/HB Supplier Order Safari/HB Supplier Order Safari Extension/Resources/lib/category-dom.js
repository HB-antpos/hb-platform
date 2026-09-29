// 分类采集的 DOM 薄适配层：只读取面包屑、标题、导航链接、子分类链接与分页链接，不修改页面。
// 注意：DOMParser 生成的文档 baseURI 是当前标签页而不是被抓取的页面，
// 因此所有链接一律用 getAttribute('href') + new URL(href, 被抓取页面 URL) 解析，绝不读取 a.href。
// 本模块刻意不 import 其他模块，便于无头浏览器测试直接内联执行。

export function parseHtml(html, DOMParserImpl = globalThis.DOMParser) {
  return new DOMParserImpl().parseFromString(String(html ?? ''), 'text/html');
}

// 选择器语法探测：后台下发的非法选择器只让该项失效（返回 null），不抛异常。
export function probeSelector(root, selector) {
  if (typeof selector !== 'string' || !selector) return null;
  try {
    root.querySelector(selector);
    return selector;
  } catch {
    return null;
  }
}

function safeQueryAll(root, selector) {
  if (!root || typeof selector !== 'string' || !selector) return [];
  try {
    return Array.from(root.querySelectorAll(selector));
  } catch {
    return [];
  }
}

function safeQuery(root, selector) {
  if (!root || typeof selector !== 'string' || !selector) return null;
  try {
    return root.querySelector(selector);
  } catch {
    return null;
  }
}

function normalizeText(value) {
  return String(value ?? '').replace(/\s+/gu, ' ').trim();
}

// 被抓取文档若声明了 <base href>，以它（相对被抓取 URL 解析）作为链接基准。
export function resolveDocumentBase(root, fetchedUrl) {
  const doc = root?.ownerDocument || root;
  const base = safeQuery(doc, 'base[href]')?.getAttribute('href');
  if (base) {
    try {
      return new URL(base, fetchedUrl).href;
    } catch {
      // 非法 base 忽略，回退被抓取 URL。
    }
  }
  return fetchedUrl;
}

// 只接受 http(s) 链接；纯锚点、javascript:、mailto: 等一律丢弃。
export function resolveLink(value, baseUrl) {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  if (!trimmed || trimmed.startsWith('#')) return null;
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
    return typeof el?.matches === 'function' && el.matches(selector);
  } catch {
    return false;
  }
}

// schema.org 面包屑常把名称放在 [itemprop=name]（可能是 meta content），首页项只有图标。
function readElementName(el) {
  const itemName = matches(el, '[itemprop="name"]') ? el : safeQuery(el, '[itemprop="name"]');
  if (itemName) {
    const content = itemName.getAttribute('content');
    const text = normalizeText(content != null && content.trim() ? content : itemName.textContent);
    if (text) return text;
  }
  const text = normalizeText(el?.textContent);
  if (text) return text;
  return normalizeText(el?.getAttribute?.('title') || el?.getAttribute?.('aria-label') || '');
}

function readElementHref(el) {
  if (matches(el, 'a[href], link[href]')) return el.getAttribute('href');
  const anchor = safeQuery(el, 'a[href]');
  if (anchor) return anchor.getAttribute('href');
  const item = safeQuery(el, '[itemprop="item"][href]');
  if (item) return item.getAttribute('href');
  const dataUrl = el?.getAttribute?.('data-url');
  return dataUrl || null;
}

export function readBreadcrumbItems(root, selector, pageUrl) {
  const base = resolveDocumentBase(root, pageUrl);
  return safeQueryAll(root, selector)
    .map((el) => ({ name: readElementName(el), url: resolveLink(readElementHref(el), base) }))
    .filter((item) => item.name || item.url);
}

export function readTitle(root, selector = 'h1') {
  const el = safeQuery(root, selector || 'h1');
  return el ? normalizeText(el.textContent) : '';
}

function toAnchor(el) {
  if (matches(el, 'a[href]')) return el;
  return safeQuery(el, 'a[href]');
}

// li 自己的锚点：li 内第一个“最近 li 就是它”的导航锚点。
function ownAnchorOfLi(li, valid) {
  for (const anchor of safeQueryAll(li, 'a[href]')) {
    if (valid.has(anchor) && anchor.closest('li') === li) return anchor;
  }
  return null;
}

// 列表前面的标题元素（mega menu 的“标题链接 + 子列表”）：只接受恰好含一个导航锚点的兄弟。
function headingAnchorOf(list, valid) {
  const previous = list.previousElementSibling;
  if (!previous) return null;
  if (valid.has(previous)) return previous;
  const tag = previous.tagName?.toLowerCase();
  if (tag === 'li' || tag === 'ul' || tag === 'ol') return null;
  const anchors = safeQueryAll(previous, 'a[href]').filter((anchor) => valid.has(anchor));
  return anchors.length === 1 ? anchors[0] : null;
}

// DOM 祖先推父级：逐级向上，先看子列表前的标题锚点，再看外层 li 的自身锚点。
function findDomParent(anchor, valid) {
  const ownLi = anchor.closest('li');
  let node = anchor.parentElement;
  while (node) {
    const tag = node.tagName?.toLowerCase();
    if (tag === 'ul' || tag === 'ol') {
      const heading = headingAnchorOf(node, valid);
      if (heading && heading !== anchor) return valid.get(heading).url;
    }
    if (tag === 'li' && node !== ownLi) {
      const own = ownAnchorOfLi(node, valid);
      if (own && own !== anchor) return valid.get(own).url;
    }
    node = node.parentElement;
  }
  return null;
}

export function readNavAnchors(root, selector, pageUrl) {
  const base = resolveDocumentBase(root, pageUrl);
  const anchors = [];
  const seen = new Set();
  for (const el of safeQueryAll(root, selector)) {
    const anchor = toAnchor(el);
    if (!anchor || seen.has(anchor)) continue;
    seen.add(anchor);
    anchors.push(anchor);
  }
  const valid = new Map();
  for (const anchor of anchors) {
    const url = resolveLink(anchor.getAttribute('href'), base);
    if (url) valid.set(anchor, { name: readElementName(anchor), url });
  }
  return anchors
    .filter((anchor) => valid.has(anchor))
    .map((anchor) => ({ ...valid.get(anchor), domParentUrl: findDomParent(anchor, valid) }));
}

export function readSubcategoryLinks(root, selector, pageUrl) {
  const base = resolveDocumentBase(root, pageUrl);
  const out = [];
  const seen = new Set();
  for (const el of safeQueryAll(root, selector)) {
    const anchor = toAnchor(el);
    const url = anchor ? resolveLink(anchor.getAttribute('href'), base) : null;
    if (!url || seen.has(url)) continue;
    seen.add(url);
    out.push({ name: readElementName(anchor), url });
  }
  return out;
}

// 分页“下一页”：兼容 <link rel="next">（写在 head）与普通链接。
export function readNextPageUrl(root, selector, pageUrl) {
  const base = resolveDocumentBase(root, pageUrl);
  for (const el of safeQueryAll(root, selector)) {
    const href = matches(el, 'a[href], link[href]') ? el.getAttribute('href') : toAnchor(el)?.getAttribute('href');
    const url = resolveLink(href, base);
    if (url) return url;
  }
  return null;
}

export function hasPasswordField(root) {
  return !!safeQuery(root, 'input[type="password"]');
}

export function readCards(root, cardSelector) {
  return safeQueryAll(root, cardSelector);
}

// 当前页面（或被抓取文档）的分类上下文：面包屑与标题。
export function readPageContext(root, config, pageUrl) {
  return {
    breadcrumbItems: config?.breadcrumbSelector
      ? readBreadcrumbItems(root, config.breadcrumbSelector, pageUrl)
      : [],
    title: readTitle(root, config?.titleSelector || 'h1'),
  };
}
