import { formatMoney } from "@/modules/reports/format";
import { addDays, diffDays, isValidDateString, weekdayOf } from "@/modules/store-cash/dates";
import type {
  DailyCloseCashCount,
  DailyCloseClientKind,
  DailyCloseCounts,
  DailyCloseDetail,
  DailyCloseDifferenceKind,
  DailyCloseFilters,
  DailyCloseListItem,
  DailyCloseListPage,
  DailyCloseListParams,
  DailyCloseRangePreset,
  DailyCloseStatusTab,
  DailyCloseTender,
  DailyCloseTotals,
} from "./types";

/** 移动端每页条数（后端上限 100）：卡片较高，30 条足够填满两屏，触底再翻页。 */
export const DAILY_CLOSE_PAGE_SIZE = 30;
/** 营业日区间最长含首尾 93 个自然日，与后端 DailyCloseQueryService.MaxRangeDays 一致。 */
export const DAILY_CLOSE_MAX_RANGE_DAYS = 93;
/** 门店时区缺失时的回退口径（仓库其他处同样回退悉尼）。 */
export const DAILY_CLOSE_DEFAULT_TIME_ZONE = "Australia/Sydney";
/** 终端选项只取最近这么多天内出现过的终端。 */
export const DAILY_CLOSE_DEVICE_LOOKBACK_DAYS = 93;
export const DAILY_CLOSE_RANGE_PRESETS: DailyCloseRangePreset[] = ["today", "yesterday", "last7", "thisMonth", "custom"];
export const DAILY_CLOSE_STATUS_TABS: DailyCloseStatusTab[] = ["all", "short", "over", "even"];
export const DAILY_CLOSE_CLIENT_KINDS: DailyCloseClientKind[] = ["Wpf", "Handheld", "Ipad"];
/** 收银员关键字上限：与员工操作日志筛选一致，避免超长输入。 */
export const DAILY_CLOSE_KEYWORD_MAX_LENGTH = 100;

// ───────────────────────── 金额 ─────────────────────────

/** 金额按「分」取整比较与求和，避免 0.1 + 0.2 这类浮点误差让合计多出 0.01。 */
export function toCents(value: number | null | undefined): number {
  return typeof value === "number" && Number.isFinite(value) ? Math.round(value * 100) : 0;
}

/**
 * 现金差额分类：差额 = 实点 − 应有。负为短款、正为长款、0 为已平、没有金额为 none。
 * 以「分」为单位判断，-0.004 这类不可能出现的亚分值也不会显示成「−$0.00」。
 */
export function classifyDifference(difference: number | null | undefined): DailyCloseDifferenceKind {
  if (typeof difference !== "number" || !Number.isFinite(difference)) return "none";
  const cents = toCents(difference);
  if (cents < 0) return "short";
  if (cents > 0) return "over";
  return "even";
}

/** AUD 金额：$1,842.30；负数用真正的减号「−」（U+2212）；没有金额显示「—」。 */
export function formatDailyCloseMoney(value: number | null | undefined): string {
  if (typeof value !== "number" || !Number.isFinite(value)) return "—";
  const cents = toCents(value);
  return `${cents < 0 ? "−" : ""}${formatMoney(Math.abs(cents) / 100)}`;
}

/** 差额：带正负号（+$2.00 / −$3.50），已平显示 $0.00，没有金额显示「—」。 */
export function formatDailyCloseDifference(value: number | null | undefined): string {
  if (typeof value !== "number" || !Number.isFinite(value)) return "—";
  const cents = toCents(value);
  return `${cents > 0 ? "+" : ""}${formatDailyCloseMoney(value)}`;
}

/** 数量（订单数、退货件数）：千分位，空值显示「—」。 */
export function formatDailyCloseCount(value: number | null | undefined): string {
  if (typeof value !== "number" || !Number.isFinite(value)) return "—";
  return value.toLocaleString("en-AU", { maximumFractionDigits: 2 });
}

/** 面额：100 分及以上显示元（$100、$2），小面额显示分（50c、5c）。 */
export function formatDenomination(denominationCents: number): string {
  if (!Number.isFinite(denominationCents)) return "—";
  if (denominationCents >= 100) {
    const dollars = denominationCents / 100;
    return `$${Number.isInteger(dollars) ? dollars : dollars.toFixed(2)}`;
  }
  return `${denominationCents}c`;
}

// ───────────────────────── 日期与时区 ─────────────────────────

const zoneFormatterCache = new Map<string, Intl.DateTimeFormat>();

function zonedFormatter(timeZone: string) {
  let formatter = zoneFormatterCache.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat("en-CA", {
      timeZone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      hourCycle: "h23",
    });
    zoneFormatterCache.set(timeZone, formatter);
  }
  return formatter;
}

export interface ZonedMoment {
  /** yyyy-MM-dd */
  date: string;
  /** HH:mm */
  time: string;
}

/**
 * UTC 时间按门店时区换算成日期与时分。门店时区无效时回退悉尼；
 * 运行时连悉尼都不支持（缺 Intl 时区数据）时按 UTC 显示，保证至少有值。
 * 格式化器按时区缓存：列表每张卡片都要换算，重复构造 Intl 对象很慢。
 */
export function toZonedMoment(utcIso: string | null | undefined, timeZoneId?: string | null): ZonedMoment | null {
  if (!utcIso) return null;
  const date = new Date(utcIso);
  if (Number.isNaN(date.getTime())) return null;
  const zones = [timeZoneId?.trim(), DAILY_CLOSE_DEFAULT_TIME_ZONE].filter((zone): zone is string => Boolean(zone));
  for (const zone of zones) {
    try {
      const parts: Record<string, string> = {};
      for (const part of zonedFormatter(zone).formatToParts(date)) parts[part.type] = part.value;
      return { date: `${parts.year}-${parts.month}-${parts.day}`, time: `${parts.hour}:${parts.minute}` };
    } catch {
      // 时区 ID 无效：试下一个候选
    }
  }
  const iso = date.toISOString();
  return { date: iso.slice(0, 10), time: iso.slice(11, 16) };
}

/** 门店「今天」：用悉尼日期，后端默认窗口同口径（悉尼日期不会落后于任何一家门店）。 */
export function todayInSydney(now: Date = new Date()): string {
  return toZonedMoment(now.toISOString(), DAILY_CLOSE_DEFAULT_TIME_ZONE)?.date ?? now.toISOString().slice(0, 10);
}

/**
 * 保存时间标签：与营业日同一天只显示「21:48」，跨天（过零点保存、补录）显示「10-07 00:12」，
 * 一眼能看出保存时间和营业日不在同一天。
 */
export function formatSavedLabel(utcIso: string, timeZoneId: string | null | undefined, businessDate: string): string {
  const moment = toZonedMoment(utcIso, timeZoneId);
  if (!moment) return "—";
  return moment.date === businessDate ? moment.time : `${moment.date.slice(5)} ${moment.time}`;
}

/** 完整日期时间「2026-10-06 21:48」，用于明细里的统计时段与上传时间。 */
export function formatZonedDateTime(utcIso: string | null | undefined, timeZoneId: string | null | undefined): string {
  const moment = toZonedMoment(utcIso, timeZoneId);
  return moment ? `${moment.date} ${moment.time}` : "—";
}

export interface BusinessDateParts {
  year: number;
  month: number;
  day: number;
  /** 0 = 周日 … 6 = 周六，文案键 weekdays.N 取对应的星期。 */
  weekday: number;
}

/** 营业日拆成年月日与星期；无效日期返回 null（界面回退显示原字符串）。 */
export function describeBusinessDate(businessDate: string): BusinessDateParts | null {
  if (!isValidDateString(businessDate)) return null;
  const weekday = weekdayOf(businessDate);
  if (weekday === null) return null;
  return {
    year: Number(businessDate.slice(0, 4)),
    month: Number(businessDate.slice(5, 7)),
    day: Number(businessDate.slice(8, 10)),
    weekday,
  };
}

// ───────────────────────── 筛选 ─────────────────────────

export function createDefaultDailyCloseFilters(): DailyCloseFilters {
  return {
    storeCodes: [],
    preset: "last7",
    customFrom: "",
    customTo: "",
    clientKind: null,
    deviceCode: null,
    keyword: "",
    status: "all",
  };
}

export interface DailyCloseRange {
  from: string;
  to: string;
}

/** 预设转营业日闭区间（含首尾）：今天 / 昨天 / 近 7 天（含今天）/ 本月（1 号到今天）/ 自定义。 */
export function resolveDailyCloseRange(filters: Pick<DailyCloseFilters, "preset" | "customFrom" | "customTo">, today: string): DailyCloseRange {
  switch (filters.preset) {
    case "today":
      return { from: today, to: today };
    case "yesterday": {
      const yesterday = addDays(today, -1);
      return { from: yesterday, to: yesterday };
    }
    case "thisMonth":
      return { from: `${today.slice(0, 8)}01`, to: today };
    case "custom":
      return { from: filters.customFrom, to: filters.customTo };
    case "last7":
    default:
      return { from: addDays(today, -6), to: today };
  }
}

/** 自定义区间在筛选 chip 上的显示：和「今天」同一年时省略年份（10-01 → 10-07），跨年才带上，避免 chip 被长日期撑爆。 */
export function formatDailyCloseRangeDates(range: DailyCloseRange, today: string): DailyCloseRange {
  const year = `${today.slice(0, 4)}-`;
  const short = (date: string) => (date.startsWith(year) ? date.slice(5) : date);
  return { from: short(range.from), to: short(range.to) };
}

export type DailyCloseRangeIssue = "invalid" | "reversed" | "tooLong";

/** 校验营业日区间：日期合法、起不晚于止、含首尾不超过 93 天（超限后端会 400 INVALID_QUERY，这里提前拦住）。 */
export function validateDailyCloseRange(range: DailyCloseRange): DailyCloseRangeIssue | null {
  if (!isValidDateString(range.from) || !isValidDateString(range.to)) return "invalid";
  if (range.from > range.to) return "reversed";
  const span = diffDays(range.to, range.from);
  return span !== null && span + 1 > DAILY_CLOSE_MAX_RANGE_DAYS ? "tooLong" : null;
}

/** 当前预设下的区间问题；非自定义预设恒合法。 */
export function validateDailyCloseFilters(filters: DailyCloseFilters, today: string): DailyCloseRangeIssue | null {
  return validateDailyCloseRange(resolveDailyCloseRange(filters, today));
}

function uniqueStoreCodes(codes: readonly string[]) {
  return [...new Set(codes.map((code) => code.trim()).filter(Boolean))];
}

/**
 * 列表查询参数（接口字段名）。区间不合法返回 null，调用方不发请求。
 * 终端只在恰好选了一家分店时带上：多店或全部分店时同一终端编号会跨店重名，过滤没有意义。
 */
export function buildDailyCloseListParams(
  filters: DailyCloseFilters,
  page: number,
  pageSize: number,
  today: string,
): DailyCloseListParams | null {
  const range = resolveDailyCloseRange(filters, today);
  if (validateDailyCloseRange(range)) return null;
  const params: DailyCloseListParams = {
    businessDateFrom: range.from,
    businessDateTo: range.to,
    page,
    pageSize,
  };
  const stores = uniqueStoreCodes(filters.storeCodes);
  if (stores.length > 0) params.storeCodes = stores.join(",");
  if (stores.length === 1 && filters.deviceCode?.trim()) params.deviceCode = filters.deviceCode.trim();
  if (filters.clientKind) params.clientKind = filters.clientKind;
  const keyword = filters.keyword.trim();
  if (keyword) params.keyword = keyword.slice(0, DAILY_CLOSE_KEYWORD_MAX_LENGTH);
  if (filters.status !== "all") params.status = filters.status;
  return params;
}

/** 已生效的非默认筛选项数（分店、营业日、来源、终端、收银员各算一项），用于顶栏筛选图标角标。状态页签不算。 */
export function countActiveDailyCloseFilters(filters: DailyCloseFilters): number {
  return [
    uniqueStoreCodes(filters.storeCodes).length > 0,
    filters.preset !== "last7",
    Boolean(filters.clientKind),
    Boolean(filters.deviceCode?.trim()),
    Boolean(filters.keyword.trim()),
  ].filter(Boolean).length;
}

/** 点选/取消一家分店；分店变化后终端选项不再可靠，一并清空。 */
export function toggleDailyCloseStore(filters: DailyCloseFilters, storeCode: string): DailyCloseFilters {
  const code = storeCode.trim();
  if (!code) return filters;
  const storeCodes = filters.storeCodes.includes(code)
    ? filters.storeCodes.filter((item) => item !== code)
    : [...filters.storeCodes, code];
  return { ...filters, storeCodes, deviceCode: null };
}

/** 「全部分店」：清空分店选择（后端按账号可见分店收口）。 */
export function clearDailyCloseStores(filters: DailyCloseFilters): DailyCloseFilters {
  return { ...filters, storeCodes: [], deviceCode: null };
}

/** 选了「自定义」时先用当前预设的区间做初值，用户在此基础上改，而不是从空白开始。 */
export function selectDailyClosePreset(filters: DailyCloseFilters, preset: DailyCloseRangePreset, today: string): DailyCloseFilters {
  if (preset !== "custom" || filters.preset === "custom") return { ...filters, preset };
  const range = resolveDailyCloseRange(filters, today);
  return { ...filters, preset, customFrom: range.from, customTo: range.to };
}

/** 终端只能在选定单个分店后选择。 */
export function canPickDailyCloseDevice(filters: Pick<DailyCloseFilters, "storeCodes">): boolean {
  return uniqueStoreCodes(filters.storeCodes).length === 1;
}

/** 范围按钮上的分店摘要：全部 / 一家显示名称 / 多家显示「首家名称 等 N 家」。 */
export function summarizeDailyCloseStores(codes: readonly string[], names: ReadonlyMap<string, string>) {
  const stores = uniqueStoreCodes(codes);
  if (stores.length === 0) return { key: "scope.allStores" as const };
  const first = names.get(stores[0]) || stores[0];
  return stores.length === 1
    ? { key: "scope.oneStore" as const, params: { store: first } }
    : { key: "scope.manyStores" as const, params: { store: first, count: String(stores.length) } };
}

/** 终端选项：从已有日结里取指定分店出现过的终端，加上当前已选的，去重后按自然序排。 */
export function collectDeviceCodes(items: readonly Pick<DailyCloseListItem, "storeCode" | "deviceCode">[], storeCode: string, selected?: string | null): string[] {
  const codes = new Set<string>();
  items.forEach((item) => {
    if (item.storeCode === storeCode && item.deviceCode.trim()) codes.add(item.deviceCode.trim());
  });
  if (selected?.trim()) codes.add(selected.trim());
  return [...codes].sort((a, b) => a.localeCompare(b, "en", { numeric: true }));
}

// ───────────────────────── 列表汇总 ─────────────────────────

/** 状态页签的计数（不受状态页签自身影响，其余筛选生效）；无金额只计入「全部」。 */
export function statusTabCounts(counts: DailyCloseCounts): Record<DailyCloseStatusTab, number> {
  return { all: counts.all, short: counts.short, over: counts.over, even: counts.even };
}

export interface DailyCloseSummaryView {
  /** 当前筛选下是否有带金额的记录；没有时摘要卡显示「—」而不是 $0.00。 */
  hasAmounts: boolean;
  kind: DailyCloseDifferenceKind;
  difference: number;
  expectedCash: number;
  countedCash: number;
  /** 「全部」页签下另有多少份无金额记录（不计入合计，摘要卡里提示）。 */
  noAmountCount: number;
}

/**
 * 摘要卡：净差额 = 实点合计 − 应有合计。无金额记录不计入合计（后端已按此口径），
 * 带金额记录数按状态页签推出：全部 = 短款 + 长款 + 已平，其余页签就是该状态的记录数。
 */
export function summarizeDailyCloseTotals(counts: DailyCloseCounts, totals: DailyCloseTotals, status: DailyCloseStatusTab): DailyCloseSummaryView {
  const amountRows = status === "all" ? counts.short + counts.over + counts.even : counts[status];
  const hasAmounts = amountRows > 0;
  return {
    hasAmounts,
    kind: hasAmounts ? classifyDifference(totals.difference) : "none",
    difference: totals.difference,
    expectedCash: totals.expectedCash,
    countedCash: totals.countedCash,
    noAmountCount: status === "all" ? counts.none : 0,
  };
}

export interface DailyCloseSection {
  /** 营业日，同时作为 SectionList 的分组键。 */
  key: string;
  businessDate: string;
  count: number;
  /** 分组内带金额的记录数。 */
  amountCount: number;
  /** 分组内差额合计；没有带金额的记录时为 null。 */
  difference: number | null;
  /** 最后一组在还有下一页时可能只加载了一部分，份数与差额合计要等加载完整才准确。 */
  partial: boolean;
  data: DailyCloseListItem[];
}

/**
 * 按营业日分组（保持接口顺序：营业日降序、保存时间降序）。
 * complete = 全部分页都已加载；未加载完时最后一组标记 partial，界面不显示它的差额合计。
 */
export function groupDailyClosesByBusinessDate(items: readonly DailyCloseListItem[], complete: boolean): DailyCloseSection[] {
  const sections: DailyCloseSection[] = [];
  const byDate = new Map<string, DailyCloseSection>();
  const cents = new Map<string, number>();
  items.forEach((item) => {
    let section = byDate.get(item.businessDate);
    if (!section) {
      section = { key: item.businessDate, businessDate: item.businessDate, count: 0, amountCount: 0, difference: null, partial: false, data: [] };
      byDate.set(item.businessDate, section);
      sections.push(section);
    }
    section.data.push(item);
    section.count += 1;
    if (item.cashDifference !== null) {
      section.amountCount += 1;
      cents.set(item.businessDate, (cents.get(item.businessDate) ?? 0) + toCents(item.cashDifference));
    }
  });
  sections.forEach((section) => {
    section.difference = section.amountCount > 0 ? (cents.get(section.key) ?? 0) / 100 : null;
  });
  if (!complete && sections.length > 0) sections[sections.length - 1].partial = true;
  return sections;
}

/** 合并已加载的分页：翻页期间有新日结上传会让相邻页重复，按日结编号去重，先出现的保留。 */
export function mergeDailyClosePages(pages: readonly Pick<DailyCloseListPage, "items">[]): DailyCloseListItem[] {
  const seen = new Set<string>();
  const merged: DailyCloseListItem[] = [];
  pages.forEach((page) =>
    page.items.forEach((item) => {
      if (seen.has(item.dailyCloseGuid)) return;
      seen.add(item.dailyCloseGuid);
      merged.push(item);
    }),
  );
  return merged;
}

/** 下一页页码：已加载条数不足总数且最后一页非空时翻页，否则结束（空页防止总数过时造成无限翻页）。 */
export function nextDailyClosePage(lastPage: Pick<DailyCloseListPage, "items" | "total" | "page">, loadedCount: number): number | undefined {
  if (lastPage.items.length === 0) return undefined;
  return loadedCount < lastPage.total ? lastPage.page + 1 : undefined;
}

// ───────────────────────── 记录标记 ─────────────────────────

/** 端类型对应的文案键（source.wpf / source.handheld / source.ipad）；未知端类型返回 null，界面直接显示原文。 */
export function clientKindLabelKey(clientKind: string): "wpf" | "handheld" | "ipad" | null {
  const kind = clientKind.trim().toLowerCase();
  return kind === "wpf" ? "wpf" : kind === "handheld" ? "handheld" : kind === "ipad" ? "ipad" : null;
}


/** 同日多次保存：saveCountInDay > 1 时返回「第 N 次」的 N，否则 null。全部列出，不去重。 */
export function saveSequenceMark(item: Pick<DailyCloseListItem, "saveSequence" | "saveCountInDay">): number | null {
  return item.saveCountInDay > 1 ? item.saveSequence : null;
}

/** 历史补录：数据来自旧操作日志回填，或明细不完整（detailLevel 不是 Full）。 */
export function isBackfilledRecord(item: Pick<DailyCloseListItem, "dataSource" | "detailLevel">): boolean {
  return item.dataSource === "AuditBackfill" || item.detailLevel !== "Full";
}

export type DailyCloseNoticeKind = "cashOnly" | "traceOnly" | "generic";

export interface DailyCloseNotice {
  kind: DailyCloseNoticeKind;
  /** 营业日是按保存时间推算的：提示条里多一句说明。 */
  inferred: boolean;
}

/** 明细顶部的蓝色提示条：只有补录记录才有。 */
export function resolveBackfillNotice(item: Pick<DailyCloseListItem, "dataSource" | "detailLevel" | "businessDateInferred">): DailyCloseNotice | null {
  if (!isBackfilledRecord(item)) return null;
  const kind: DailyCloseNoticeKind = item.detailLevel === "CashOnly" ? "cashOnly" : item.detailLevel === "TraceOnly" ? "traceOnly" : "generic";
  return { kind, inferred: item.businessDateInferred };
}

// ───────────────────────── 明细 ─────────────────────────

const TENDER_ORDER: Record<string, number> = { Cash: 0, Card: 1, Voucher: 2 };

/** 支付方式按现金、刷卡、代金券排序，未知方式排最后；非 Full 记录为空数组，界面显示虚线占位。 */
export function orderTenders(tenders: readonly DailyCloseTender[]): DailyCloseTender[] {
  return [...tenders].sort((a, b) => (TENDER_ORDER[a.method] ?? 99) - (TENDER_ORDER[b.method] ?? 99));
}

/** 支付方式合计行：销售、退款、净额各自求和（按分）。没有笔数。 */
export function sumTenders(tenders: readonly DailyCloseTender[]) {
  const sum = (pick: (tender: DailyCloseTender) => number) => tenders.reduce((total, tender) => total + toCents(pick(tender)), 0) / 100;
  return { salesAmount: sum((t) => t.salesAmount), refundAmount: sum((t) => t.refundAmount), netAmount: sum((t) => t.netAmount) };
}

export interface CashCountGroups {
  notes: DailyCloseCashCount[];
  coins: DailyCloseCashCount[];
  /** 纸币小计：优先用接口给的 noteSubtotal，缺失时按明细求和。 */
  noteSubtotal: number;
  coinSubtotal: number;
}

/** 面额明细拆成纸币与硬币（按面额降序）；0 张的档位保留（界面灰显）。 */
export function splitCashCounts(counts: readonly DailyCloseCashCount[], noteSubtotal: number | null, coinSubtotal: number | null): CashCountGroups {
  const byDenomination = (a: DailyCloseCashCount, b: DailyCloseCashCount) => b.denominationCents - a.denominationCents;
  const notes = counts.filter((count) => count.kind === "Note").sort(byDenomination);
  const coins = counts.filter((count) => count.kind === "Coin").sort(byDenomination);
  const sumOf = (rows: readonly DailyCloseCashCount[]) => rows.reduce((total, row) => total + toCents(row.subtotalAmount), 0) / 100;
  return {
    notes,
    coins,
    noteSubtotal: noteSubtotal ?? sumOf(notes),
    coinSubtotal: coinSubtotal ?? sumOf(coins),
  };
}

/** 支付方式汇总有数据：Full 记录且有行。补录记录没有，要显示「暂无数据 · 待该终端补传」。 */
export function hasTenderData(detail: Pick<DailyCloseDetail, "detailLevel" | "tenders">): boolean {
  return detail.detailLevel === "Full" && detail.tenders.length > 0;
}

export function hasCashCountData(detail: Pick<DailyCloseDetail, "detailLevel" | "cashCounts">): boolean {
  return detail.detailLevel === "Full" && detail.cashCounts.length > 0;
}

/** 现金对账三格（实点 − 应有 = 差额）有数据：TraceOnly 没有金额，显示「无金额」说明。 */
export function hasCashReconciliation(item: Pick<DailyCloseListItem, "expectedCashAmount" | "countedCashAmount" | "cashDifference">): boolean {
  return item.expectedCashAmount !== null && item.countedCashAmount !== null && item.cashDifference !== null;
}

// ───────────────────────── 查看保存记录（员工操作日志）─────────────────────────

export type SaveLogSource = "legacy" | "pos";
export type SaveLogPreset = "today" | "yesterday" | "last7" | "last31";

export interface SaveLogLink {
  source: SaveLogSource;
  stores: string;
  device: string;
  preset: SaveLogPreset;
}

/**
 * 跳员工操作日志的预置参数。日志页只支持「今天 / 昨天 / 近 7 天 / 近 31 天」，
 * 取能盖住保存日的最小预设（保存日在门店时区）；超过 31 天的旧记录只能落在近 31 天，页面不会有对应日志。
 * 来源：补录记录来自旧收银日志优先老收银，其余优先新收银；没有对应来源的查看权限就换另一个，都没有返回 null（不显示入口）。
 */
export function resolveSaveLogLink(
  item: Pick<DailyCloseListItem, "dataSource" | "storeCode" | "deviceCode" | "savedAtUtc" | "storeTimeZoneId">,
  access: { canLegacy: boolean; canPos: boolean },
  today: string,
): SaveLogLink | null {
  const order: SaveLogSource[] = item.dataSource === "AuditBackfill" ? ["legacy", "pos"] : ["pos", "legacy"];
  const source = order.find((candidate) => (candidate === "legacy" ? access.canLegacy : access.canPos));
  if (!source) return null;
  const savedDate = toZonedMoment(item.savedAtUtc, item.storeTimeZoneId)?.date;
  const age = savedDate ? diffDays(today, savedDate) : null;
  const preset: SaveLogPreset = age === null || age <= 0 ? "today" : age === 1 ? "yesterday" : age <= 6 ? "last7" : "last31";
  return { source, stores: item.storeCode, device: item.deviceCode, preset };
}

// ───────────────────────── 错误 ─────────────────────────

export type DailyCloseErrorKind = "invalidQuery" | "forbidden" | "notFound" | "other";

/** 把接口错误归类，界面再映射成友好文案；其余错误走通用的本地化错误提示。 */
export function classifyDailyCloseError(error: unknown): DailyCloseErrorKind {
  const record = (error && typeof error === "object" ? error : {}) as {
    code?: unknown;
    response?: { status?: unknown; data?: unknown };
  };
  const status = typeof record.response?.status === "number" ? record.response.status : undefined;
  const data = record.response?.data && typeof record.response.data === "object" ? (record.response.data as Record<string, unknown>) : {};
  // Axios 的 error.code 通常是 ERR_BAD_REQUEST，业务码要先读响应体。
  const code = [data.errorCode, data.ErrorCode, data.code, data.Code, record.code].find((value) => typeof value === "string");
  if (status === 400 && code === "INVALID_QUERY") return "invalidQuery";
  if (status === 403) return "forbidden";
  if (status === 404) return "notFound";
  return "other";
}
