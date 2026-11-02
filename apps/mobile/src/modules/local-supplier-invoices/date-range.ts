import type { InvoiceGridFilters } from "./types";

export interface InvoiceDateRangeValue {
  from?: string;
  to?: string;
}

export interface InvoiceDateRangeDisplay {
  kind: "all" | "from-only" | "to-only" | "range";
  text: string;
}

export interface FormatInvoiceDateRangeDisplayOptions {
  allDatesLabel?: string;
  formatFrom?: (date: string) => string;
  formatTo?: (date: string) => string;
  rangeSeparator?: string;
}

function trimDate(value?: string) {
  const trimmed = value?.trim();
  return trimmed || undefined;
}

function normalizeRange(range: InvoiceDateRangeValue): InvoiceDateRangeValue {
  const from = trimDate(range.from);
  const to = trimDate(range.to);
  return { from, to };
}

export function clearInvoiceDateRange(): InvoiceDateRangeValue {
  return {};
}

export function formatInvoiceDateRangeDisplay(
  range: InvoiceDateRangeValue,
  options: FormatInvoiceDateRangeDisplayOptions = {}
): InvoiceDateRangeDisplay {
  const { from, to } = normalizeRange(range);

  if (from && to) {
    return { kind: "range", text: `${from}${options.rangeSeparator ?? " ~ "}${to}` };
  }

  if (from) {
    return { kind: "from-only", text: options.formatFrom?.(from) ?? from };
  }

  if (to) {
    return { kind: "to-only", text: options.formatTo?.(to) ?? to };
  }

  return { kind: "all", text: options.allDatesLabel ?? "" };
}

export function selectInvoiceDateRange(
  current: InvoiceDateRangeValue,
  selectedDate: string
): InvoiceDateRangeValue {
  const next = trimDate(selectedDate);
  if (!next) {
    return normalizeRange(current);
  }

  const { from, to } = normalizeRange(current);

  if (from && to) {
    return { from: next };
  }

  if (from) {
    return next < from ? { from: next, to: from } : { from, to: next };
  }

  if (to) {
    return next <= to ? { from: next, to } : { from: to, to: next };
  }

  return { from: next };
}

export function toInvoiceDateRangeFilters(
  range: InvoiceDateRangeValue
): Pick<InvoiceGridFilters, "dateFrom" | "dateTo"> {
  const { from, to } = normalizeRange(range);
  return {
    ...(from ? { dateFrom: from } : {}),
    ...(to ? { dateTo: to } : {}),
  };
}

/** 列表默认只看最近 90 天，口径与 Web 一致（含今天在内往前数 90 个自然日）。 */
export const DEFAULT_INVOICE_DATE_RANGE_DAYS = 90;

function pad2(value: number) {
  return value.toString().padStart(2, "0");
}

function formatLocalDate(date: Date) {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
}

/**
 * 默认日期筛选：创建日期、近 90 天（设备本地日历）。
 * 用 new Date(y, m, d - N) 做日期加减，跨月、跨年、夏令时切换日都按日历天数走，不会因为 24h 毫秒差错一天。
 */
export function getDefaultInvoiceDateFilters(
  now: Date = new Date()
): Required<Pick<InvoiceGridFilters, "dateField" | "dateFrom" | "dateTo">> {
  const from = new Date(
    now.getFullYear(),
    now.getMonth(),
    now.getDate() - (DEFAULT_INVOICE_DATE_RANGE_DAYS - 1)
  );
  return {
    dateField: "createdAt",
    dateFrom: formatLocalDate(from),
    dateTo: formatLocalDate(now),
  };
}

const LOCAL_DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

function parseLocalDate(value: string) {
  const match = value.match(LOCAL_DATE_PATTERN);
  if (!match) {
    return null;
  }
  const year = Number(match[1]);
  const monthIndex = Number(match[2]) - 1;
  const day = Number(match[3]);
  const parsed = new Date(year, monthIndex, day);
  return parsed.getFullYear() === year && parsed.getMonth() === monthIndex && parsed.getDate() === day
    ? parsed
    : null;
}

/** 没有起始日期时的下界：早于任何一张进货单的创建时间即可。 */
const CREATED_AT_OPEN_START = new Date(2000, 0, 1);

/**
 * 把本地日期区间换算成后端创建时间筛选要的「时刻区间」[起, 止)。
 *
 * 关键逻辑：库里 CreatedAt 存的是 UTC，而用户选的是设备本地日期。后端 CreatedAt 只认 inRange 且两端都要有，
 * 所以这里把本地日期当天 0 点、结束日次日 0 点转成带时区的 ISO 时刻再传；
 * 这样悉尼 10-06 当天 00:00–24:00 创建的单都落在 10-06，不会因为时差错到前一天（与 Web buildInvoiceListDateFilter 同口径）。
 * 只选了一端时补另一端：只有起始 -> 止取明天 0 点（创建时间不会在未来）；只有结束 -> 起取 2000-01-01。
 * 两端都没有或日期无法解析时返回 null，调用方不加条件。
 */
export function toCreatedAtInstantRange(
  range: InvoiceDateRangeValue,
  now: Date = new Date()
): { filter: string; filterTo: string } | null {
  const { from, to } = normalizeRange(range);
  if (!from && !to) {
    return null;
  }

  const start = from ? parseLocalDate(from) : CREATED_AT_OPEN_START;
  if (!start) {
    return null;
  }
  let endExclusive: Date;
  if (to) {
    const end = parseLocalDate(to);
    if (!end) {
      return null;
    }
    endExclusive = new Date(end.getFullYear(), end.getMonth(), end.getDate() + 1);
  } else {
    endExclusive = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
  }

  return { filter: start.toISOString(), filterTo: endExclusive.toISOString() };
}
