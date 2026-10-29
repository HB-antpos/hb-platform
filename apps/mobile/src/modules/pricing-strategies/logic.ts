import { sortByName } from "../../shared/utils/name-sort";
import { FIXED_MAX_COST, roundMoney } from "./pricing-curve";
import { intervalsError, isEditable, isFixed, priceDrops, type PricingIssue } from "./pricing-intervals";
import type {
  NamedOption,
  PricingEvaluateResult,
  PricingListFilters,
  PricingStrategyAlgorithm,
  PricingStrategyDetail,
  PricingStrategyIntervalSummary,
  PricingStrategyLevel,
  PricingStrategyListItem,
  PricingStrategyListPage,
  PricingStrategyMode,
  PricingStrategyRule,
  PricingStrategySavePayload,
  PricingStrategyTarget,
} from "./types";

/** 策略数量很少（线上不到 20 条），列表一次取完，不做分页。 */
export const PRICING_LIST_PAGE_SIZE = 200;
export const RATE_PRESETS = [1.5, 2, 2.5, 3, 3.5, 5] as const;
export const BEND_PRESETS = [0, 25, 50, 75, 100] as const;

// ---- 接口数据归一化（后端字段是 camelCase；容忍缺字段与字符串数字） ----
const asRecord = (value: unknown): Record<string, unknown> => (value && typeof value === "object" ? (value as Record<string, unknown>) : {});
const pick = (source: Record<string, unknown>, key: string) => source[key] ?? source[key.charAt(0).toUpperCase() + key.slice(1)];
const toNumber = (value: unknown): number | null => {
  const n = typeof value === "number" ? value : typeof value === "string" && value.trim() !== "" ? Number(value) : Number.NaN;
  return Number.isFinite(n) ? n : null;
};
const toText = (value: unknown) => (typeof value === "string" ? value : value == null ? "" : String(value));
const toCodes = (value: unknown) => (Array.isArray(value) ? value.map(toText).filter(Boolean) : []);
const LEVELS: PricingStrategyLevel[] = ["Supplier", "Store", "Global"];
const MODES: PricingStrategyMode[] = ["Fixed", "Curve", "Mixed", "Legacy", "Empty"];
const ALGORITHMS: PricingStrategyAlgorithm[] = ["Linear", "ArcUp", "ArcDown", "Exponential", "Step", "Fixed"];
const toLevel = (value: unknown): PricingStrategyLevel => LEVELS.find((level) => level.toLowerCase() === toText(value).toLowerCase()) ?? "Global";
// 后端存的算法名大小写不一定统一（Validate 用 OrdinalIgnoreCase），这里统一成标准写法，避免被当成「不支持」。
const toAlgorithm = (value: unknown): PricingStrategyAlgorithm =>
  ALGORITHMS.find((algorithm) => algorithm.toLowerCase() === toText(value).toLowerCase()) ?? "Linear";

function normalizeInterval(raw: unknown): PricingStrategyIntervalSummary | null {
  const source = asRecord(raw);
  const minPrice = toNumber(pick(source, "minPrice"));
  const maxPrice = toNumber(pick(source, "maxPrice"));
  if (minPrice === null || maxPrice === null) return null;
  const type = toText(pick(source, "type"));
  return {
    minPrice,
    maxPrice,
    type: type === "Fixed" || type === "Curve" ? type : "Legacy",
    rate: toNumber(pick(source, "rate")),
    startPrice: toNumber(pick(source, "startPrice")) ?? 0,
    endPrice: toNumber(pick(source, "endPrice")) ?? 0,
  };
}

export function normalizeListItem(raw: unknown): PricingStrategyListItem | null {
  const source = asRecord(raw);
  const id = toText(pick(source, "id"));
  if (!id) return null;
  const mode = toText(pick(source, "pricingMode"));
  const intervals = pick(source, "intervals");
  return {
    id,
    name: toText(pick(source, "name")),
    level: toLevel(pick(source, "level")),
    priority: toNumber(pick(source, "priority")) ?? 0,
    isEnabled: pick(source, "isEnabled") === true,
    storeCodes: toCodes(pick(source, "storeCodes")),
    supplierCodes: toCodes(pick(source, "supplierCodes")),
    pricingMode: MODES.find((item) => item === mode) ?? null,
    fixedRate: toNumber(pick(source, "fixedRate")),
    startRate: toNumber(pick(source, "startRate")),
    endRate: toNumber(pick(source, "endRate")),
    intervals: Array.isArray(intervals) ? intervals.map(normalizeInterval).filter((item): item is PricingStrategyIntervalSummary => item !== null) : [],
  };
}

export function normalizeListPage(raw: unknown): PricingStrategyListPage {
  const source = asRecord(raw);
  const rawItems = pick(source, "items");
  const items = Array.isArray(rawItems) ? rawItems.map(normalizeListItem).filter((item): item is PricingStrategyListItem => item !== null) : [];
  return { items, total: toNumber(pick(source, "total")) ?? items.length };
}

export function normalizeRule(raw: unknown): PricingStrategyRule {
  const source = asRecord(raw);
  const optional = (key: string) => toNumber(pick(source, key));
  const id = toText(pick(source, "id"));
  return {
    ...(id ? { id } : {}),
    minPrice: toNumber(pick(source, "minPrice")) ?? Number.NaN,
    maxPrice: toNumber(pick(source, "maxPrice")) ?? Number.NaN,
    startRetailPrice: optional("startRetailPrice"),
    endRetailPrice: optional("endRetailPrice"),
    curveBend: optional("curveBend"),
    startRate: toNumber(pick(source, "startRate")) ?? Number.NaN,
    endRate: toNumber(pick(source, "endRate")) ?? Number.NaN,
    algorithm: toAlgorithm(pick(source, "algorithm")),
  };
}

export function normalizeDetail(raw: unknown): PricingStrategyDetail | null {
  const source = asRecord(raw);
  const id = toText(pick(source, "id"));
  if (!id) return null;
  const details = pick(source, "details");
  const targets = pick(source, "targets");
  return {
    id,
    name: toText(pick(source, "name")),
    level: toLevel(pick(source, "level")),
    priority: toNumber(pick(source, "priority")) ?? 0,
    isEnabled: pick(source, "isEnabled") === true,
    // 区间按下限排序：后端 Validate 也先排序，编辑器默认它们是首尾相接的顺序。
    details: Array.isArray(details) ? details.map(normalizeRule).sort((a, b) => a.minPrice - b.minPrice) : [],
    targets: Array.isArray(targets)
      ? targets.map((item) => {
          const target = asRecord(item);
          const type = toText(pick(target, "targetType"));
          const code = toText(pick(target, "targetCode"));
          return { targetType: type === "Store" || type === "Supplier" ? type : "Global", ...(code ? { targetCode: code } : {}) } as PricingStrategyTarget;
        })
      : [],
  };
}

export function normalizeEvaluate(raw: unknown, purchasePrice: number): PricingEvaluateResult {
  const source = asRecord(raw);
  const retailPrice = toNumber(pick(source, "retailPrice")) ?? 0;
  const id = toText(pick(source, "strategyId"));
  const name = toText(pick(source, "strategyName"));
  return {
    retailPrice,
    // 优先用后端返回的实际成率；后端较旧时用 零售价 / 进货价 兜底
    effectiveRate: toNumber(pick(source, "effectiveRate")) ?? (purchasePrice > 0 ? retailPrice / purchasePrice : 0),
    strategyId: id || null,
    strategyName: name || null,
  };
}

// ---- 列表请求 ----
export const createDefaultListFilters = (): PricingListFilters => ({ keyword: "", storeCode: null, supplierCode: null });

/** 与 Web 列表同一个 grid 请求格式（StartRow / FilterModel / GlobalSearch）。 */
export function buildGridRequest(filters: PricingListFilters) {
  const filterModel: Record<string, { FilterType: string; Type: string; Filter: string }> = {};
  if (filters.storeCode) filterModel.storeCode = { FilterType: "text", Type: "equals", Filter: filters.storeCode };
  if (filters.supplierCode) filterModel.supplierCode = { FilterType: "text", Type: "equals", Filter: filters.supplierCode };
  const keyword = filters.keyword.trim();
  return {
    StartRow: 0,
    EndRow: PRICING_LIST_PAGE_SIZE - 1,
    PageSize: PRICING_LIST_PAGE_SIZE,
    ...(keyword ? { GlobalSearch: keyword } : {}),
    ...(Object.keys(filterModel).length ? { FilterModel: filterModel } : {}),
  };
}

export const countActiveFilters = (filters: PricingListFilters) => (filters.storeCode ? 1 : 0) + (filters.supplierCode ? 1 : 0);

// ---- 列表卡片：定价方式摘要与缩略图 ----
export type PricingSummary =
  | { kind: "fixed"; rate: number }
  | { kind: "mixed"; fixedCount: number; curveCount: number; startRate: number | null; endRate: number | null }
  | { kind: "curve"; count: number; startRate: number | null; endRate: number | null }
  | { kind: "legacy"; count: number; startRate: number | null; endRate: number | null }
  | { kind: "empty" };

export function summarizePricing(item: PricingStrategyListItem): PricingSummary {
  const intervals = item.intervals;
  const startRate = item.startRate;
  const endRate = item.endRate;
  // 单个固定区间（或后端摘要明确是 Fixed）显示倍数
  if (item.pricingMode === "Fixed" || (intervals.length === 1 && intervals[0].type === "Fixed")) {
    const rate = item.fixedRate ?? intervals[0]?.rate ?? startRate;
    if (rate !== null && rate !== undefined) return { kind: "fixed", rate };
  }
  if (item.pricingMode === "Empty" || (!item.pricingMode && intervals.length === 0)) return { kind: "empty" };
  if (item.pricingMode === "Legacy" || intervals.some((interval) => interval.type === "Legacy"))
    return { kind: "legacy", count: intervals.length, startRate, endRate };
  const fixedCount = intervals.filter((interval) => interval.type === "Fixed").length;
  if (fixedCount > 0) return { kind: "mixed", fixedCount, curveCount: intervals.length - fixedCount, startRate, endRate };
  return { kind: "curve", count: intervals.length, startRate, endRate };
}

export interface SparkSegment {
  kind: "Fixed" | "Curve" | "Legacy";
  points: { x: number; y: number }[];
}

/**
 * 卡片缩略图：横轴按区间序号等分（不按成本比例，否则「不限」会把前面压成一个点），纵轴是区间端点售价的对数，
 * 只表达「每段是固定还是曲线、整体怎么走」，不承担读数。width/height 为画布尺寸。
 */
export function sparkSegments(intervals: PricingStrategyIntervalSummary[], width: number, height: number, pad = 3): SparkSegment[] {
  const usable = intervals.filter((interval) => interval.startPrice > 0 && interval.endPrice > 0);
  if (!usable.length) return [];
  const logs = usable.flatMap((interval) => [Math.log(interval.startPrice), Math.log(interval.endPrice)]);
  const lo = Math.min(...logs);
  const hi = Math.max(...logs);
  const span = hi - lo || 1;
  const step = (width - pad * 2) / usable.length;
  const y = (price: number) => height - pad - ((Math.log(price) - lo) / span) * (height - pad * 2);
  return usable.map((interval, i) => ({
    kind: interval.type,
    points: [
      { x: pad + step * i, y: y(interval.startPrice) },
      { x: pad + step * (i + 1), y: y(interval.endPrice) },
    ],
  }));
}

// ---- 编辑页草稿 ----
export interface StrategyDraft {
  name: string;
  level: PricingStrategyLevel;
  /** 输入框原文，保存时再解析。 */
  priority: string;
  isEnabled: boolean;
  storeCodes: string[];
  supplierCodes: string[];
  rules: PricingStrategyRule[];
}

export function draftFromDetail(detail: PricingStrategyDetail): StrategyDraft {
  return {
    name: detail.name,
    level: detail.level,
    priority: String(detail.priority),
    isEnabled: detail.isEnabled,
    storeCodes: detail.targets.filter((target) => target.targetType === "Store" && target.targetCode).map((target) => target.targetCode!),
    supplierCodes: detail.targets.filter((target) => target.targetType === "Supplier" && target.targetCode).map((target) => target.targetCode!),
    rules: detail.details,
  };
}

export type DraftIssueCode = "nameRequired" | "priorityInvalid" | "storeRequired" | "supplierRequired" | "legacyNotConverted";
export type DraftIssue = { scope: "draft"; code: DraftIssueCode } | { scope: "rules"; issue: PricingIssue };

/** 保存前的校验：先基本信息（与 Web 表单同口径），再区间（与后端 Validate 同口径）。旧版规则必须先转换（后端保存时不接受下限 0）。 */
export function validateDraft(draft: StrategyDraft): DraftIssue | null {
  if (!draft.name.trim()) return { scope: "draft", code: "nameRequired" };
  if (parsePriority(draft.priority) === null) return { scope: "draft", code: "priorityInvalid" };
  if (draft.level === "Store" && draft.storeCodes.length === 0) return { scope: "draft", code: "storeRequired" };
  if (draft.level === "Supplier" && draft.supplierCodes.length === 0) return { scope: "draft", code: "supplierRequired" };
  if (!isEditable(draft.rules)) return { scope: "draft", code: "legacyNotConverted" };
  const issue = intervalsError(draft.rules);
  return issue ? { scope: "rules", issue } : null;
}

/** 优先级：空视为 0；只接受非负整数。 */
export function parsePriority(text: string): number | null {
  const trimmed = text.trim();
  if (trimmed === "") return 0;
  if (!/^\d+$/.test(trimmed)) return null;
  const value = Number(trimmed);
  return Number.isSafeInteger(value) ? value : null;
}

export function buildSavePayload(draft: StrategyDraft): PricingStrategySavePayload {
  return {
    name: draft.name.trim(),
    level: draft.level,
    priority: parsePriority(draft.priority) ?? 0,
    isEnabled: draft.isEnabled,
    details: draft.rules.map((rule) => ({
      ...(rule.id ? { id: rule.id } : {}),
      minPrice: rule.minPrice,
      maxPrice: rule.maxPrice,
      startRetailPrice: rule.startRetailPrice ?? null,
      endRetailPrice: rule.endRetailPrice ?? null,
      curveBend: rule.curveBend ?? null,
      startRate: rule.startRate,
      endRate: rule.endRate,
      algorithm: rule.algorithm,
    })),
    targets: [
      ...draft.storeCodes.map((code) => ({ targetType: "Store" as const, targetCode: code })),
      ...draft.supplierCodes.map((code) => ({ targetType: "Supplier" as const, targetCode: code })),
    ],
  };
}

/** 列表上直接启停：取回完整策略后只改 isEnabled 再整体保存（与 Web 同一套后端校验）。 */
export function buildToggledPayload(detail: PricingStrategyDetail, isEnabled: boolean): PricingStrategySavePayload {
  return {
    name: detail.name,
    level: detail.level,
    priority: detail.priority,
    isEnabled,
    details: detail.details,
    targets: detail.targets.map(({ targetType, targetCode }) => ({ targetType, ...(targetCode ? { targetCode } : {}) })),
  };
}

/** 编辑页底部摘要：区间组成、校验结论、回落处数。 */
export function summarizeDraftRules(rules: PricingStrategyRule[]) {
  const fixedCount = rules.filter(isFixed).length;
  return { total: rules.length, fixedCount, curveCount: rules.length - fixedCount, drops: isEditable(rules) ? priceDrops(rules).length : 0 };
}

// ---- 数字输入与显示 ----
/** 解析用户输入的数字：允许逗号作小数点、前后空格；空或无法识别返回 NaN。 */
export function parseDecimal(text: string): number {
  const trimmed = text.trim().replace(",", ".");
  if (trimmed === "" || !/^\d*\.?\d*$/.test(trimmed) || trimmed === ".") return Number.NaN;
  return Number(trimmed);
}

export const money = (value: number) => (Number.isFinite(value) ? roundMoney(value).toFixed(2) : "-");
export const isUnbounded = (cost: number) => cost >= FIXED_MAX_COST;

/** 分店 / 供应商下拉的选项按名字排序（名字空退回编号，同名按编号；中文界面按拼音）。返回新数组，不改入参。 */
export function sortNamedOptions(options: readonly NamedOption[], language?: string): NamedOption[] {
  return sortByName(options, (option) => ({ name: option.name, code: option.code }), language);
}
