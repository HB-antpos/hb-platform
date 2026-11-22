/**
 * 纯数字条码的前导 0 变体（与后端 BarcodeVariants.GetLeadingZeroVariant 一致）：
 * 12 位补一个 0 → 13 位；13 位且以 0 开头则去掉它 → 12 位；其它长度或含非数字返回 null。
 *
 * iOS 相机（expo-camera）会把「以 0 开头的 EAN-13」去掉前导 0 变成 12 位，而库里可能按 13 位存放。
 * 调用方必须「精确匹配无结果才回退」：库里存在 12 位 X 与 13 位 0+X 分属不同商品的情况，
 * 不能把两种写法当作等价一起查。
 */
export function getLeadingZeroBarcodeVariant(code: string): string | null {
  if (!/^\d{12,13}$/.test(code)) return null;
  if (code.length === 12) return `0${code}`;
  return code.startsWith("0") ? code.slice(1) : null;
}

/** GS1 校验位：从最右一位起，奇数位 ×3、偶数位 ×1，求和后补到 10 的倍数。 */
function computeGs1CheckDigit(digits: string): number {
  let sum = 0;
  for (let i = 0; i < digits.length; i += 1) {
    const digit = digits.charCodeAt(digits.length - 1 - i) - 48;
    sum += i % 2 === 0 ? digit * 3 : digit;
  }
  return (10 - (sum % 10)) % 10;
}

/**
 * 箱码（GTIN-14 / ITF-14）换算回单件商品的 EAN-13（与后端 BarcodeVariants.GetCaseCodeItemBarcode 一致）：
 * 去掉首位包装层级指示符和末位校验位，按 EAN-13 规则补算校验位，例如 19333527854091 → 9333527854094。
 * 只认指示符 0–8 且 14 位校验位正确的码；不满足返回 null。
 */
export function getCaseCodeItemBarcode(code: string): string | null {
  if (!/^[0-8]\d{13}$/.test(code)) return null;
  if (computeGs1CheckDigit(code.slice(0, 13)) !== Number(code[13])) return null;
  const body = code.slice(1, 13);
  return `${body}${computeGs1CheckDigit(body)}`;
}

/**
 * 精确匹配无结果时依次尝试的条码写法（与后端 BarcodeVariants.GetLookupFallbacks 一致）：
 * 前导 0 变体 → 箱码换算出的单件条码 → 该单件条码的前导 0 变体。调用方逐个查、命中即停，只认条码来源。
 */
export function getLookupFallbackBarcodes(code: string): string[] {
  const fallbacks: string[] = [];
  const leadingZero = getLeadingZeroBarcodeVariant(code);
  if (leadingZero) fallbacks.push(leadingZero);
  const itemBarcode = getCaseCodeItemBarcode(code);
  if (itemBarcode) {
    fallbacks.push(itemBarcode);
    const itemLeadingZero = getLeadingZeroBarcodeVariant(itemBarcode);
    if (itemLeadingZero) fallbacks.push(itemLeadingZero);
  }
  return fallbacks;
}
