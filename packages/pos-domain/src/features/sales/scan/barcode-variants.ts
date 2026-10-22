/**
 * 纯数字条码的前导 0 变体（与后端 BarcodeVariants.GetLeadingZeroVariant、
 * WPF BarcodeVariants.GetLeadingZeroVariant、移动端 barcode-variants.ts 一致）：
 * 12 位补一个 0 → 13 位；13 位且以 0 开头则去掉它 → 12 位；其它长度或含非数字返回 null。
 *
 * 扫码枪 / 相机常把「以 0 开头的 EAN-13」当 UPC-A 去掉前导 0，而库里可能按 13 位存放（或反之）。
 * 调用方必须「精确匹配无结果才回退」：生产库里存在 12 位 X 与 13 位 0+X 分属不同商品的情况，
 * 不能把两种写法当作等价一起查。
 */
export function getLeadingZeroBarcodeVariant(code: string): string | null {
  const trimmed = code.trim();
  if (!/^\d{12,13}$/.test(trimmed)) return null;
  if (trimmed.length === 12) return `0${trimmed}`;
  return trimmed.startsWith("0") ? trimmed.slice(1) : null;
}
