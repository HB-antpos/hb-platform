import { isAxiosError } from "axios";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import type { ContainerMain, ContainerQueryRequest, UpdateContainerRequest } from "./types";

/** 绑定 containerDetail 命名空间的翻译函数（界面传 useAppTranslation("containerDetail").t）。 */
export type ContainerTranslate = (key: string, options?: Record<string, unknown>) => string;

// 状态名走 containerDetail 的 status.{value}，与货柜明细页共用同一套文案。
export const CONTAINER_STATUS_OPTIONS = [
  { value: 0 },
  { value: 1 },
  { value: 2 },
  { value: 7 },
] as const;

// 后端只按 DateType 选择倒序排序字段；取值必须保持后端认识的中文字段名，界面文案统一叫「到库」。
// labelKey 用于排序按钮，summaryKey 用于列表顶部「按…从新到旧」。
export const CONTAINER_SORT_OPTIONS = [
  { value: "预计到岸日期", labelKey: "list.sort.estimated", summaryKey: "list.sortSummary.estimated" },
  { value: "实际到货日期", labelKey: "list.sort.actual", summaryKey: "list.sortSummary.actual" },
  { value: "装柜日期", labelKey: "list.sort.loading", summaryKey: "list.sortSummary.loading" },
] as const;

export const DEFAULT_CONTAINER_SORT = CONTAINER_SORT_OPTIONS[0].value;

const DATE_ONLY_PATTERN = /^(\d{4})-(\d{2})-(\d{2})/;
const DAY_MS = 24 * 60 * 60 * 1000;

/** 接口日期形如 2026-10-08T00:00:00，只取日期部分，避免经 Date 解析产生时区偏移。 */
export function toDateOnly(value?: string | null) {
  const match = value?.match(DATE_ONLY_PATTERN);
  return match ? `${match[1]}-${match[2]}-${match[3]}` : "";
}

export function formatShortDate(value?: string | null) {
  const date = toDateOnly(value);
  return date ? date.slice(5) : "";
}

function toUtcDay(date: string) {
  const [year, month, day] = date.split("-").map(Number);
  return Date.UTC(year, month - 1, day);
}

/** 两个 YYYY-MM-DD 之间相差的整天数（to - from）。 */
export function diffDays(from: string, to: string) {
  return Math.round((toUtcDay(to) - toUtcDay(from)) / DAY_MS);
}

export function containerStatusLabel(status: number | undefined, t: ContainerTranslate) {
  return CONTAINER_STATUS_OPTIONS.some((item) => item.value === status)
    ? t(`status.${status}`)
    : t("status.unknown", { status: status ?? "--" });
}

export type ArrivalInsightTone = "warning" | "muted" | "accent";

/** late/early：实际到库比预计晚/早；overdue/today/upcoming：未到库时相对今天。 */
export type ArrivalInsightKind = "late" | "early" | "overdue" | "today" | "upcoming";

export interface ArrivalInsight {
  /** 提示类型，文案由 arrivalInsightText 按界面语言生成 */
  kind: ArrivalInsightKind;
  /** 相差天数（正数；today 为 0） */
  days: number;
  tone: ArrivalInsightTone;
  /** 提示挂在哪个日期块上 */
  target: "estimated" | "actual";
}

/** 显示在对应日期块下方的短提示，如「晚 2 天」/「2 days late」。 */
export function arrivalInsightText(insight: ArrivalInsight, t: ContainerTranslate) {
  return insight.kind === "today"
    ? t("list.insight.today")
    : t(`list.insight.${insight.kind}`, { count: insight.days });
}

/**
 * 根据预计/实际到库日期推算列表卡片上的提示：
 * - 已登记实际到库：与预计相比晚到/早到；
 * - 未登记实际到库且未完成/取消：已逾期或还有几天到库。
 */
export function getArrivalInsight(container: ContainerMain, today: string): ArrivalInsight | null {
  const estimated = toDateOnly(container.预计到岸日期);
  const actual = toDateOnly(container.实际到货日期);

  if (actual) {
    if (!estimated) return null;
    const late = diffDays(estimated, actual);
    if (late > 0) return { kind: "late", days: late, tone: "warning", target: "actual" };
    if (late < 0) return { kind: "early", days: -late, tone: "muted", target: "actual" };
    return null;
  }

  // 已完成或已取消的货柜不再提示逾期，避免历史数据满屏警告。
  if (!estimated || container.状态 === 2 || container.状态 === 7) return null;

  const remaining = diffDays(today, estimated);
  if (remaining < 0) return { kind: "overdue", days: -remaining, tone: "warning", target: "estimated" };
  if (remaining === 0) return { kind: "today", days: 0, tone: "accent", target: "estimated" };
  return { kind: "upcoming", days: remaining, tone: "muted", target: "estimated" };
}

export interface ArrivalDateDraft {
  estimated: string;
  actual: string;
}

/**
 * 只提交有变化的日期：新值为空且原来有值时发送清空标记，未改动的字段不出现在请求里。
 * 返回 null 表示没有任何变化。
 */
export function buildArrivalDatePatch(
  original: ArrivalDateDraft,
  draft: ArrivalDateDraft,
): UpdateContainerRequest | null {
  const patch: UpdateContainerRequest = {};
  const originalEstimated = toDateOnly(original.estimated);
  const originalActual = toDateOnly(original.actual);
  const nextEstimated = toDateOnly(draft.estimated);
  const nextActual = toDateOnly(draft.actual);

  if (nextEstimated !== originalEstimated) {
    if (nextEstimated) patch.预计到岸日期 = nextEstimated;
    else patch.ClearEstimatedArrivalDate = true;
  }
  if (nextActual !== originalActual) {
    if (nextActual) patch.实际到货日期 = nextActual;
    else patch.ClearActualArrivalDate = true;
  }

  return Object.keys(patch).length ? patch : null;
}

export type ContainerListFilters = Pick<
  ContainerQueryRequest,
  | "dateType"
  | "containerNumberFilter"
  | "itemNumberFilter"
  | "statuses"
  | "loadingDateStart"
  | "loadingDateEnd"
  | "estimatedArrivalDateStart"
  | "estimatedArrivalDateEnd"
  | "actualArrivalDateStart"
  | "actualArrivalDateEnd"
>;

export const DATE_RANGE_FIELDS = [
  { key: "loading", start: "loadingDateStart", end: "loadingDateEnd" },
  { key: "estimated", start: "estimatedArrivalDateStart", end: "estimatedArrivalDateEnd" },
  { key: "actual", start: "actualArrivalDateStart", end: "actualArrivalDateEnd" },
] as const;

export type DateRangeFieldKey = (typeof DATE_RANGE_FIELDS)[number]["key"];

/** 日期区间名称（装柜日期 / 预计到库 / 实际到库）。 */
export function dateRangeFieldLabel(key: DateRangeFieldKey, t: ContainerTranslate) {
  return t(`list.dateFields.${key}`);
}

/** 返回第一个开始日期晚于结束日期的区间 key；都合法时返回 null。单边区间允许。 */
export function findInvalidDateRange(filters: ContainerListFilters): DateRangeFieldKey | null {
  for (const field of DATE_RANGE_FIELDS) {
    const start = toDateOnly(filters[field.start]);
    const end = toDateOnly(filters[field.end]);
    if (start && end && start > end) return field.key;
  }
  return null;
}

/** 筛选弹层里生效的条件数（货号 + 每个有值的日期区间），用于筛选按钮角标；状态和货柜编号在页面上直接可见，不计入。 */
export function countSheetFilters(filters: ContainerListFilters) {
  let count = filters.itemNumberFilter?.trim() ? 1 : 0;
  for (const field of DATE_RANGE_FIELDS) {
    if (toDateOnly(filters[field.start]) || toDateOnly(filters[field.end])) count += 1;
  }
  if (filters.dateType && filters.dateType !== DEFAULT_CONTAINER_SORT) count += 1;
  return count;
}

export function describeDateRange(t: ContainerTranslate, start?: string, end?: string) {
  const from = toDateOnly(start);
  const to = toDateOnly(end);
  if (from && to) return t("list.dateRange.between", { from, to });
  if (from) return t("list.dateRange.from", { from });
  if (to) return t("list.dateRange.until", { to });
  return "";
}

// 本地校验错误的 code → 文案 key（message 仍是中文，供中文界面原样显示）。
const LOCAL_ERROR_KEYS: Record<string, string> = {
  CONTAINER_EDIT_INVALID_NUMBER: "errors.invalidNumber",
  CONTAINER_EDIT_NO_CHANGES: "errors.noChanges",
};

const CJK_PATTERN = /[\u3400-\u9fff\uf900-\ufaff]/;

/**
 * 货柜页面统一的错误提示：
 * - 中文界面：与原来一致，优先显示错误原文（多为后端中文提示），没有再用 fallback；
 * - 英文界面：已知本地错误按 code 翻译；本地抛出的英文提示原样显示；
 *   其余（后端中文提示、axios 错误）交给 resolveLocalizedErrorMessage，不显示中文原文。
 */
export function describeContainerError(error: unknown, fallback: string, language: string, t: ContainerTranslate) {
  const raw = error instanceof Error ? error.message.trim() : "";
  if (language.toLowerCase().startsWith("zh")) return raw || fallback;

  const code = (error as { code?: unknown } | null)?.code;
  const localKey = typeof code === "string" ? LOCAL_ERROR_KEYS[code] : undefined;
  if (localKey) return t(localKey);
  if (raw && !isAxiosError(error) && !CJK_PATTERN.test(raw)) return raw;
  return resolveLocalizedErrorMessage(error, { language, t, fallbackText: fallback || undefined });
}
