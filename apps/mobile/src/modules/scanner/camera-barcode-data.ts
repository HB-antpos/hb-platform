// 相机扫码结果还原：条码上印的是以 0 开头的 13 位（UPC-A 即「0 + 12 位」），相机层会丢掉这个 0：
// - iOS：UPC-A 被识别成 EAN-13，expo-camera 原生层再去掉首位 0，返回 12 位、类型 ean13；
// - Android：ML Kit 把「0 开头的 EAN-13」直接按 UPC-A 返回，12 位、类型 upc_a。
// 无论哪种，12 位纯数字都是同一条 13 位 GTIN 去掉了前导 0，这里在源头补回，
// 让输入框、查询、缓存看到的都是条码上印的完整 13 位。
// 补 0 后若库里按 12 位存放，由各查询处「精确无结果再回退」的前导 0 变体兜底（见 barcode-variants）。
// 其他类型（code128 / qr / upc_e 等）或长度不符一律原样返回，不碰其他条码。
const GTIN_STRIPPED_TYPES = new Set(["ean13", "upc_a"]);

export function restoreCameraBarcodeData(type: string | undefined, data: string): string {
  if (type && GTIN_STRIPPED_TYPES.has(type) && /^\d{12}$/.test(data)) {
    return `0${data}`;
  }
  return data;
}
