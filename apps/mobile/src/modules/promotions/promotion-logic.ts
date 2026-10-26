import type {
  PromotionLifecycleStatus,
  PromotionListItem,
  PromotionScopeType,
} from "./types";

/** 买满数量下限：与 Web 后台促销表单一致（门槛件数 ≥ 2）。 */
export const MIN_APPLY_QUANTITY = 2;

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * 解析后端返回的促销时间。
 * 前端提交的是 UTC（toISOString），后端读回时序列化成不带时区的字符串，
 * 收银端与后端都按 UTC 比较，所以没有时区后缀时按 UTC 解析，避免在本地时区下整体偏移。
 */
export function parsePromotionTime(value?: string | null): Date | null {
  if (!value) return null;
  const text = value.trim();
  if (!text) return null;
  const hasZone = /(?:z|[+-]\d{2}:?\d{2})$/i.test(text);
  const date = new Date(hasZone ? text : `${text}Z`);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** 与后端 ApplyStoreStatusFilter 同一套判定：已结束优先，其次停用，再按开始时间分生效中 / 未开始。 */
export function getPromotionStatus(
  item: Pick<PromotionListItem, "isEnabled" | "effectiveStart" | "effectiveEnd">,
  now: Date = new Date(),
): PromotionLifecycleStatus {
  const start = parsePromotionTime(item.effectiveStart);
  const end = parsePromotionTime(item.effectiveEnd);
  const nowMs = now.getTime();
  if (end && end.getTime() < nowMs) return "ended";
  if (!item.isEnabled) return "disabled";
  if (start && start.getTime() > nowMs) return "upcoming";
  return "active";
}

/** 剩余天数（向上取整，至少 0）；没有结束时间返回 null。 */
export function getDaysRemaining(effectiveEnd: string | undefined, now: Date = new Date()) {
  const end = parsePromotionTime(effectiveEnd);
  if (!end) return null;
  return Math.max(0, Math.ceil((end.getTime() - now.getTime()) / DAY_MS));
}

/** 活动已过时间占比 0~1，用于列表进度条；时间不完整或未开始返回 0。 */
export function getElapsedRatio(
  item: Pick<PromotionListItem, "effectiveStart" | "effectiveEnd">,
  now: Date = new Date(),
) {
  const start = parsePromotionTime(item.effectiveStart);
  const end = parsePromotionTime(item.effectiveEnd);
  if (!start || !end || end.getTime() <= start.getTime()) return 0;
  const ratio = (now.getTime() - start.getTime()) / (end.getTime() - start.getTime());
  return Math.min(1, Math.max(0, ratio));
}

/** 「长期」促销：结束时间在 5 年以后（Web 后台常用远期结束时间表示长期有效）。 */
export function isLongTermPromotion(effectiveEnd: string | undefined, now: Date = new Date()) {
  const end = parsePromotionTime(effectiveEnd);
  return Boolean(end && end.getTime() - now.getTime() > 5 * 365 * DAY_MS);
}

function toCents(value: number) {
  return Math.round(value * 100);
}

/** 单件均价（保留到分）；数量不合法返回 null。 */
export function getUnitAveragePrice(applyQuantity: number, fixedPrice: number) {
  if (!Number.isFinite(applyQuantity) || applyQuantity <= 0 || !Number.isFinite(fixedPrice)) {
    return null;
  }
  return toCents(fixedPrice / applyQuantity) / 100;
}

export interface BundleSavings {
  /** 单件均价 */
  unitPrice: number;
  /** 每件省多少（零售价 - 均价） */
  savingPerUnit: number;
  /** 每组省多少（零售价 × 数量 - 组合价） */
  savingPerBundle: number;
  /** 折扣率 0~1（每组省 / 原价合计） */
  discountRate: number;
  /** 组合价是否真的比原价便宜 */
  isDiscount: boolean;
}

/**
 * 按某个商品的零售价计算组合价的优惠力度。
 * 用分做整数比较，避免 14.99 × 2 这类浮点误差把「刚好相等」判成便宜。
 */
export function getBundleSavings(
  retailPrice: number | null | undefined,
  applyQuantity: number,
  fixedPrice: number,
): BundleSavings | null {
  const unitPrice = getUnitAveragePrice(applyQuantity, fixedPrice);
  if (unitPrice == null || retailPrice == null || !Number.isFinite(retailPrice) || retailPrice <= 0) {
    return null;
  }
  const originalCents = toCents(retailPrice) * Math.trunc(applyQuantity);
  const bundleCents = toCents(fixedPrice);
  const savingPerBundleCents = originalCents - bundleCents;
  return {
    unitPrice,
    savingPerUnit: (toCents(retailPrice) - toCents(unitPrice)) / 100,
    savingPerBundle: savingPerBundleCents / 100,
    discountRate: originalCents > 0 ? savingPerBundleCents / originalCents : 0,
    isDiscount: savingPerBundleCents > 0,
  };
}

export type PromotionRuleIssue =
  | "quantityTooSmall"
  | "priceRequired"
  | "notDiscount";

/**
 * 组合规则校验。
 * 用户定：只有一个商品的促销，组合价不比原价便宜时禁止保存；
 * 多个商品混搭时，个别贵品享受不到优惠是正常的，只提醒（由 getNonDiscountedProducts 给出名单），不拦。
 */
export function validatePromotionRule(input: {
  applyQuantity: number;
  fixedPrice: number;
  productCount: number;
  singleProductRetailPrice?: number | null;
}): PromotionRuleIssue[] {
  const issues: PromotionRuleIssue[] = [];
  if (!Number.isFinite(input.applyQuantity) || Math.trunc(input.applyQuantity) < MIN_APPLY_QUANTITY) {
    issues.push("quantityTooSmall");
  }
  if (!Number.isFinite(input.fixedPrice) || toCents(input.fixedPrice) <= 0) {
    issues.push("priceRequired");
  }
  if (issues.length === 0 && input.productCount === 1) {
    const savings = getBundleSavings(input.singleProductRetailPrice, input.applyQuantity, input.fixedPrice);
    if (savings && !savings.isDiscount) {
      issues.push("notDiscount");
    }
  }
  return issues;
}

export interface PricedPromotionProduct {
  productCode: string;
  retailPrice?: number | null;
}

/** 混搭促销里买满数量却享受不到优惠的商品（组合价 ≥ 零售价 × 数量）。零售价未知的不提示。 */
export function getNonDiscountedProducts<T extends PricedPromotionProduct>(
  products: readonly T[],
  applyQuantity: number,
  fixedPrice: number,
): T[] {
  return products.filter((product) => {
    const savings = getBundleSavings(product.retailPrice, applyQuantity, fixedPrice);
    return Boolean(savings && !savings.isDiscount);
  });
}

export interface PromotionWindowCandidate {
  id?: string;
  effectiveStart: string;
  effectiveEnd: string;
  isEnabled: boolean;
  isExclusive: boolean;
}

/**
 * 保存前的本地冲突预检：时间重叠，且任意一方是排他促销、对方处于启用状态。
 * 只是提醒（服务端排他冲突校验仍是最终防线）；本条停用时不提醒，因为保存后不会参与计价。
 */
export function findExclusiveConflicts<T extends PromotionListItem>(
  candidate: PromotionWindowCandidate,
  existing: readonly T[],
): T[] {
  if (!candidate.isEnabled) return [];
  const start = parsePromotionTime(candidate.effectiveStart);
  const end = parsePromotionTime(candidate.effectiveEnd);
  if (!start || !end) return [];
  return existing.filter((item) => {
    if (candidate.id && item.id === candidate.id) return false;
    if (!item.isEnabled) return false;
    if (!candidate.isExclusive && !item.isExclusive) return false;
    const otherStart = parsePromotionTime(item.effectiveStart);
    const otherEnd = parsePromotionTime(item.effectiveEnd);
    if (!otherStart || !otherEnd) return false;
    return otherStart.getTime() <= end.getTime() && otherEnd.getTime() >= start.getTime();
  });
}

/**
 * 「改到冲突前结束」的建议结束时间：最早一条冲突开始前 1 分钟。
 * 若建议时间不晚于本条开始时间（整段都被覆盖），返回 null，界面不提供这个按钮。
 */
export function suggestEndBeforeConflicts(
  candidateStart: string,
  conflicts: readonly Pick<PromotionListItem, "effectiveStart">[],
): string | null {
  const start = parsePromotionTime(candidateStart);
  const starts = conflicts
    .map((item) => parsePromotionTime(item.effectiveStart))
    .filter((value): value is Date => Boolean(value))
    .map((value) => value.getTime());
  if (!start || starts.length === 0) return null;
  const suggested = Math.min(...starts) - 60 * 1000;
  return suggested > start.getTime() ? new Date(suggested).toISOString() : null;
}

const STATUS_ORDER: Record<PromotionLifecycleStatus, number> = {
  active: 0,
  upcoming: 1,
  disabled: 2,
  ended: 3,
};

/** 商品查询页排序：生效中 → 未开始 → 已停用，同组内先结束的排前。 */
export function sortProductPromotions<T extends PromotionListItem>(
  items: readonly T[],
  now: Date = new Date(),
): T[] {
  return [...items].sort((a, b) => {
    const statusDiff = STATUS_ORDER[getPromotionStatus(a, now)] - STATUS_ORDER[getPromotionStatus(b, now)];
    if (statusDiff !== 0) return statusDiff;
    const endA = parsePromotionTime(a.effectiveEnd)?.getTime() ?? Number.MAX_SAFE_INTEGER;
    const endB = parsePromotionTime(b.effectiveEnd)?.getTime() ?? Number.MAX_SAFE_INTEGER;
    if (endA !== endB) return endA - endB;
    return a.id.localeCompare(b.id);
  });
}

/** 只有本店独享可以改（与后端店级规则一致）；多店促销只读，可复制到本店。 */
export function canEditPromotionInStore(item: Pick<PromotionListItem, "scopeType" | "canEditInStoreScope">) {
  return item.scopeType === "StoreOnly" && item.canEditInStoreScope;
}

export function canCopyPromotionToStore(item: Pick<PromotionListItem, "scopeType" | "canCopyToStore">) {
  return item.scopeType === "MultiStore" && item.canCopyToStore;
}

/** 提前结束只对生效中的本店独享促销开放；未开始的直接改有效期或停用即可。 */
export function canEndPromotionNow(item: PromotionListItem, now: Date = new Date()) {
  return canEditPromotionInStore(item) && getPromotionStatus(item, now) === "active";
}

export function isScopeVisible(scopeType: PromotionScopeType | null) {
  // 未绑定分店（旧口径的 Headquarters）不属于任何分店，店级页面一律不显示。
  return scopeType === "StoreOnly" || scopeType === "MultiStore";
}

export type QuickRangeKey = "sevenDays" | "monthEnd" | "thirtyDays";

/**
 * 有效期快捷项（按设备本地日期计算整天）：从今天 00:00 开始，到第 N 天 23:59:59 结束。
 * 返回 UTC ISO 字符串，直接提交给后端。
 */
export function buildQuickRange(key: QuickRangeKey, now: Date = new Date()) {
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 0, 0, 0, 0);
  let end: Date;
  if (key === "monthEnd") {
    end = new Date(now.getFullYear(), now.getMonth() + 1, 0, 23, 59, 59, 0);
  } else {
    const days = key === "sevenDays" ? 7 : 30;
    end = new Date(now.getFullYear(), now.getMonth(), now.getDate() + days - 1, 23, 59, 59, 0);
  }
  return { effectiveStart: start.toISOString(), effectiveEnd: end.toISOString() };
}

function pad2(value: number) {
  return String(value).padStart(2, "0");
}

/** MM/DD（本地时区） */
export function formatPromotionDate(value?: string | null) {
  const date = parsePromotionTime(value);
  return date ? `${pad2(date.getMonth() + 1)}/${pad2(date.getDate())}` : "--";
}

/** MM/DD HH:mm（本地时区） */
export function formatPromotionDateTime(value?: string | null) {
  const date = parsePromotionTime(value);
  return date
    ? `${pad2(date.getMonth() + 1)}/${pad2(date.getDate())} ${pad2(date.getHours())}:${pad2(date.getMinutes())}`
    : "--";
}

export function formatMoney(value: number | null | undefined) {
  return value == null || !Number.isFinite(value) ? "0.00" : value.toFixed(2);
}

export function formatPercent(rate: number) {
  return `${(Math.round(rate * 1000) / 10).toFixed(1)}%`;
}
