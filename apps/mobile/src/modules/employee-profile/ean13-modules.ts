// EAN13 编码表：左半区使用 L/G 两套，右半区使用 R；首位数字只决定左半区的 L/G 排列。
const L_CODES = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
const G_CODES = ["0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111"];
const R_CODES = ["1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100"];
const PARITY_PATTERNS = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

export const EAN13_MODULE_COUNT = 95;

export function isValidEan13(value: string) {
  if (!/^\d{13}$/.test(value)) return false;
  const sum = value
    .slice(0, 12)
    .split("")
    .reduce((total, char, index) => total + Number(char) * (index % 2 === 0 ? 1 : 3), 0);
  return (10 - (sum % 10)) % 10 === Number(value[12]);
}

/** 返回 95 位 "0/1" 模块串（1=黑条）；输入不是合法 EAN13 时返回 null，调用方应回退到二维码。 */
export function buildEan13Modules(value: string): string | null {
  const barcode = value.trim();
  if (!isValidEan13(barcode)) return null;
  const digits = barcode.split("").map(Number);
  const parity = PARITY_PATTERNS[digits[0]];
  const left = digits.slice(1, 7).map((digit, index) => (parity[index] === "L" ? L_CODES : G_CODES)[digit]).join("");
  const right = digits.slice(7).map((digit) => R_CODES[digit]).join("");
  return `101${left}01010${right}101`;
}

/** 把连续黑模块合并成 [起点, 宽度] 段，SVG 只需画少量矩形。 */
export function ean13BarRuns(modules: string): [number, number][] {
  const runs: [number, number][] = [];
  let start = -1;
  for (let index = 0; index <= modules.length; index += 1) {
    const black = modules[index] === "1";
    if (black && start < 0) start = index;
    if (!black && start >= 0) {
      runs.push([start, index - start]);
      start = -1;
    }
  }
  return runs;
}
