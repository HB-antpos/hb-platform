/**
 * 促销管理页（列表 / 编辑 / 只读详情）的纯逻辑：草稿结构、表单校验、排序口径、错误归类等。
 * 不依赖 React Native，便于 node:assert 单测；公共口径（状态、均价、冲突预检）复用 promotion-logic。
 */
import {
  MIN_APPLY_QUANTITY,
  buildQuickRange,
  getNonDiscountedProducts,
  getPromotionStatus,
  isScopeVisible,
  parsePromotionTime,
  validatePromotionRule,
  type PromotionRuleIssue,
} from "./promotion-logic";
import type {
  PromotionDetail,
  PromotionFormValues,
  PromotionGridQuery,
  PromotionListItem,
  PromotionStatusCounts,
  PromotionStatusFilter,
} from "./types";

/** 状态分段顺序：默认「生效中」。 */
export const PROMOTION_STATUS_SEGMENTS: readonly PromotionStatusFilter[] = [
  "active",
  "upcoming",
  "disabled",
  "ended",
  "all",
];

/** 买满数量上限：步进器防误触，实际业务远小于此值。 */
export const MAX_APPLY_QUANTITY = 99;

/**
 * 列表排序口径（用户定）：生效中 / 未开始按结束时间升序（快到期的排前），已结束按结束时间降序；
 * 已停用与全部沿用后端默认排序。
 */
export function getListSortModel(status: PromotionStatusFilter): PromotionGridQuery["sortModel"] {
  if (status === "active" || status === "upcoming") {
    return [{ colId: "effectiveEnd", sort: "asc" }];
  }
  if (status === "ended") {
    return [{ colId: "effectiveEnd", sort: "desc" }];
  }
  return undefined;
}

/** 列表标题下的排序说明用哪条文案。 */
export function getSortCaptionKind(status: PromotionStatusFilter): "endAsc" | "endDesc" | "none" {
  if (status === "active" || status === "upcoming") return "endAsc";
  if (status === "ended") return "endDesc";
  return "none";
}

/**
 * 「只看含某商品的促销」模式：by-product 接口一次返回全部（不分页、只含未结束），
 * 前端负责范围过滤（兼容旧后端仍返回未绑定分店的促销）、按分段过滤并按列表口径排序。
 */
export function filterProductPromotions(
  items: readonly PromotionListItem[],
  status: PromotionStatusFilter,
  now: Date = new Date(),
): PromotionListItem[] {
  const visible = items.filter((item) => isScopeVisible(item.scopeType));
  const filtered =
    status === "all" ? visible : visible.filter((item) => getPromotionStatus(item, now) === status);
  const direction = getSortCaptionKind(status) === "endDesc" ? -1 : 1;
  return [...filtered].sort((a, b) => {
    const endA = parsePromotionTime(a.effectiveEnd)?.getTime() ?? Number.MAX_SAFE_INTEGER;
    const endB = parsePromotionTime(b.effectiveEnd)?.getTime() ?? Number.MAX_SAFE_INTEGER;
    if (endA !== endB) return (endA - endB) * direction;
    return a.id.localeCompare(b.id);
  });
}

/** 商品模式下的分段计数（由前端按同一套状态判定统计）。 */
export function countPromotionsByStatus(
  items: readonly PromotionListItem[],
  now: Date = new Date(),
): PromotionStatusCounts {
  const counts: PromotionStatusCounts = { active: 0, upcoming: 0, disabled: 0, ended: 0, total: 0 };
  for (const item of items) {
    if (!isScopeVisible(item.scopeType)) continue;
    counts[getPromotionStatus(item, now)] += 1;
    counts.total += 1;
  }
  return counts;
}

export function getSegmentCount(counts: PromotionStatusCounts | null, status: PromotionStatusFilter) {
  if (!counts) return null;
  return status === "all" ? counts.total : counts[status];
}

export type PromotionErrorKind = "exclusiveConflict" | "storeForbidden" | "notFound" | null;

/**
 * 把后端业务错误归类成页面能稳定映射的几种：
 * api-envelope 会把 errorCode 放到 error.code，启停接口则把 exclusive_conflict 放在 message 里，两处都要看。
 */
export function getPromotionErrorKind(error: unknown): PromotionErrorKind {
  const record = error && typeof error === "object" ? (error as Record<string, unknown>) : null;
  const code = typeof record?.code === "string" ? record.code : "";
  const message = typeof record?.message === "string" ? record.message : "";
  const text = `${code} ${message}`.toLowerCase();
  if (text.includes("exclusive_conflict") || message.includes("排他促销优先级冲突")) return "exclusiveConflict";
  if (text.includes("store_forbidden")) return "storeForbidden";
  if (text.includes("promotion_not_found")) return "notFound";
  return null;
}

/** 编辑页里的一行参与商品；名称与零售价按本店查询，查不到时仍保留商品编码。 */
export interface PromotionDraftProduct {
  id?: string;
  productCode: string;
  unitWeight: number;
  productName?: string | null;
  barcode?: string | null;
  retailPrice?: number | null;
  /** idle：还没查；loading：查询中；ready：已拿到；failed：查询失败（仍可保存） */
  lookupState: "idle" | "loading" | "ready" | "failed";
}

export interface PromotionDraft {
  id?: string;
  name: string;
  description?: string;
  applyQuantity: number;
  /** 组合总价的输入框原文，保存时再解析，避免输入「12.」时被吞掉小数点。 */
  fixedPriceText: string;
  effectiveStart: string;
  effectiveEnd: string;
  isEnabled: boolean;
  isExclusive: boolean;
  priority: number;
  /** 每单最多次数的输入框原文，空表示不限。 */
  maxApplicationsText: string;
  products: PromotionDraftProduct[];
}

/** 新建草稿：本店独享、默认启用、排他、优先级 0（后端店级新建也会强制 0），有效期默认今天起 30 天。 */
export function createEmptyDraft(now: Date = new Date()): PromotionDraft {
  const range = buildQuickRange("thirtyDays", now);
  return {
    name: "",
    applyQuantity: MIN_APPLY_QUANTITY,
    fixedPriceText: "",
    effectiveStart: range.effectiveStart,
    effectiveEnd: range.effectiveEnd,
    isEnabled: true,
    // 默认可叠加：与商品查询页快速新建、已确认的设计稿一致，避免新建即与已有促销排他冲突。
    isExclusive: false,
    priority: 0,
    maxApplicationsText: "",
    products: [],
  };
}

function normalizeIso(value: string) {
  // 后端读回的时间不带时区（按 UTC 存），统一转成带 Z 的 ISO，避免回写时被当成本地时间。
  return parsePromotionTime(value)?.toISOString() ?? value;
}

export function draftFromDetail(detail: PromotionDetail): PromotionDraft {
  return {
    id: detail.id,
    name: detail.name,
    description: detail.description,
    applyQuantity: detail.applyQuantity,
    fixedPriceText: detail.fixedPrice > 0 ? detail.fixedPrice.toFixed(2) : "",
    effectiveStart: normalizeIso(detail.effectiveStart),
    effectiveEnd: normalizeIso(detail.effectiveEnd),
    isEnabled: detail.isEnabled,
    isExclusive: detail.isExclusive,
    priority: detail.priority,
    maxApplicationsText: detail.maxApplicationsPerOrder ? String(detail.maxApplicationsPerOrder) : "",
    // 草稿里新行在前（扫码时刚加的在眼前），所以把详情里按加入顺序的商品反过来放；提交时再反转回去。
    products: [...detail.products].reverse().map((product) => ({
      id: product.id,
      productCode: product.productCode,
      unitWeight: Math.max(1, product.unitWeight || 1),
      lookupState: "idle",
    })),
  };
}

/** 金额输入：只认非负数字，最多两位小数；不合法返回 null。 */
export function parseMoneyInput(text: string): number | null {
  const trimmed = text.trim().replace(/^\$/, "").trim();
  if (!/^\d+(\.\d{0,2})?$/.test(trimmed)) return null;
  const value = Number(trimmed);
  return Number.isFinite(value) ? value : null;
}

/** 正整数输入：空串返回 null（表示不限），非法返回 NaN。 */
export function parsePositiveIntInput(text: string): number | null {
  const trimmed = text.trim();
  if (!trimmed) return null;
  if (!/^\d+$/.test(trimmed)) return Number.NaN;
  const value = Number(trimmed);
  return value > 0 ? value : Number.NaN;
}

export function stepApplyQuantity(current: number, delta: number) {
  const base = Number.isFinite(current) ? Math.trunc(current) : MIN_APPLY_QUANTITY;
  return Math.min(MAX_APPLY_QUANTITY, Math.max(MIN_APPLY_QUANTITY, base + delta));
}

export function stepUnitWeight(current: number, delta: number) {
  const base = Number.isFinite(current) ? Math.trunc(current) : 1;
  return Math.min(MAX_APPLY_QUANTITY, Math.max(1, base + delta));
}

/**
 * 加入一个商品（扫码 / 搜索）：同一商品编码只保留一行，重复时返回 duplicated=true 由页面提示。
 * 新行插在最前面，连续扫码时刚扫到的商品始终在眼前。
 */
export function addDraftProduct(
  products: readonly PromotionDraftProduct[],
  product: Omit<PromotionDraftProduct, "unitWeight" | "lookupState"> &
    Partial<Pick<PromotionDraftProduct, "unitWeight" | "lookupState">>,
): { products: PromotionDraftProduct[]; duplicated: boolean } {
  const code = product.productCode.trim();
  if (!code) return { products: [...products], duplicated: false };
  if (products.some((item) => item.productCode === code)) {
    return { products: [...products], duplicated: true };
  }
  return {
    products: [
      {
        ...product,
        productCode: code,
        unitWeight: Math.max(1, product.unitWeight ?? 1),
        lookupState: product.lookupState ?? (product.productName ? "ready" : "idle"),
      },
      ...products,
    ],
    duplicated: false,
  };
}

export type PromotionDraftIssue =
  | PromotionRuleIssue
  | "nameRequired"
  | "productsRequired"
  | "rangeInvalid"
  | "maxApplicationsInvalid";

/**
 * 保存前的阻断性校验。
 * 规则口径复用 validatePromotionRule：只有 1 个商品且组合价不比原价便宜时禁止保存；多商品混搭只提醒不拦。
 */
export function validateDraft(draft: PromotionDraft): PromotionDraftIssue[] {
  const issues: PromotionDraftIssue[] = [];
  if (!draft.name.trim()) issues.push("nameRequired");
  const fixedPrice = parseMoneyInput(draft.fixedPriceText) ?? 0;
  issues.push(
    ...validatePromotionRule({
      applyQuantity: draft.applyQuantity,
      fixedPrice,
      productCount: draft.products.length,
      singleProductRetailPrice: draft.products.length === 1 ? draft.products[0].retailPrice : null,
    }),
  );
  if (draft.products.length === 0) issues.push("productsRequired");
  const start = parsePromotionTime(draft.effectiveStart);
  const end = parsePromotionTime(draft.effectiveEnd);
  if (!start || !end || end.getTime() <= start.getTime()) issues.push("rangeInvalid");
  const maxApplications = parsePositiveIntInput(draft.maxApplicationsText);
  if (maxApplications !== null && Number.isNaN(maxApplications)) issues.push("maxApplicationsInvalid");
  return issues;
}

/** 混搭促销里买满也不优惠的行（仅提醒）；单商品时由 validateDraft 阻断，这里不重复提示。 */
export function getDraftNonDiscountedProducts(draft: PromotionDraft) {
  if (draft.products.length <= 1) return [];
  const fixedPrice = parseMoneyInput(draft.fixedPriceText);
  if (fixedPrice == null || fixedPrice <= 0) return [];
  return getNonDiscountedProducts(draft.products, draft.applyQuantity, fixedPrice);
}

/**
 * 草稿 → 店级保存请求。分店固定为当前店；新建时优先级固定 0（后端也会强制），编辑时保持原值。
 */
export function buildFormValuesFromDraft(draft: PromotionDraft, storeCode: string): PromotionFormValues {
  const maxApplications = parsePositiveIntInput(draft.maxApplicationsText);
  return {
    id: draft.id,
    name: draft.name.trim(),
    description: draft.description,
    storeCode,
    effectiveStart: draft.effectiveStart,
    effectiveEnd: draft.effectiveEnd,
    isEnabled: draft.isEnabled,
    isExclusive: draft.isExclusive,
    priority: draft.id ? draft.priority : 0,
    applyQuantity: Math.trunc(draft.applyQuantity),
    fixedPrice: parseMoneyInput(draft.fixedPriceText) ?? 0,
    maxApplicationsPerOrder: maxApplications && !Number.isNaN(maxApplications) ? maxApplications : null,
    // 新行插在最前面只是为了扫码时好看；提交顺序按加入先后，便于和 Web 后台对照。
    products: [...draft.products].reverse().map((product) => ({
      id: product.id,
      productCode: product.productCode,
      unitWeight: product.unitWeight,
    })),
  };
}

/** 用于判断「有未保存修改」：只比较会提交给后端的字段。 */
export function getDraftFingerprint(draft: PromotionDraft) {
  return JSON.stringify({
    name: draft.name.trim(),
    q: draft.applyQuantity,
    p: draft.fixedPriceText.trim(),
    s: draft.effectiveStart,
    e: draft.effectiveEnd,
    en: draft.isEnabled,
    ex: draft.isExclusive,
    m: draft.maxApplicationsText.trim(),
    products: draft.products.map((item) => `${item.productCode}:${item.unitWeight}`),
  });
}

function pad2(value: number) {
  return String(value).padStart(2, "0");
}

/** ISO → 本地日期 YYYY-MM-DD 与时间 HH:mm（供日期选择器与时间输入框使用）。 */
export function splitLocalDateTime(iso: string): { date: string; time: string } | null {
  const date = parsePromotionTime(iso);
  if (!date) return null;
  return {
    date: `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`,
    time: `${pad2(date.getHours())}:${pad2(date.getMinutes())}`,
  };
}

/** 解析 HH:mm（允许 9:5、0930 这类输入），不合法返回 null。 */
export function parseTimeInput(text: string): { hours: number; minutes: number } | null {
  const trimmed = text.trim();
  const match = /^(\d{1,2})(?::?(\d{1,2}))?$/.exec(trimmed.length === 4 && !trimmed.includes(":") ? `${trimmed.slice(0, 2)}:${trimmed.slice(2)}` : trimmed);
  if (!match) return null;
  const hours = Number(match[1]);
  const minutes = match[2] ? Number(match[2]) : 0;
  if (hours > 23 || minutes > 59) return null;
  return { hours, minutes };
}

/**
 * 本地日期 + 时间 → UTC ISO。结束时间精确到 59 秒，与快捷有效期一致（23:59 表示整天结束）。
 */
export function combineLocalDateTime(dateText: string, time: { hours: number; minutes: number }, isEnd: boolean) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(dateText);
  if (!match) return null;
  const date = new Date(
    Number(match[1]),
    Number(match[2]) - 1,
    Number(match[3]),
    time.hours,
    time.minutes,
    isEnd ? 59 : 0,
    0,
  );
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

/** 复制到本店的默认新名称：「原名（分店名）」，与设计稿一致。 */
export function buildCopyName(sourceName: string, storeName?: string | null) {
  const name = sourceName.trim();
  const store = (storeName ?? "").trim();
  if (!store) return name;
  const suffix = `（${store}）`;
  return name.endsWith(suffix) ? name : `${name}${suffix}`;
}

/** 列表卡片日期行的剩余时间描述。 */
export type RemainingKind =
  | { kind: "longTerm" }
  | { kind: "daysLeft"; days: number }
  | { kind: "startsIn"; days: number }
  | { kind: "ended" }
  | { kind: "none" };

const DAY_MS = 24 * 60 * 60 * 1000;

export function getRemainingKind(
  item: Pick<PromotionListItem, "isEnabled" | "effectiveStart" | "effectiveEnd">,
  now: Date = new Date(),
): RemainingKind {
  const status = getPromotionStatus(item, now);
  const start = parsePromotionTime(item.effectiveStart);
  const end = parsePromotionTime(item.effectiveEnd);
  if (status === "ended") return { kind: "ended" };
  if (end && end.getTime() - now.getTime() > 5 * 365 * DAY_MS) return { kind: "longTerm" };
  if (start && start.getTime() > now.getTime()) {
    return { kind: "startsIn", days: Math.max(0, Math.ceil((start.getTime() - now.getTime()) / DAY_MS)) };
  }
  if (end) return { kind: "daysLeft", days: Math.max(0, Math.ceil((end.getTime() - now.getTime()) / DAY_MS)) };
  return { kind: "none" };
}

/** 按固定并发跑一批异步任务（商品名称 / 零售价逐个查询时避免一次打出几十个请求）。 */
export async function runWithConcurrency<T>(
  items: readonly T[],
  limit: number,
  worker: (item: T) => Promise<void>,
) {
  let cursor = 0;
  const runners = Array.from({ length: Math.max(1, Math.min(limit, items.length)) }, async () => {
    while (cursor < items.length) {
      const item = items[cursor];
      cursor += 1;
      await worker(item);
    }
  });
  await Promise.all(runners);
}
