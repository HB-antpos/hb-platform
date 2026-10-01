import { PERMISSIONS } from "@/shared/utils/access";
import type {
  PosOperationDeviceSystem,
  PosOperationLogDaySection,
  PosOperationLogFilters,
  PosOperationLogItem,
  PosOperationOutcome,
  PosOperationQuickFilter,
  PosOperationRangePreset,
} from "./types";

/** 入口权限，无前缀的历史写法由 access.ts 的别名组统一归一。 */
export const POS_OPERATION_AUDIT_VIEW_PERMISSION = PERMISSIONS.PosTerminal.AuditView;

export const POS_OPERATION_LOG_PAGE_SIZE = 50;
/** 自定义区间上限，防止一次拉取跨越审计表保留期的大范围数据。 */
export const POS_OPERATION_LOG_MAX_RANGE_DAYS = 90;

export const POS_OPERATION_OUTCOMES: PosOperationOutcome[] = ["Succeeded", "Denied", "Failed"];
export const POS_OPERATION_DEVICE_SYSTEMS: PosOperationDeviceSystem[] = ["Windows", "iPadOS", "Unknown"];
export const POS_OPERATION_RANGE_PRESETS: PosOperationRangePreset[] = [
  "today",
  "yesterday",
  "last7Days",
  "last30Days",
  "custom",
];

/** 操作类型按审计关注度分组；高风险组在筛选面板默认展开，其余折叠。 */
export const POS_OPERATION_TYPE_GROUPS: readonly {
  key: "risk" | "sales" | "payment" | "session" | "system";
  types: readonly string[];
}[] = [
  {
    key: "risk",
    types: [
      "CART_ITEM_PRICE_CHANGE",
      "CART_LINE_DISCOUNT_CHANGE",
      "CART_ORDER_DISCOUNT_CHANGE",
      "SALE_VOID",
      "RETURN_REFUND_COMPLETE",
      "ORDER_CANCEL",
      "CASH_DRAWER_OPEN",
      "CARD_PAYMENT_SUPERVISOR_RESOLUTION",
      "PERMISSION_OVERRIDE",
    ],
  },
  {
    key: "sales",
    types: [
      "SALE_COMPLETE",
      "CART_ITEM_ADD",
      "CART_ITEM_REMOVE",
      "CART_ITEM_QUANTITY_CHANGE",
      "CART_CLEAR",
      "ORDER_HOLD",
      "ORDER_RECALL",
      "RECEIPT_REPRINT",
    ],
  },
  {
    key: "payment",
    types: [
      "PAYMENT_TENDER_ADD",
      "PAYMENT_TENDER_REMOVE",
      "PAYMENT_CANCEL",
      "INSTALLMENT_REPAYMENT_COMPLETE",
      "INSTALLMENT_REPAYMENT_CANCEL",
      "INSTALLMENT_PICKUP_CONFIRM",
    ],
  },
  {
    key: "session",
    types: [
      "CASHIER_LOGIN",
      "CASHIER_LOGOUT",
      "DAILY_CLOSE_SAVE",
      "DAILY_CLOSE_REPRINT",
      "LINKLY_SETTLEMENT",
      "LINKLY_SETTLEMENT_REPRINT",
    ],
  },
  {
    // 收银机系统级操作：影响数据去向、设备归属或远程控制，单独成组便于巡检。
    key: "system",
    types: [
      "API_SERVER_CHANGE",
      "DEVICE_REREGISTER",
      "REMOTE_MAINTENANCE_INSTALL",
      "CATALOG_RESET",
      "TEST_SALES_DATA_RESET",
    ],
  },
];

export const POS_OPERATION_TYPES: readonly string[] = POS_OPERATION_TYPE_GROUPS.flatMap(
  (group) => group.types,
);

/** 操作类型常量到文案键的映射：CART_ITEM_ADD → cartItemAdd。 */
export function operationTypeI18nKey(operationType: string): string {
  return operationType
    .toLowerCase()
    .replace(/_([a-z0-9])/g, (_, char: string) => char.toUpperCase());
}

export function isKnownOperationType(operationType: string): boolean {
  return POS_OPERATION_TYPES.includes(operationType);
}

export function canViewPosOperationLogs(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
): boolean {
  return isAuthenticated && hasPermission(POS_OPERATION_AUDIT_VIEW_PERMISSION);
}

export function createDefaultPosOperationLogFilters(
  now: Date = new Date(),
): PosOperationLogFilters {
  const today = formatLocalDate(now);
  return {
    preset: "today",
    startDate: today,
    endDate: today,
    storeCode: null,
    cashierKeyword: "",
    deviceCode: "",
    deviceSystem: null,
    operationType: null,
    outcome: null,
    emergencyOverrideOnly: false,
    offlineCachedOnly: false,
    productKeyword: "",
    orderGuid: "",
    keyword: "",
  };
}

const pad = (value: number) => String(value).padStart(2, "0");

/** 设备本地日期，审计以门店员工的本地日为口径而不是 UTC 日。 */
export function formatLocalDate(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export function parseLocalDate(value: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return null;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(year, month - 1, day, 0, 0, 0, 0);
  // 排除 2026-02-30 这类会被 Date 自动进位的非法日期。
  return date.getFullYear() === year &&
    date.getMonth() === month - 1 &&
    date.getDate() === day
    ? date
    : null;
}

function startOfLocalDay(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate(), 0, 0, 0, 0);
}

function addDays(date: Date, days: number): Date {
  const next = new Date(date);
  next.setDate(next.getDate() + days);
  return next;
}

export interface PosOperationRangeValidation {
  ok: boolean;
  dayCount: number;
  reason?: "format" | "order" | "tooLong";
}

export function validateCustomRange(
  startDate: string,
  endDate: string,
): PosOperationRangeValidation {
  const start = parseLocalDate(startDate);
  const end = parseLocalDate(endDate);
  if (!start || !end) return { ok: false, dayCount: 0, reason: "format" };
  if (start > end) return { ok: false, dayCount: 0, reason: "order" };
  const dayCount = Math.round((end.getTime() - start.getTime()) / 86_400_000) + 1;
  if (dayCount > POS_OPERATION_LOG_MAX_RANGE_DAYS) {
    return { ok: false, dayCount, reason: "tooLong" };
  }
  return { ok: true, dayCount };
}

export interface PosOperationResolvedRange {
  fromUtc: string;
  toUtc: string;
  /** 本地展示用 */
  startDate: string;
  endDate: string;
}

/**
 * 预设区间按设备本地日推导。今天/近 N 天的终点是"现在"，昨天的终点是今日零点前一毫秒，
 * 自定义区间取结束日 23:59:59.999。
 */
export function resolvePosOperationRange(
  filters: Pick<PosOperationLogFilters, "preset" | "startDate" | "endDate">,
  now: Date = new Date(),
): PosOperationResolvedRange | null {
  const todayStart = startOfLocalDay(now);
  let start: Date;
  let end: Date;
  switch (filters.preset) {
    case "today":
      start = todayStart;
      end = now;
      break;
    case "yesterday":
      start = addDays(todayStart, -1);
      end = new Date(todayStart.getTime() - 1);
      break;
    case "last7Days":
      start = addDays(todayStart, -6);
      end = now;
      break;
    case "last30Days":
      start = addDays(todayStart, -29);
      end = now;
      break;
    case "custom": {
      if (!validateCustomRange(filters.startDate, filters.endDate).ok) return null;
      start = parseLocalDate(filters.startDate)!;
      end = new Date(addDays(parseLocalDate(filters.endDate)!, 1).getTime() - 1);
      break;
    }
  }
  return {
    fromUtc: start.toISOString(),
    toUtc: end.toISOString(),
    startDate: formatLocalDate(start),
    endDate: formatLocalDate(end),
  };
}

export type PosOperationLogQueryParams = Record<string, string | number | boolean>;

const trimmed = (value: string) => value.trim();

/** 列表与汇总共用同一份查询参数，保证计数与列表口径一致。 */
export function buildPosOperationLogQueryParams(
  filters: PosOperationLogFilters,
  now: Date = new Date(),
): PosOperationLogQueryParams | null {
  const range = resolvePosOperationRange(filters, now);
  if (!range) return null;
  const params: PosOperationLogQueryParams = {
    fromUtc: range.fromUtc,
    toUtc: range.toUtc,
    sortBy: "occurredAtUtc",
    sortOrder: "desc",
  };
  if (filters.storeCode) params.storeCode = filters.storeCode;
  if (trimmed(filters.cashierKeyword)) params.cashierKeyword = trimmed(filters.cashierKeyword);
  if (trimmed(filters.deviceCode)) params.deviceCode = trimmed(filters.deviceCode);
  if (filters.deviceSystem) params.deviceSystem = filters.deviceSystem;
  if (filters.operationType) params.operationType = filters.operationType;
  if (filters.outcome) params.outcome = filters.outcome;
  if (filters.emergencyOverrideOnly) params.isEmergencyOverride = true;
  if (filters.offlineCachedOnly) params.isOfflineCached = true;
  if (trimmed(filters.productKeyword)) params.productKeyword = trimmed(filters.productKeyword);
  if (trimmed(filters.orderGuid)) params.orderGuid = trimmed(filters.orderGuid);
  if (trimmed(filters.keyword)) params.keyword = trimmed(filters.keyword);
  return params;
}

/** 筛选面板角标：时间预设不算，只统计用户额外收窄的条件数。 */
export function countActivePosOperationFilters(filters: PosOperationLogFilters): number {
  let count = 0;
  if (filters.preset !== "today") count++;
  if (filters.storeCode) count++;
  if (trimmed(filters.cashierKeyword)) count++;
  if (trimmed(filters.deviceCode)) count++;
  if (filters.deviceSystem) count++;
  if (filters.operationType) count++;
  if (filters.outcome) count++;
  if (filters.emergencyOverrideOnly) count++;
  if (filters.offlineCachedOnly) count++;
  if (trimmed(filters.productKeyword)) count++;
  if (trimmed(filters.orderGuid)) count++;
  if (trimmed(filters.keyword)) count++;
  return count;
}

export function resolveQuickFilter(filters: PosOperationLogFilters): PosOperationQuickFilter {
  if (filters.outcome) return filters.outcome;
  if (filters.emergencyOverrideOnly) return "emergencyOverride";
  return "all";
}

/** 快捷过滤互斥：选结果时清掉紧急覆盖，选紧急覆盖时清掉结果。 */
export function applyQuickFilter(
  filters: PosOperationLogFilters,
  quick: PosOperationQuickFilter,
): PosOperationLogFilters {
  if (quick === "all") {
    return { ...filters, outcome: null, emergencyOverrideOnly: false };
  }
  if (quick === "emergencyOverride") {
    return { ...filters, outcome: null, emergencyOverrideOnly: true };
  }
  return { ...filters, outcome: quick, emergencyOverrideOnly: false };
}

export function formatLogTime(isoUtc: string): string {
  const date = new Date(isoUtc);
  if (Number.isNaN(date.getTime())) return "--:--:--";
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

export function formatLogDateTime(isoUtc: string): string {
  const date = new Date(isoUtc);
  if (Number.isNaN(date.getTime())) return "—";
  return `${pad(date.getDate())}/${pad(date.getMonth() + 1)}/${date.getFullYear()} ${formatLogTime(isoUtc)}`;
}

export function formatDisplayDate(localDate: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(localDate);
  return match ? `${match[3]}/${match[2]}/${match[1]}` : localDate;
}

/** 按设备本地日分组，保持输入顺序（后端已按时间倒序）。 */
export function groupPosOperationLogsByDay(
  items: PosOperationLogItem[],
  now: Date = new Date(),
): PosOperationLogDaySection[] {
  const todayKey = formatLocalDate(now);
  const yesterdayKey = formatLocalDate(addDays(startOfLocalDay(now), -1));
  const sections: PosOperationLogDaySection[] = [];
  for (const item of items) {
    const date = new Date(item.occurredAtUtc);
    const dayKey = Number.isNaN(date.getTime()) ? "unknown" : formatLocalDate(date);
    const last = sections[sections.length - 1];
    if (last && last.dayKey === dayKey) {
      last.items.push(item);
      continue;
    }
    sections.push({
      dayKey,
      relative: dayKey === todayKey ? "today" : dayKey === yesterdayKey ? "yesterday" : null,
      dateLabel: dayKey === "unknown" ? "—" : formatDisplayDate(dayKey),
      items: [item],
    });
  }
  return sections;
}

function currencySymbol(currencyCode: string): string {
  return currencyCode === "AUD" ? "A$" : `${currencyCode} `;
}

export function formatMoney(value: number | null, currencyCode = "AUD"): string {
  if (value == null || !Number.isFinite(value)) return "—";
  const abs = Math.abs(value).toLocaleString("en-AU", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  return `${value < 0 ? "−" : ""}${currencySymbol(currencyCode)}${abs}`;
}

/** 变化量始终带符号，0 显示为 "0.00" 而不是 "+0.00"。 */
export function formatSignedMoney(value: number | null, currencyCode = "AUD"): string {
  if (value == null || !Number.isFinite(value)) return "—";
  if (value > 0) return `+${formatMoney(value, currencyCode)}`;
  return formatMoney(value, currencyCode);
}

export function formatQuantity(value: number | null): string {
  if (value == null || !Number.isFinite(value)) return "—";
  return Number.isInteger(value) ? String(value) : value.toFixed(3).replace(/\.?0+$/, "");
}

/** 列表行右侧的主金额：优先变化量，其次付款金额，最后实收后值。 */
export function resolveRowAmount(item: PosOperationLogItem): number | null {
  if (item.amountDelta != null) return item.amountDelta;
  if (item.paymentAmount != null) return item.paymentAmount;
  return item.afterActual;
}

export type PosOperationRowSummary =
  | { kind: "reason"; reasonCode: string | null; message: string | null }
  | { kind: "product"; name: string; extraCount: number }
  | { kind: "receipt"; receiptNumber: string; paymentMethod: string | null }
  | { kind: "payment"; paymentMethod: string }
  | { kind: "none" };

/** 第三行摘要：异常结果先看原因，正常结果看商品，再退到小票或付款方式。 */
export function describeRowSummary(item: PosOperationLogItem): PosOperationRowSummary {
  if (item.outcome !== "Succeeded" && (item.reasonCode || item.safeMessage)) {
    return { kind: "reason", reasonCode: item.reasonCode, message: item.safeMessage };
  }
  if (item.primaryProduct) {
    return {
      kind: "product",
      name: item.primaryProduct,
      extraCount: Math.max(item.productCount - 1, 0),
    };
  }
  if (item.receiptNumber) {
    return { kind: "receipt", receiptNumber: item.receiptNumber, paymentMethod: item.paymentMethod };
  }
  if (item.paymentMethod) return { kind: "payment", paymentMethod: item.paymentMethod };
  return { kind: "none" };
}

export function shortenIdentifier(value: string | null, keep = 4): string {
  if (!value) return "—";
  const compact = value.replace(/-/g, "");
  if (compact.length <= keep * 2 + 1) return value;
  return `${compact.slice(0, keep)}…${compact.slice(-keep)}`;
}

/** 上报延迟：设备发生时间到服务端接收时间的秒数，负值按 0 处理。 */
export function reportingDelaySeconds(item: Pick<PosOperationLogItem, "occurredAtUtc" | "receivedAtUtc">): number | null {
  const occurred = new Date(item.occurredAtUtc).getTime();
  const received = new Date(item.receivedAtUtc).getTime();
  if (Number.isNaN(occurred) || Number.isNaN(received)) return null;
  return Math.max(0, Math.round((received - occurred) / 1000));
}

export interface PosOperationMoneyRow {
  key: "gross" | "discount" | "actual";
  before: number | null;
  after: number | null;
  delta: number | null;
}

/** 详情页金额表：三行都为空时返回空数组，界面据此隐藏整个区块。 */
export function buildMoneyRows(item: PosOperationLogItem): PosOperationMoneyRow[] {
  const rows: PosOperationMoneyRow[] = [
    { key: "gross", before: item.beforeGross, after: item.afterGross, delta: diff(item.beforeGross, item.afterGross) },
    { key: "discount", before: item.beforeDiscount, after: item.afterDiscount, delta: diff(item.beforeDiscount, item.afterDiscount) },
    { key: "actual", before: item.beforeActual, after: item.afterActual, delta: item.amountDelta ?? diff(item.beforeActual, item.afterActual) },
  ];
  return rows.some((row) => row.before != null || row.after != null || row.delta != null)
    ? rows
    : [];
}

function diff(before: number | null, after: number | null): number | null {
  if (before == null || after == null) return null;
  return Math.round((after - before) * 100) / 100;
}

/** 深链参数解析：详情页"该订单全部操作 / 该员工今日操作"回到列表时带入。 */
export function applyDeepLinkParams(
  filters: PosOperationLogFilters,
  params: { orderGuid?: string; cashier?: string; storeCode?: string; preset?: string },
): PosOperationLogFilters {
  const next = { ...filters };
  if (params.orderGuid?.trim()) {
    next.orderGuid = params.orderGuid.trim();
    // 订单可能跨天完成，按订单追溯时放宽到近 30 天。
    next.preset = "last30Days";
  }
  if (params.cashier?.trim()) next.cashierKeyword = params.cashier.trim();
  if (params.storeCode?.trim()) next.storeCode = params.storeCode.trim();
  if (params.preset && POS_OPERATION_RANGE_PRESETS.includes(params.preset as PosOperationRangePreset) && params.preset !== "custom") {
    next.preset = params.preset as PosOperationRangePreset;
  }
  return next;
}
