import type {
  ProductDetail,
  ScanLabelPrintTarget,
  ScanLabelResult,
} from "./types";

/** 快速打印只能使用本次在线扫码的唯一商品及同门店目标，不能猜测套码或沿用旧价格。 */
export function getVerifiedScanPrintTarget(
  result: ScanLabelResult,
  keyword: string,
  storeCode: string,
): ScanLabelPrintTarget | null {
  const candidate = result.candidates.length === 1 ? result.candidates[0] : null;
  const detail = result.detail;
  const target = result.printTarget;
  if (
    !candidate || !detail || !target ||
    candidate.productCode !== detail.productCode ||
    target.productCode !== detail.productCode ||
    target.storeCode !== storeCode ||
    (target.kind !== "clearance" &&
      (detail.storePrice?.storeCode !== storeCode || !detail.storePrice?.uuid.trim())) ||
    target.retailPrice == null ||
    !Number.isFinite(target.retailPrice) ||
    target.retailPrice <= 0 ||
    (target.discountRate != null &&
      (!Number.isFinite(target.discountRate) || target.discountRate < 0 || target.discountRate > 1))
  ) {
    return null;
  }

  const scannedCode = keyword.trim();
  const matchSource = candidate.matchSource;
  if (candidate.matchValue?.trim() !== scannedCode) return null;
  if (target.kind === "product") {
    return (matchSource === "ProductBarcode" || matchSource === "ItemNumber") &&
      [detail.barcode, detail.itemNumber, detail.productCode, detail.storePrice?.storeProductCode]
        .some((code) => code?.trim() === target.barcode)
      ? target : null;
  }
  if (target.barcode !== scannedCode || !target.codeId?.trim()) return null;
  if (target.kind === "set" && matchSource === "SetBarcode") return target;
  if (target.kind === "multi" && matchSource === "SetBarcode") return target;
  if (
    target.kind === "clearance" &&
    matchSource === "ClearanceBarcode" &&
    detail.clearancePrice?.storeCode === storeCode &&
    detail.clearancePrice.clearanceBarcode?.trim() === scannedCode &&
    detail.clearancePrice.clearancePrice === target.retailPrice
  ) return target;
  return null;
}

/** 套装 / 多码子项标签：条码、价格、折扣都取子项口径，不能落回主档商品。 */
export interface CodeLabelTarget {
  kind: "set" | "multi";
  codeId: string;
  barcode: string;
  /** null 表示该子项没有可打印的价格，调用方必须拒绝打印，不能让打印层回退到主档价。 */
  retailPrice: number | null;
  /** 显式数值（含 0）：打印层对 null/undefined 会回退到主档折扣。 */
  discountRate: number;
}

export type ScannedCodeLabel = CodeLabelTarget | { kind: "product" } | { kind: "clearance" };

/**
 * 多码（商品类型 2）与主条码是同一商品：标签价格与折扣一律取主档门店价，
 * 不取多码行自身的 retailPrice / discountRate（门店多码价可能未与主档同步）。
 */
export function resolveMultiCodeLabelPrice(
  detail: Pick<ProductDetail, "storePrice">,
): { retailPrice: number | null; discountRate: number } {
  return {
    retailPrice: detail.storePrice?.retailPrice ?? null,
    discountRate: detail.storePrice?.discountRate ?? 0,
  };
}

/**
 * 按本次扫到的码决定打哪种标签（无服务器 printTarget 时使用：离线、普通查询、自动定价确认后）。
 * - 套装子项（类型 1）：套装条码 + 套装价（详情里已是门店套装价优先），按业务规则不印折扣；
 * - 多码子项（类型 2）：多码条码 + 主档门店价 + 主档折扣；
 * - 主条码 / 货号 / 商品编码 / 店内码：主档标签；
 * - 都没命中（码表分页未加载到该码）返回 null，调用方不能回退打印主档条码和价格。
 */
export function resolveScannedCodeLabel(
  detail: ProductDetail,
  scannedCode: string,
): ScannedCodeLabel | null {
  const code = normalizeCode(scannedCode);
  if (!code) return null;

  const set = detail.setCodes.find((item) => normalizeCode(item.setBarcode) === code);
  if (set) {
    return {
      kind: "set",
      codeId: set.setCodeId,
      barcode: set.setBarcode!.trim(),
      retailPrice: set.setRetailPrice ?? null,
      discountRate: 0,
    };
  }

  const multi = detail.multiCodes.find((item) => normalizeCode(item.barcode) === code);
  if (multi) {
    return {
      kind: "multi",
      // 历史多码可能没有 setCodeId，与页面 getMultiCodeItemId 一致回落到 uuid。
      codeId: multi.setCodeId || multi.uuid,
      barcode: multi.barcode!.trim(),
      ...resolveMultiCodeLabelPrice(detail),
    };
  }

  if (normalizeCode(detail.clearancePrice?.clearanceBarcode) === code) {
    return { kind: "clearance" };
  }

  const productCodes = [
    detail.barcode,
    detail.itemNumber,
    detail.productCode,
    detail.storePrice?.storeProductCode,
  ];
  if (productCodes.some((value) => normalizeCode(value) === code)) {
    return { kind: "product" };
  }
  return null;
}

/** 服务端查询按不区分大小写的排序规则匹配，本地核对同样忽略大小写，避免货号大小写不同就拒绝打印。 */
function normalizeCode(value?: string | null): string {
  return value?.trim().toUpperCase() ?? "";
}
