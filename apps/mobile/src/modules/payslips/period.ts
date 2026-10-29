// 工资单列表的日期口径：按「支付日期」筛选，预设为最近两周 / 近 3 个月（90 天，与后端默认一致）/ 本财年。
// 澳洲财年 7/1 至次年 6/30。全部用 yyyy-MM-dd 字符串 + UTC 日历运算，不受设备时区与夏令时影响。

export type PayslipPreset = "twoWeeks" | "threeMonths" | "fiscalYear" | "custom";

export interface PayslipDateRange {
  from: string;
  to: string;
}

/** 默认预设：近 3 个月（最近 90 天），与 Web 工资单页默认窗口一致。 */
export const DEFAULT_PAYSLIP_PRESET: PayslipPreset = "threeMonths";
export const PAYSLIP_PRESETS: readonly PayslipPreset[] = ["twoWeeks", "threeMonths", "fiscalYear", "custom"];

const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

function pad2(value: number) {
  return String(value).padStart(2, "0");
}

function parseUtc(iso: string): Date | null {
  const match = iso.match(DATE_PATTERN);
  if (!match) return null;
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
  // 拒绝 2026-02-31 这类被 Date 自动进位的日期。
  return date.toISOString().slice(0, 10) === iso ? date : null;
}

export function isValidIsoDate(value: unknown): value is string {
  return typeof value === "string" && parseUtc(value) !== null;
}

/** 设备本地「今天」的 yyyy-MM-dd。 */
export function localTodayIso(now: Date = new Date()): string {
  return `${now.getFullYear()}-${pad2(now.getMonth() + 1)}-${pad2(now.getDate())}`;
}

export function addDays(iso: string, days: number): string {
  const date = parseUtc(iso);
  if (!date) return iso;
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

/** 今天所在澳洲财年的第一天（7 月 1 日）。 */
export function fiscalYearStart(today: string): string {
  const date = parseUtc(today);
  if (!date) return today;
  const year = date.getUTCMonth() + 1 >= 7 ? date.getUTCFullYear() : date.getUTCFullYear() - 1;
  return `${year}-07-01`;
}

/** 校验自定义区间：两端都要是合法日期；起止颠倒时自动对调；不合法返回 null。 */
export function normalizeCustomRange(from?: string | null, to?: string | null): PayslipDateRange | null {
  if (!isValidIsoDate(from) || !isValidIsoDate(to)) return null;
  return from <= to ? { from, to } : { from: to, to: from };
}

/** 预设 → 具体起止日（含首尾）。custom 取传入的自定义区间，缺失时退回默认预设。 */
export function resolvePreset(preset: PayslipPreset, today: string, custom?: PayslipDateRange | null): PayslipDateRange {
  switch (preset) {
    case "twoWeeks":
      return { from: addDays(today, -13), to: today };
    case "fiscalYear":
      return { from: fiscalYearStart(today), to: today };
    case "custom":
      if (custom) return custom;
      return resolvePreset(DEFAULT_PAYSLIP_PRESET, today);
    case "threeMonths":
    default:
      return { from: addDays(today, -89), to: today };
  }
}
