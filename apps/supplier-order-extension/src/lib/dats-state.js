import { normalizeLastYear } from './last-year.js';
import { matchesListPage } from './profiles.js';
import { normalizeRankingDays, normalizeSalesRankBand } from './ranking.js';

// 列表注入的纯状态逻辑：代次守卫、节点登记（WeakMap）、列表/详情判断、按钮状态
export function createGenerationGuard(initial = 0) {
  let gen = initial;
  return {
    current: () => gen,
    advance: () => ++gen,
    isCurrent: (g) => g === gen,
  };
}

export function createNodeStateRegistry() {
  const map = new WeakMap();
  return {
    set: (node, state) => map.set(node, state),
    get: (node) => map.get(node),
    has: (node) => map.has(node),
    delete: (node) => map.delete(node),
  };
}

// 节点无状态或代次不一致时需（重新）处理
export function needsProcessing(node, registry, generation) {
  const state = registry.get(node);
  return !state || state.generation !== generation;
}

// 仅列表卡片注入；详情页（单一详情容器）不注入
export function shouldInjectList({ href, listPagePatterns, cardCount, isDetailPage = false }) {
  if (isDetailPage || cardCount < 1) return false;
  const patterns = Array.isArray(listPagePatterns) ? listPagePatterns : [];
  if (patterns.length > 0) {
    return matchesListPage(patterns, href);
  }
  return cardCount > 1;
}

// 摘要按钮状态：无匹配 / 无采购 / 错误 给出明确短状态
export function computeButtonState(item) {
  if (!item) return { kind: 'none', reason: 'noMatch' };
  if (item.error) return { kind: 'error', reason: 'error' };
  if (item.hasMatch === false) return { kind: 'none', reason: 'noMatch' };
  const salesRankBand = normalizeSalesRankBand(item.salesRankBand);
  const salesRankingDays = salesRankBand ? normalizeRankingDays(item.salesRankingDays) : null;
  if (item.hasPurchase === false) {
    return {
      kind: 'none',
      reason: 'noPurchase',
      ...(salesRankBand ? { salesRankBand, salesRankingDays } : {}),
      ...(item.lastYear ? { lastYear: item.lastYear } : {}),
    };
  }
  return {
    kind: 'ok',
    lastOrderDate: item.lastOrderDate,
    lastOrderQuantity: item.lastOrderQuantity,
    salesToDate: item.salesToDate,
    ...(salesRankBand ? { salesRankBand, salesRankingDays } : {}),
    ...(item.lastYear ? { lastYear: item.lastYear } : {}),
  };
}

export function buildSummaryCacheKey(storeCode, itemNumber, salesRankingDays) {
  const normalizedStoreCode = String(storeCode || 'none').trim() || 'none';
  const normalizedItemNumber = String(itemNumber || '').trim();
  return `${normalizedStoreCode}:${normalizeRankingDays(salesRankingDays)}:${normalizedItemNumber}`;
}

// 将服务端返回的单个摘要项归一化为 computeButtonState 所需的标准结构
export function normalizeSummaryItem(raw, { salesRankingAvailable = false } = {}) {
  if (!raw || typeof raw !== 'object') return { hasMatch: false };
  if (raw.error) return { error: raw.error };
  const matchStatus = typeof raw.matchStatus === 'string' ? raw.matchStatus.toLowerCase() : null;
  const hasMatch = matchStatus ? matchStatus !== 'unmatched' : raw.hasMatch !== false;
  const hasPurchase =
    matchStatus
      ? matchStatus === 'matched'
      : raw.hasPurchase != null
      ? raw.hasPurchase
      : raw.lastOrderDate != null
        || raw.latestPurchaseDate != null
        || raw.lastOrderQuantity != null
        || raw.latestPurchaseQuantity != null
        || raw.orderCount > 0;
  const normalized = {
    hasMatch,
    hasPurchase: !!hasPurchase,
    lastOrderDate: raw.latestPurchaseDate ?? raw.lastOrderDate ?? raw.lastOrderDateStr ?? null,
    lastOrderQuantity:
      raw.latestPurchaseQuantity ?? raw.lastOrderQuantity ?? raw.lastOrderQty ?? null,
    salesToDate: raw.salesSinceLatestPurchase ?? raw.salesToDate ?? raw.salesQty ?? null,
  };
  const salesRankBand = normalizeSalesRankBand(raw.salesRankBand);
  if (salesRankingAvailable === true && hasMatch && salesRankBand) {
    normalized.salesRankBand = salesRankBand;
  }
  // 去年同期：窗口不可用（旧后端、分店未启用、去年没数据）时没有这个键，按钮与改动前完全一样。
  const lastYear = hasMatch ? normalizeLastYear(raw) : null;
  if (lastYear) normalized.lastYear = lastYear;
  return normalized;
}

// 将批量摘要响应（对象/数组/items 数组）归一化为 itemNumber -> 标准摘要
export function normalizeSummaryMap(rawData) {
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
  if (typeof rawData === 'object') {
    for (const [k, v] of Object.entries(rawData)) {
      out[k] = normalizeSummaryItem(v);
    }
    return out;
  }
  return out;
}
