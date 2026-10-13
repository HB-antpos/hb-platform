// 人脸比对模块的日期纯函数。全部可注入 now，测试不依赖当前时间；到期日按手机本地时区的自然日计算（与 Web 一致）。
import { UNWELCOME_MAX_EXPIRY_MONTHS } from "./constants";

const YMD_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

function pad2(value: number): string {
  return String(value).padStart(2, "0");
}

export function formatYmd(year: number, month: number, day: number): string {
  return `${String(year).padStart(4, "0")}-${pad2(month)}-${pad2(day)}`;
}

/** 解析 yyyy-MM-dd；日期不存在（如 2026-02-30）或格式不对返回 null。 */
export function parseYmd(value: string | null | undefined): { year: number; month: number; day: number } | null {
  const match = YMD_PATTERN.exec((value ?? "").trim());
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const probe = new Date(Date.UTC(year, month - 1, day));
  if (probe.getUTCFullYear() !== year || probe.getUTCMonth() !== month - 1 || probe.getUTCDate() !== day) return null;
  return { year, month, day };
}

export function daysInMonth(year: number, month: number): number {
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

/** 手机本地时区的「今天」。 */
export function todayYmd(now: Date): string {
  return formatYmd(now.getFullYear(), now.getMonth() + 1, now.getDate());
}

export function addDaysYmd(value: string, delta: number): string {
  const parsed = parseYmd(value);
  if (!parsed) return value;
  const date = new Date(Date.UTC(parsed.year, parsed.month - 1, parsed.day + delta));
  return formatYmd(date.getUTCFullYear(), date.getUTCMonth() + 1, date.getUTCDate());
}

/** 加月份，月底取当月最后一天（1 月 31 日 + 1 月 = 2 月末），与 C# DateTime.AddMonths 一致。 */
export function addMonthsYmd(value: string, months: number): string {
  const parsed = parseYmd(value);
  if (!parsed) return value;
  const index = parsed.year * 12 + (parsed.month - 1) + months;
  const year = Math.floor(index / 12);
  const month = (index % 12 + 12) % 12 + 1;
  return formatYmd(year, month, Math.min(parsed.day, daysInMonth(year, month)));
}

/** 到期日可选的最早一天（明天）。 */
export function minExpiryDay(now: Date): string {
  return addDaysYmd(todayYmd(now), 1);
}

/** 到期日可选的最晚一天（今天起 12 个月）。 */
export function maxExpiryDay(now: Date): string {
  return addMonthsYmd(todayYmd(now), UNWELCOME_MAX_EXPIRY_MONTHS);
}

/** 服务端上限：现在（UTC）起 12 个月，月底取当月最后一天。 */
function addMonthsUtcMs(nowMs: number, months: number): number {
  const now = new Date(nowMs);
  const index = now.getUTCFullYear() * 12 + now.getUTCMonth() + months;
  const year = Math.floor(index / 12);
  const month = (index % 12 + 12) % 12;
  const day = Math.min(now.getUTCDate(), daysInMonth(year, month + 1));
  return Date.UTC(year, month, day, now.getUTCHours(), now.getUTCMinutes(), now.getUTCSeconds(), now.getUTCMilliseconds());
}

/**
 * 到期日 → 提交给服务端的 UTC 时刻：所选日期当天 0 点（手机本地时区）。
 * 服务端拒绝晚于「现在起 12 个月」的时刻；选最晚一天时，若夏令时切换让本地 0 点晚于该上限（只会差不到一小时），
 * 就收敛到上限前一分钟，避免用户明明选了界面允许的最晚日期却被服务端拒绝。
 */
export function expiryToUtcIso(expiresOn: string, now: Date): string {
  const parsed = parseYmd(expiresOn);
  if (!parsed) throw new Error("INVALID_EXPIRY_DATE");
  const localMidnight = new Date(parsed.year, parsed.month - 1, parsed.day, 0, 0, 0, 0).getTime();
  const serverCap = addMonthsUtcMs(now.getTime(), UNWELCOME_MAX_EXPIRY_MONTHS) - 60_000;
  return new Date(Math.min(localMidnight, serverCap)).toISOString();
}

/** UTC ISO → 手机本地时区的 yyyy-MM-dd HH:mm；无效返回 "-"。 */
export function formatLocalDateTime(utcIso: string | null | undefined): string {
  if (!utcIso) return "-";
  const date = new Date(utcIso);
  if (Number.isNaN(date.getTime())) return "-";
  return `${formatYmd(date.getFullYear(), date.getMonth() + 1, date.getDate())} ${pad2(date.getHours())}:${pad2(date.getMinutes())}`;
}

/** UTC ISO → 手机本地时区的 yyyy-MM-dd；无效返回 "-"。 */
export function formatLocalDate(utcIso: string | null | undefined): string {
  const text = formatLocalDateTime(utcIso);
  return text === "-" ? text : text.slice(0, 10);
}
