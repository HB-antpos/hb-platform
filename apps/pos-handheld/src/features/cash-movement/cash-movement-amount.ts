/**
 * 小键盘金额输入的纯逻辑：全程用字符串与整数分，不经过浮点数。
 * 规则：最多 2 位小数；整数与小数部分合计最多 9 位数字（100 万元上限正好是 9 位：1000000.00）；
 * 前导 0 归一（"05" → "5"）；超出限制的按键直接忽略，不静默截断已输入的内容。
 */
export const CASH_MOVEMENT_AMOUNT_MAX_DIGITS = 9;
export const CASH_MOVEMENT_AMOUNT_MAX_DECIMALS = 2;

/** 快捷金额（分）：20 / 50 / 100 / 200 / 500。 */
export const CASH_MOVEMENT_QUICK_AMOUNTS_CENTS: readonly number[] = Object.freeze([
  2_000, 5_000, 10_000, 20_000, 50_000,
]);

export type CashMovementKeypadKey =
  | "0"
  | "1"
  | "2"
  | "3"
  | "4"
  | "5"
  | "6"
  | "7"
  | "8"
  | "9"
  | "00"
  | "."
  | "backspace"
  | "clear";

export function applyAmountKey(
  current: string,
  key: CashMovementKeypadKey,
): string {
  if (key === "clear") return "";
  if (key === "backspace") return current.slice(0, -1);
  if (key === ".") {
    if (current.includes(".")) return current;
    return current === "" ? "0." : `${current}.`;
  }
  // 数字与 "00"：先拼接，再校验位数与小数位。
  let next = `${current}${key}`;
  // 前导 0 归一：只保留整数部分的一个 0（"0" + "5" → "5"，"0." 不受影响）。
  next = next.replace(/^0+(?=\d)/u, "");
  if (next === "00") next = "0";
  const [integerPart = "", decimalPart] = next.split(".");
  if (decimalPart !== undefined && decimalPart.length > CASH_MOVEMENT_AMOUNT_MAX_DECIMALS) {
    return current;
  }
  const digitCount = integerPart.length + (decimalPart?.length ?? 0);
  if (digitCount > CASH_MOVEMENT_AMOUNT_MAX_DIGITS) return current;
  return next;
}

/** 字符串金额 → 整数分；空串、只有 "0" / "." 或无法解析一律返回 0（由校验挡成 AMOUNT_INVALID）。 */
export function amountTextToCents(text: string): number {
  if (!/^\d*\.?\d*$/u.test(text) || text === "" || text === ".") return 0;
  const [integerPart = "", decimalPart = ""] = text.split(".");
  const padded = decimalPart.padEnd(CASH_MOVEMENT_AMOUNT_MAX_DECIMALS, "0");
  const cents = Number(`${integerPart === "" ? "0" : integerPart}${padded}`);
  return Number.isSafeInteger(cents) ? cents : 0;
}

/** 整数分 → 输入框文本（快捷金额用）："2000" → "20"，"2050" → "20.5"。 */
export function centsToAmountText(cents: number): string {
  const whole = Math.floor(cents / 100);
  const fraction = cents % 100;
  if (fraction === 0) return String(whole);
  return `${whole}.${String(fraction).padStart(2, "0").replace(/0$/u, "")}`;
}

export function formatCents(cents: number): string {
  const sign = cents < 0 ? "-" : "";
  const absolute = Math.abs(cents);
  return `${sign}$${Math.floor(absolute / 100)}.${String(absolute % 100).padStart(2, "0")}`;
}
