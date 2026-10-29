// 工资单页的展示格式化：金额带 $、负数 -$138.00、千分位；日期按纸面 d/m/yyyy（不补零）。
// 纯函数、不依赖 Intl（Hermes 的 Intl 能力因设备而异），也不依赖当前语言。

const EM_DASH = "—";

/** 给整数部分加千分位逗号。 */
export function groupThousands(integerPart: string): string {
  return integerPart.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
}

function toNumber(value: unknown): number | null {
  if (typeof value === "number") return Number.isFinite(value) ? value : null;
  if (typeof value === "string" && value.trim() !== "") {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

/** 固定小数位的数字字符串（带千分位，负号放最前；四舍五入后为 0 不显示负号）。 */
function formatFixed(value: number, digits: number): string {
  const fixed = Math.abs(value).toFixed(digits);
  const [integerPart, fraction] = fixed.split(".");
  const isZero = Number(fixed) === 0;
  const sign = value < 0 && !isZero ? "-" : "";
  return `${sign}${groupThousands(integerPart)}${fraction ? `.${fraction}` : ""}`;
}

/** 金额：$1,234.50；负数 -$138.00；空值显示「—」。 */
export function formatMoney(value: number | null | undefined, empty: string = EM_DASH): string {
  const number = toNumber(value);
  if (number === null) return empty;
  const text = formatFixed(number, 2);
  return text.startsWith("-") ? `-$${text.slice(1)}` : `$${text}`;
}

/** 小时数：两位小数 + " h"，如 30.00 h。 */
export function formatHours(value: number | null | undefined, empty: string = EM_DASH): string {
  const number = toNumber(value);
  return number === null ? empty : `${formatFixed(number, 2)} h`;
}

/** 明细里的费率（MYOB Calc. Rate）：至少两位小数，有更多有效小数时保留到四位，如 $24.00 / $24.5438。 */
export function formatRate(value: number | null | undefined, empty: string = EM_DASH): string {
  const number = toNumber(value);
  if (number === null) return empty;
  // 先按四位取小数，去掉尾部多余的 0，但至少留两位。
  const fraction = Math.abs(number).toFixed(4).split(".")[1];
  const digits = Math.max(2, fraction.replace(/0+$/, "").length);
  const text = formatFixed(number, digits);
  return text.startsWith("-") ? `-$${text.slice(1)}` : `$${text}`;
}

/** 头部的 Hourly Rate：固定四位小数（与 MYOB 纸面一致），如 $24.0000。 */
export function formatHourlyRate(value: number | null | undefined, empty: string = EM_DASH): string {
  const number = toNumber(value);
  if (number === null) return empty;
  const text = formatFixed(number, 4);
  return text.startsWith("-") ? `-$${text.slice(1)}` : `$${text}`;
}

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})/;

/** 取日期字符串的 yyyy-MM-dd 部分（兼容带时间的 ISO 串）；不是日期返回空串。 */
export function toDateOnly(value: unknown): string {
  if (typeof value !== "string") return "";
  const match = value.trim().match(ISO_DATE);
  return match ? `${match[1]}-${match[2]}-${match[3]}` : "";
}

/** 纸面日期 d/m/yyyy（不补零），如 29/10/2026；无法解析时原样返回（或空）。 */
export function formatDate(value: string | null | undefined): string {
  const dateOnly = toDateOnly(value);
  if (!dateOnly) return typeof value === "string" ? value : "";
  const [year, month, day] = dateOnly.split("-");
  return `${Number(day)}/${Number(month)}/${year}`;
}

/** 列表里的短日期 d/m，如 19/10。 */
export function formatShortDate(value: string | null | undefined): string {
  const dateOnly = toDateOnly(value);
  if (!dateOnly) return typeof value === "string" ? value : "";
  const [, month, day] = dateOnly.split("-");
  return `${Number(day)}/${Number(month)}`;
}

/** 工资周期（列表卡片用）：19/10 — 25/10；缺结束日只显示开始日。 */
export function formatPeriodShort(start: string | null | undefined, end: string | null | undefined): string {
  const from = formatShortDate(start);
  const to = formatShortDate(end);
  if (from && to) return `${from} ${EM_DASH} ${to}`;
  return from || to;
}

/** 工资周期（带年，弹层副标题用）：19/10/2026 — 25/10/2026。 */
export function formatPeriodFull(start: string | null | undefined, end: string | null | undefined): string {
  const from = formatDate(start);
  const to = formatDate(end);
  if (from && to) return `${from} ${EM_DASH} ${to}`;
  return from || to;
}

/** ABN 按 2-3-3-3 分组；已经带空格或位数不是 11 位时原样返回。 */
export function formatAbn(value: string | null | undefined): string {
  const text = (value ?? "").trim();
  if (!/^\d{11}$/.test(text)) return text;
  return `${text.slice(0, 2)} ${text.slice(2, 5)} ${text.slice(5, 8)} ${text.slice(8)}`;
}
