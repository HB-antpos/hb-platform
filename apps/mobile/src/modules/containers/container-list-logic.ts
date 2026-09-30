import type { ContainerMain, ContainerQueryRequest, UpdateContainerRequest } from "./types";

export const CONTAINER_STATUS_OPTIONS = [
  { value: 0, label: "已装柜" },
  { value: 1, label: "在途" },
  { value: 2, label: "已完成" },
  { value: 7, label: "已取消" },
] as const;

// 后端只按 DateType 选择倒序排序字段；取值必须保持后端认识的中文字段名，界面文案统一叫「到库」。
export const CONTAINER_SORT_OPTIONS = [
  { value: "预计到岸日期", label: "预计到库" },
  { value: "实际到货日期", label: "实际到库" },
  { value: "装柜日期", label: "装柜" },
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

export function containerStatusLabel(status?: number) {
  return CONTAINER_STATUS_OPTIONS.find((item) => item.value === status)?.label ?? `状态 ${status ?? "--"}`;
}

export type ArrivalInsightTone = "warning" | "muted" | "accent";

export interface ArrivalInsight {
  /** 显示在对应日期块下方的短提示 */
  text: string;
  tone: ArrivalInsightTone;
  /** 提示挂在哪个日期块上 */
  target: "estimated" | "actual";
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
    if (late > 0) return { text: `晚 ${late} 天`, tone: "warning", target: "actual" };
    if (late < 0) return { text: `早 ${-late} 天`, tone: "muted", target: "actual" };
    return null;
  }

  // 已完成或已取消的货柜不再提示逾期，避免历史数据满屏警告。
  if (!estimated || container.状态 === 2 || container.状态 === 7) return null;

  const remaining = diffDays(today, estimated);
  if (remaining < 0) return { text: `已逾期 ${-remaining} 天`, tone: "warning", target: "estimated" };
  if (remaining === 0) return { text: "今天到库", tone: "accent", target: "estimated" };
  return { text: `${remaining} 天后`, tone: "muted", target: "estimated" };
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
  { key: "loading", label: "装柜日期", start: "loadingDateStart", end: "loadingDateEnd" },
  { key: "estimated", label: "预计到库", start: "estimatedArrivalDateStart", end: "estimatedArrivalDateEnd" },
  { key: "actual", label: "实际到库", start: "actualArrivalDateStart", end: "actualArrivalDateEnd" },
] as const;

export type DateRangeFieldKey = (typeof DATE_RANGE_FIELDS)[number]["key"];

/** 返回第一个开始日期晚于结束日期的区间名称；都合法时返回 null。单边区间允许。 */
export function findInvalidDateRange(filters: ContainerListFilters) {
  for (const field of DATE_RANGE_FIELDS) {
    const start = toDateOnly(filters[field.start]);
    const end = toDateOnly(filters[field.end]);
    if (start && end && start > end) return field.label;
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

export function describeDateRange(start?: string, end?: string) {
  const from = toDateOnly(start);
  const to = toDateOnly(end);
  if (from && to) return `${from} 至 ${to}`;
  if (from) return `${from} 起`;
  if (to) return `截至 ${to}`;
  return "";
}
