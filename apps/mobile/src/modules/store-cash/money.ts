// 现金模块的金额纯函数：一律以「分」为整数运算，避免 0.1 + 0.2 之类的浮点误差。
import { CASH_MAX_AMOUNT } from "./constants";

export type MoneyParseIssue = "empty" | "invalid" | "tooManyDecimals" | "negative" | "notPositive" | "tooLarge";

export type MoneyParseResult =
  | { ok: true; value: number; cents: number }
  | { ok: false; issue: MoneyParseIssue };

/** 澳元小数转分（四舍五入到整数分）。 */
export function toCents(value: number): number {
  return Math.round(value * 100);
}

export function fromCents(cents: number): number {
  return cents / 100;
}

/** 多个金额求和，按分累加后再还原，结果一定是两位小数内的精确值。 */
export function sumAmounts(values: readonly (number | null | undefined)[]): number {
  let cents = 0;
  for (const value of values) {
    if (typeof value === "number" && Number.isFinite(value)) cents += toCents(value);
  }
  return fromCents(cents);
}

/** 两个金额之差（a − b），同样走分。 */
export function diffAmounts(a: number, b: number): number {
  return fromCents(toCents(a) - toCents(b));
}

/** 键盘输入过滤：只留数字与一个小数点，小数最多两位，保持输入过程中不抖动。 */
export function sanitizeMoneyInput(text: string): string {
  const cleaned = text.replace(/[^\d.]/g, "");
  const dot = cleaned.indexOf(".");
  if (dot < 0) return cleaned;
  const integer = cleaned.slice(0, dot);
  const decimals = cleaned.slice(dot + 1).replace(/\./g, "").slice(0, 2);
  return `${integer}.${decimals}`;
}

/**
 * 解析用户输入的金额。
 * - 只接受 "12"、"12.5"、"12.50"、".5"、"12." 这类写法；千分位逗号、负号、科学计数法一律拒绝。
 * - 超过两位小数拒绝（不静默截断，避免记错账）。
 * - allowZero 控制 0 是否合法：存款/支出必须 > 0，期初/盘点允许 0。
 */
export function parseMoneyInput(
  text: string,
  options: { allowZero?: boolean; max?: number } = {},
): MoneyParseResult {
  const raw = text.trim();
  if (!raw) return { ok: false, issue: "empty" };
  if (raw.startsWith("-")) return { ok: false, issue: "negative" };
  if (!/^(\d+\.?\d*|\.\d+)$/.test(raw)) return { ok: false, issue: "invalid" };
  const [, decimals = ""] = raw.split(".");
  if (decimals.length > 2) return { ok: false, issue: "tooManyDecimals" };
  const value = Number(raw);
  if (!Number.isFinite(value)) return { ok: false, issue: "invalid" };
  const cents = toCents(value);
  if (cents === 0 && !options.allowZero) return { ok: false, issue: "notPositive" };
  if (cents > toCents(options.max ?? CASH_MAX_AMOUNT)) return { ok: false, issue: "tooLarge" };
  return { ok: true, value: fromCents(cents), cents };
}

function groupThousands(integerPart: string): string {
  return integerPart.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
}

/** "$1,234.50"；负数 "-$12.00"；null/undefined/NaN 返回 "--"（日结未接入时绝不能显示成 $0.00）。 */
export function formatAud(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return "--";
  const cents = toCents(value);
  const abs = Math.abs(cents);
  const integer = Math.floor(abs / 100);
  const fraction = String(abs % 100).padStart(2, "0");
  return `${cents < 0 ? "-" : ""}$${groupThousands(String(integer))}.${fraction}`;
}

/** 带正负号的差异展示："+$5.00" / "-$5.00" / "$0.00"。 */
export function formatSignedAud(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return "--";
  const cents = toCents(value);
  if (cents === 0) return formatAud(0);
  return cents > 0 ? `+${formatAud(value)}` : formatAud(value);
}

/** 把金额转成输入框初始文本（建议存款额等预填），整数不带小数。 */
export function amountToInputText(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return "";
  const cents = toCents(value);
  if (cents % 100 === 0) return String(cents / 100);
  return (cents / 100).toFixed(2);
}
