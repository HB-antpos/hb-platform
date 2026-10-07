import { classifyDifference } from "./logic";
import type {
  DailyCloseCashCount,
  DailyCloseCounts,
  DailyCloseDataSource,
  DailyCloseDetail,
  DailyCloseDetailLevel,
  DailyCloseListItem,
  DailyCloseListPage,
  DailyCloseTender,
  DailyCloseTotals,
} from "./types";

/** 接口响应归一化（纯函数，不依赖 apiClient，便于在 Node 下测试）：缺字段、类型不对都落到安全默认值。 */
type Raw = Record<string, unknown>;

const asRaw = (value: unknown): Raw => (value && typeof value === "object" && !Array.isArray(value) ? (value as Raw) : {});
const str = (value: unknown): string | null => (typeof value === "string" && value.trim() ? value.trim() : null);
const num = (value: unknown): number => (typeof value === "number" && Number.isFinite(value) ? value : 0);
/** 可空金额：null / 缺失 / 非数字都是「没有这个值」，不能当成 0（0 是「已平」，null 是「无金额」）。 */
const optionalNumber = (value: unknown): number | null => (typeof value === "number" && Number.isFinite(value) ? value : null);
const rows = <T>(value: unknown, map: (row: Raw) => T | null): T[] =>
  (Array.isArray(value) ? value : []).map((row) => map(asRaw(row))).filter((row): row is T => row !== null);

const CLIENT_KINDS = ["Wpf", "Handheld", "Ipad"] as const;

/** 端类型规整大小写；未知值原样保留（界面直接显示原文）。 */
function normalizeClientKind(value: unknown): string {
  const raw = str(value) ?? "";
  return CLIENT_KINDS.find((kind) => kind.toLowerCase() === raw.toLowerCase()) ?? raw;
}

function normalizeDetailLevel(value: unknown, hasAmounts: boolean): DailyCloseDetailLevel {
  const raw = str(value)?.toLowerCase();
  if (raw === "full") return "Full";
  if (raw === "cashonly") return "CashOnly";
  if (raw === "traceonly") return "TraceOnly";
  // 未知明细级别按「不完整」处理：有金额当现金三项，没有就是只有保存记录，不会误显示成完整明细。
  return hasAmounts ? "CashOnly" : "TraceOnly";
}

function normalizeDataSource(value: unknown): DailyCloseDataSource {
  return str(value)?.toLowerCase() === "auditbackfill" ? "AuditBackfill" : "ClientUpload";
}

/** 营业日序列化为 yyyy-MM-dd；兼容带时间部分的写法。 */
const dateOnly = (value: unknown) => str(value)?.slice(0, 10) ?? "";

export function normalizeDailyCloseItem(raw: unknown): DailyCloseListItem | null {
  const r = asRaw(raw);
  const dailyCloseGuid = str(r.dailyCloseGuid);
  // 没有编号的记录无法进入详情，也无法去重，直接丢弃。
  if (!dailyCloseGuid) return null;
  const expectedCashAmount = optionalNumber(r.expectedCashAmount);
  const countedCashAmount = optionalNumber(r.countedCashAmount);
  const cashDifference = optionalNumber(r.cashDifference);
  const saveSequence = Math.max(1, Math.trunc(num(r.saveSequence)) || 1);
  return {
    dailyCloseGuid,
    storeCode: str(r.storeCode) ?? "",
    storeName: str(r.storeName),
    storeTimeZoneId: str(r.storeTimeZoneId),
    deviceCode: str(r.deviceCode) ?? "",
    clientKind: normalizeClientKind(r.clientKind),
    detailLevel: normalizeDetailLevel(r.detailLevel, expectedCashAmount !== null && countedCashAmount !== null && cashDifference !== null),
    dataSource: normalizeDataSource(r.dataSource),
    businessDate: dateOnly(r.businessDate),
    businessDateInferred: r.businessDateInferred === true,
    cashierId: str(r.cashierId) ?? "",
    cashierName: str(r.cashierName) ?? "",
    savedAtUtc: str(r.savedAtUtc) ?? "",
    orderCount: optionalNumber(r.orderCount),
    expectedCashAmount,
    countedCashAmount,
    cashDifference,
    cardNetAmount: optionalNumber(r.cardNetAmount),
    // 状态只由差额符号决定：金额、符号、颜色三者永远一致（后端同口径）。
    differenceKind: classifyDifference(cashDifference),
    saveSequence,
    saveCountInDay: Math.max(saveSequence, Math.trunc(num(r.saveCountInDay)) || 1),
  };
}

function normalizeCounts(raw: unknown): DailyCloseCounts {
  const r = asRaw(raw);
  return { all: num(r.all), short: num(r.short), over: num(r.over), even: num(r.even), none: num(r.none) };
}

function normalizeTotals(raw: unknown): DailyCloseTotals {
  const r = asRaw(raw);
  return { expectedCash: num(r.expectedCash), countedCash: num(r.countedCash), difference: num(r.difference) };
}

export function normalizeDailyCloseListPage(raw: unknown): DailyCloseListPage {
  const r = asRaw(raw);
  return {
    items: rows(r.items, normalizeDailyCloseItem),
    total: num(r.total),
    page: Math.max(1, Math.trunc(num(r.page)) || 1),
    pageSize: Math.trunc(num(r.pageSize)),
    counts: normalizeCounts(r.counts),
    totals: normalizeTotals(r.totals),
  };
}

function normalizeTender(r: Raw): DailyCloseTender | null {
  const method = str(r.method);
  if (!method) return null;
  return { method, salesAmount: num(r.salesAmount), refundAmount: num(r.refundAmount), netAmount: num(r.netAmount) };
}

function normalizeCashCount(r: Raw): DailyCloseCashCount | null {
  const denominationCents = optionalNumber(r.denominationCents);
  if (denominationCents === null || denominationCents <= 0) return null;
  const kind = str(r.kind)?.toLowerCase();
  return {
    denominationCents,
    quantity: Math.max(0, Math.trunc(num(r.quantity))),
    subtotalAmount: num(r.subtotalAmount),
    // 接口没给类型时按 5 元（500 分）为界，与后端汇总口径一致。
    kind: kind === "note" ? "Note" : kind === "coin" ? "Coin" : denominationCents >= 500 ? "Note" : "Coin",
  };
}

export function normalizeDailyCloseDetail(raw: unknown): DailyCloseDetail | null {
  const base = normalizeDailyCloseItem(raw);
  if (!base) return null;
  const r = asRaw(raw);
  return {
    ...base,
    periodFromUtc: str(r.periodFromUtc),
    periodToUtc: str(r.periodToUtc),
    appVersion: str(r.appVersion),
    returnQuantity: optionalNumber(r.returnQuantity),
    refundAmount: optionalNumber(r.refundAmount),
    tenders: rows(r.tenders, normalizeTender),
    cashCounts: rows(r.cashCounts, normalizeCashCount),
    noteSubtotal: optionalNumber(r.noteSubtotal),
    coinSubtotal: optionalNumber(r.coinSubtotal),
    receivedAtUtc: str(r.receivedAtUtc) ?? "",
  };
}
