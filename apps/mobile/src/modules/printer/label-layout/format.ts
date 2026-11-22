/**
 * 标签文字格式化：逐条对齐 Android HbPrinterModule.kt 的同名函数（现有纸面以安卓为准）。
 */

/** Kotlin ReadableMap.getNullableString：null / 非字符串按空串，并去掉首尾空白。 */
export function payloadString(value: unknown): string {
  if (typeof value === "string") return value.trim();
  if (value == null) return "";
  return String(value).trim();
}

/** Kotlin ReadableMap.getNullableDouble：只认有限数字，其余视为缺失。 */
export function payloadNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/** Kotlin cpclText：换行并成空格，去掉首尾空白。 */
export function cpclText(value: string) {
  return value.replace(/[\r\n]+/g, " ").trim();
}

/** Kotlin String.ifBlank { " " }：全空白时用一个空格占位（位图至少有宽度）。 */
export function ifBlankSpace(value: string) {
  return value.trim() === "" ? " " : value;
}

/** Kotlin Double.roundToInt：四舍五入（.5 向正无穷）。 */
export function roundToInt(value: number) {
  return Math.round(value);
}

/** kotlin.math.round：四舍六入五成双（银行家舍入）。 */
export function roundHalfEven(value: number) {
  const floor = Math.floor(value);
  const diff = value - floor;
  if (diff > 0.5) return floor + 1;
  if (diff < 0.5) return floor;
  return floor % 2 === 0 ? floor : floor + 1;
}

/** Kotlin formatPriceParts：先按分取整，整数部分与两位小数分开。 */
export function formatPriceParts(value: number | null) {
  const cents = roundToInt((value ?? 0) * 100);
  const integer = Math.trunc(cents / 100);
  const decimal = String(Math.abs(cents % 100)).padStart(2, "0");
  return { integer: String(integer), decimal };
}

/** Kotlin formatMoney（%.2f，Locale.US）。 */
export function formatMoney(value: number) {
  return value.toFixed(2);
}

export function formatOptionalMoney(value: number | null) {
  return value == null ? "--" : formatMoney(value);
}

export function formatOptionalQuantity(value: number | null) {
  if (value == null) return "--";
  const rounded = roundToInt(value);
  return Math.abs(value - rounded) < 0.01 ? String(rounded) : value.toFixed(2);
}

/** Kotlin todayString：本机时区 yyyy/MM/dd。 */
export function todayString(now: Date) {
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, "0");
  const day = String(now.getDate()).padStart(2, "0");
  return `${year}/${month}/${day}`;
}

/** Kotlin formatSupplierAbbreviation：单词取前三个字母大写；多词取前四个词首字母用点连接。 */
export function formatSupplierAbbreviation(value: string) {
  const words = value
    .toLocaleLowerCase("en-US")
    .split(/\s+/)
    .filter(Boolean)
    .map((word) => word.charAt(0).toLocaleUpperCase("en-US") + word.slice(1));
  if (words.length === 0) return "";
  if (words.length === 1) return words[0].slice(0, 3).toLocaleUpperCase("en-US");
  return words
    .slice(0, 4)
    .map((word) => word.charAt(0).toLocaleUpperCase("en-US"))
    .join(".");
}

/** Kotlin isValidEan13：13 位数字且校验位正确。 */
export function isValidEan13(barcode: string) {
  if (!/^\d{13}$/.test(barcode)) return false;
  const sum = barcode
    .slice(0, 12)
    .split("")
    .reduce((total, char, index) => total + Number(char) * (index % 2 === 0 ? 1 : 3), 0);
  return (10 - (sum % 10)) % 10 === Number(barcode[12]);
}
