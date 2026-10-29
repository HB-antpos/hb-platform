import {
  MIN_APPLY_QUANTITY,
  buildQuickRange,
  canCopyPromotionToStore,
  canEditPromotionInStore,
  canEndPromotionNow,
  getBundleSavings,
  getDaysRemaining,
  getNonDiscountedProducts,
  getPromotionStatus,
  isLongTermPromotion,
  isScopeVisible,
  parsePromotionTime,
  sortProductPromotions,
  validatePromotionRule,
  type BundleSavings,
  type PromotionRuleIssue,
  type PromotionWindowCandidate,
  type QuickRangeKey,
} from "../promotions/promotion-logic";
import type {
  PromotionDetail,
  PromotionFormValues,
  PromotionLifecycleStatus,
  PromotionListItem,
} from "../promotions/types";

/**
 * 商品查询页「本商品促销」区块的纯逻辑（不依赖 React Native，便于 node 单测）。
 * 公共促销逻辑在 promotions/promotion-logic.ts，这里只做区块专属的组装与草稿处理。
 */

/** 区块权限：hidden 整块不显示；readonly 只看不改；editable 可开关 / 编辑 / 提前结束 / 新建 / 复制。 */
export type ProductPromotionAccessMode = "hidden" | "readonly" | "editable";

export function resolveProductPromotionAccess(input: {
  canView: boolean;
  canEdit: boolean;
  /** 在线且为登录账号时才允许操作；离线、设备模式只读。 */
  interactive: boolean;
}): ProductPromotionAccessMode {
  // 用户定：区块显示只看 View；只有 Edit 没有 View 也不显示。
  if (!input.canView) return "hidden";
  return input.canEdit && input.interactive ? "editable" : "readonly";
}

export interface ProductPromotionCardModel {
  item: PromotionListItem;
  status: PromotionLifecycleStatus;
  /** store = 本店独享；multi = 多店（只读） */
  scope: "store" | "multi";
  /** 按本店零售价算出的优惠力度；零售价未知时为 null。 */
  savings: BundleSavings | null;
  /** 生效中：剩余天数；未开始：距开始天数；其余为 null。 */
  dayCount: number | null;
  longTerm: boolean;
  canToggle: boolean;
  canEdit: boolean;
  canEndNow: boolean;
  canCopy: boolean;
}

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * 区块卡片模型：先过滤掉未绑定分店（Headquarters / null，兼容旧后端），再排序并按权限算出可用操作。
 * 已结束的促销不进区块（新接口本来就不返回，这里再兜一层）。
 */
export function buildProductPromotionCards(
  items: readonly PromotionListItem[],
  retailPrice: number | null | undefined,
  mode: ProductPromotionAccessMode,
  now: Date = new Date(),
): ProductPromotionCardModel[] {
  if (mode === "hidden") return [];
  const editable = mode === "editable";
  const visible = items.filter(
    (item) => isScopeVisible(item.scopeType) && getPromotionStatus(item, now) !== "ended",
  );
  return sortProductPromotions(visible, now).map((item) => {
    const status = getPromotionStatus(item, now);
    const scope = item.scopeType === "MultiStore" ? "multi" : "store";
    const longTerm = isLongTermPromotion(item.effectiveEnd, now);
    let dayCount: number | null = null;
    if (status === "active" && !longTerm) {
      dayCount = getDaysRemaining(item.effectiveEnd, now);
    } else if (status === "upcoming") {
      const start = parsePromotionTime(item.effectiveStart);
      dayCount = start ? Math.max(0, Math.ceil((start.getTime() - now.getTime()) / DAY_MS)) : null;
    }
    const storeEditable = editable && canEditPromotionInStore(item);
    return {
      item,
      status,
      scope,
      savings: getBundleSavings(retailPrice, item.applyQuantity, item.fixedPrice),
      dayCount,
      longTerm,
      canToggle: storeEditable,
      canEdit: storeEditable,
      canEndNow: editable && canEndPromotionNow(item, now),
      canCopy: editable && canCopyPromotionToStore(item),
    };
  });
}

/** 海报入口只关心「此刻生效中」的促销数量（新接口还会返回未开始 / 已停用）。 */
export function countActiveProductPromotions(
  items: readonly PromotionListItem[],
  now: Date = new Date(),
) {
  return items.filter(
    (item) => isScopeVisible(item.scopeType) && getPromotionStatus(item, now) === "active",
  ).length;
}

/** 服务端排他冲突：新建 / 修改返回 errorCode=exclusive_conflict；启停接口把它放在 message 里。 */
export function isPromotionExclusiveConflictError(error: unknown) {
  if (!error || typeof error !== "object") return false;
  const record = error as { code?: unknown; message?: unknown };
  const code = typeof record.code === "string" ? record.code.trim().toLowerCase() : "";
  const message = typeof record.message === "string" ? record.message.trim().toLowerCase() : "";
  return code === "exclusive_conflict" || message === "exclusive_conflict";
}

// ---------------------------------------------------------------------------
// 快速新建 / 编辑草稿
// ---------------------------------------------------------------------------

export type PromotionRangeKey = QuickRangeKey | "custom";

export interface QuickPromotionDraftProduct {
  productCode: string;
  productName?: string | null;
  /** 本店零售价；未知时为 null（不参与「没有优惠」提醒）。 */
  retailPrice?: number | null;
  unitWeight: number;
}

export interface QuickPromotionDraft {
  /** 编辑时为原促销 id；新建为空。 */
  id?: string;
  name: string;
  description?: string;
  applyQuantity: number;
  /** 组合总价输入框原文，保存时再解析。 */
  fixedPriceText: string;
  rangeKey: PromotionRangeKey;
  effectiveStart: string;
  effectiveEnd: string;
  /** 编辑时沿用原开关；新建由「存为停用 / 保存并启用」决定。 */
  isEnabled: boolean;
  isExclusive: boolean;
  /** 店级新建后端强制 0；编辑沿用原值。界面只显示不提供修改。 */
  priority: number;
  /** 每单最多次数输入框原文；空 = 不限。 */
  maxPerOrderText: string;
  products: QuickPromotionDraftProduct[];
}

export function createQuickPromotionDraft(
  product: QuickPromotionDraftProduct,
  now: Date = new Date(),
): QuickPromotionDraft {
  const range = buildQuickRange("sevenDays", now);
  return {
    name: "",
    applyQuantity: MIN_APPLY_QUANTITY,
    fixedPriceText: "",
    rangeKey: "sevenDays",
    effectiveStart: range.effectiveStart,
    effectiveEnd: range.effectiveEnd,
    isEnabled: true,
    // 设计稿默认「可叠加」：本店小促销不应默认压掉其他促销。
    isExclusive: false,
    priority: 0,
    maxPerOrderText: "",
    products: [{ ...product, unitWeight: Math.max(1, Math.trunc(product.unitWeight || 1)) }],
  };
}

/** 编辑：从详情生成草稿；当前商品带上本店零售价，其余商品价格未知。 */
export function draftFromPromotionDetail(
  detail: PromotionDetail,
  currentProduct: QuickPromotionDraftProduct,
): QuickPromotionDraft {
  const products: QuickPromotionDraftProduct[] = detail.products
    .filter((item) => item.productCode)
    .map((item) =>
      item.productCode === currentProduct.productCode
        ? { ...currentProduct, unitWeight: Math.max(1, item.unitWeight || 1) }
        : { productCode: item.productCode, unitWeight: Math.max(1, item.unitWeight || 1) },
    );
  return {
    id: detail.id,
    name: detail.name,
    description: detail.description,
    applyQuantity: detail.applyQuantity,
    fixedPriceText: Number.isFinite(detail.fixedPrice) ? detail.fixedPrice.toFixed(2) : "",
    rangeKey: "custom",
    effectiveStart: normalizeIso(detail.effectiveStart),
    effectiveEnd: normalizeIso(detail.effectiveEnd),
    isEnabled: detail.isEnabled,
    isExclusive: detail.isExclusive,
    priority: detail.priority,
    maxPerOrderText: detail.maxApplicationsPerOrder ? String(detail.maxApplicationsPerOrder) : "",
    products: products.length ? products : [{ ...currentProduct, unitWeight: 1 }],
  };
}

/** 后端读回的时间不带时区（实际是 UTC），统一转成带 Z 的 ISO，后续比较与提交都不会偏移。 */
function normalizeIso(value: string) {
  return parsePromotionTime(value)?.toISOString() ?? value;
}

export function applyQuickRange(
  draft: QuickPromotionDraft,
  key: QuickRangeKey,
  now: Date = new Date(),
): QuickPromotionDraft {
  return { ...draft, rangeKey: key, ...buildQuickRange(key, now) };
}

const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

/** 自定义日期：开始取当天 00:00，结束取当天 23:59:59（设备本地时区），提交 UTC ISO。 */
export function applyCustomDate(
  draft: QuickPromotionDraft,
  edge: "start" | "end",
  date: string,
): QuickPromotionDraft {
  const match = date.match(DATE_PATTERN);
  if (!match) return draft;
  const [year, month, day] = [Number(match[1]), Number(match[2]) - 1, Number(match[3])];
  const value =
    edge === "start"
      ? new Date(year, month, day, 0, 0, 0, 0)
      : new Date(year, month, day, 23, 59, 59, 0);
  return {
    ...draft,
    rangeKey: "custom",
    ...(edge === "start"
      ? { effectiveStart: value.toISOString() }
      : { effectiveEnd: value.toISOString() }),
  };
}

function pad2(value: number) {
  return String(value).padStart(2, "0");
}

/** ISO → 本地 YYYY-MM-DD，给日历控件用。 */
export function toLocalDateString(value: string) {
  const date = parsePromotionTime(value);
  if (!date) return undefined;
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
}

/** 金额输入只保留数字和一个小数点，小数最多 2 位。 */
export function sanitizeMoneyInput(text: string) {
  const cleaned = text.replace(/[^\d.]/g, "");
  const [integer, ...rest] = cleaned.split(".");
  if (rest.length === 0) return integer;
  return `${integer}.${rest.join("").slice(0, 2)}`;
}

export function parseMoneyText(text: string): number | null {
  const trimmed = text.trim();
  if (!trimmed || !/^\d*\.?\d{0,2}$/.test(trimmed) || trimmed === ".") return null;
  const value = Number(trimmed);
  return Number.isFinite(value) ? value : null;
}

/** 每单最多次数：空 = 不限（null）；非正整数视为非法（undefined）。 */
export function parseMaxPerOrderText(text: string): number | null | undefined {
  const trimmed = text.trim();
  if (!trimmed) return null;
  if (!/^\d+$/.test(trimmed)) return undefined;
  const value = Number(trimmed);
  return value > 0 ? value : undefined;
}

export type QuickPromotionIssue =
  | PromotionRuleIssue
  | "rangeInvalid"
  | "rangeEnded"
  | "maxPerOrderInvalid"
  | "productsRequired";

export interface QuickPromotionEvaluation {
  fixedPrice: number | null;
  issues: QuickPromotionIssue[];
  /** 当前商品（入口商品）的优惠力度，用于预览。 */
  savings: BundleSavings | null;
  /** 混搭时买满也没有优惠的商品（只提醒不拦）。 */
  nonDiscounted: QuickPromotionDraftProduct[];
  canSave: boolean;
}

export function evaluateQuickPromotionDraft(
  draft: QuickPromotionDraft,
  anchorProductCode: string,
  now: Date = new Date(),
): QuickPromotionEvaluation {
  const fixedPrice = parseMoneyText(draft.fixedPriceText);
  const anchor =
    draft.products.find((item) => item.productCode === anchorProductCode) ?? draft.products[0];
  const productCount = draft.products.length;
  const ruleIssues = validatePromotionRule({
    applyQuantity: draft.applyQuantity,
    fixedPrice: fixedPrice ?? Number.NaN,
    productCount,
    singleProductRetailPrice: anchor?.retailPrice ?? null,
  });
  const issues: QuickPromotionIssue[] = [...ruleIssues];
  if (productCount === 0) issues.push("productsRequired");

  const start = parsePromotionTime(draft.effectiveStart);
  const end = parsePromotionTime(draft.effectiveEnd);
  if (!start || !end || end.getTime() <= start.getTime()) {
    issues.push("rangeInvalid");
  } else if (end.getTime() <= now.getTime()) {
    // 结束时间已过等于直接结束；要结束请用「提前结束」。
    issues.push("rangeEnded");
  }
  if (parseMaxPerOrderText(draft.maxPerOrderText) === undefined) {
    issues.push("maxPerOrderInvalid");
  }

  const savings =
    fixedPrice != null ? getBundleSavings(anchor?.retailPrice, draft.applyQuantity, fixedPrice) : null;
  const nonDiscounted =
    fixedPrice != null && productCount > 1
      ? getNonDiscountedProducts(draft.products, draft.applyQuantity, fixedPrice)
      : [];
  return { fixedPrice, issues, savings, nonDiscounted, canSave: issues.length === 0 };
}

export function addDraftProduct(
  draft: QuickPromotionDraft,
  product: QuickPromotionDraftProduct,
): QuickPromotionDraft {
  const code = product.productCode.trim();
  if (!code || draft.products.some((item) => item.productCode === code)) return draft;
  return {
    ...draft,
    products: [...draft.products, { ...product, productCode: code, unitWeight: Math.max(1, product.unitWeight || 1) }],
  };
}

/** 至少保留一个商品；入口商品不允许移除（区块只展示包含本商品的促销）。 */
export function removeDraftProduct(
  draft: QuickPromotionDraft,
  productCode: string,
  anchorProductCode: string,
): QuickPromotionDraft {
  if (productCode === anchorProductCode || draft.products.length <= 1) return draft;
  return { ...draft, products: draft.products.filter((item) => item.productCode !== productCode) };
}

export function resolveDraftName(draft: QuickPromotionDraft, defaultName: string) {
  return draft.name.trim() || defaultName.trim();
}

/** 草稿 → 店级新建 / 修改接口参数；分店固定为当前店。 */
export function buildQuickPromotionPayload(
  draft: QuickPromotionDraft,
  options: { storeCode: string; isEnabled: boolean; defaultName: string },
): PromotionFormValues {
  const maxPerOrder = parseMaxPerOrderText(draft.maxPerOrderText);
  return {
    id: draft.id,
    name: resolveDraftName(draft, options.defaultName),
    description: draft.description,
    storeCode: options.storeCode,
    effectiveStart: draft.effectiveStart,
    effectiveEnd: draft.effectiveEnd,
    isEnabled: options.isEnabled,
    isExclusive: draft.isExclusive,
    priority: draft.priority,
    applyQuantity: Math.trunc(draft.applyQuantity),
    fixedPrice: parseMoneyText(draft.fixedPriceText) ?? 0,
    maxApplicationsPerOrder: maxPerOrder ?? null,
    products: draft.products.map((item) => ({
      productCode: item.productCode,
      unitWeight: item.unitWeight,
    })),
  };
}

export function toConflictCandidate(
  draft: QuickPromotionDraft,
  isEnabled: boolean,
): PromotionWindowCandidate {
  return {
    id: draft.id,
    effectiveStart: draft.effectiveStart,
    effectiveEnd: draft.effectiveEnd,
    isEnabled,
    isExclusive: draft.isExclusive,
  };
}
