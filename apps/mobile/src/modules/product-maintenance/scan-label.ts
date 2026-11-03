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
    // 套装标签只用套装价，不依赖主档门店价：主档在本店没有价格行（快路径不补建）时也要能打套装码。
    // 多码价跟随主档、主条码打主档价，二者仍要求同店门店价行存在。
    (target.kind !== "clearance" && target.kind !== "set" &&
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

/**
 * 自动打印时，普通标签打完后是否还要补打折扣标签：折扣率为大于 0 的有限数才补打。
 * 口径与手动「折扣标签」按钮一致（storePrice.discountRate > 0），null / undefined / 0 / 负数 / NaN 都不补打。
 */
export function shouldAutoPrintDiscountLabel(discountRate: number | null | undefined): boolean {
  return typeof discountRate === "number" && Number.isFinite(discountRate) && discountRate > 0;
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
 * - 套装子项（类型 1）：套装条码 + 套装价（详情里已是门店套装价优先）+ 主档门店折扣
 *   （套装行没有自己的折扣字段；2026-11-03 起套装子码与多码同口径，可带折扣）；
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
      discountRate: detail.storePrice?.discountRate ?? 0,
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

/**
 * 本次扫到的码（会话内「扫码焦点」）：页面上的焦点码条、价格页签的打印入口都以它为准，
 * 这样扫到套装 / 多码子码后，哪怕自动打印没打成，店员手按「普通」打出来的也是这个码而不是主档。
 */
export interface ScannedCodeFocus {
  kind: "set" | "multi" | "product" | "clearance";
  codeId: string | null;
  barcode: string;
  /** 解析时的价格快照。套装用它兜底；多码 / 主档打印时一律按当前详情重新取价（见 resolveFocusLabelTarget）。 */
  retailPrice: number | null;
  discountRate: number;
  /** server：服务端核对过的打印目标；local：按已加载码表 / 主档字段本地匹配。 */
  source: "server" | "local";
}

/** 套装 / 多码子码焦点：只有这两类会改变价格页签打印入口的目标。 */
export type ScannedSubCodeFocus = ScannedCodeFocus & { kind: "set" | "multi" };

export function isSubCodeFocus(focus: ScannedCodeFocus | null | undefined): focus is ScannedSubCodeFocus {
  return focus?.kind === "set" || focus?.kind === "multi";
}

/**
 * 解析扫码焦点：优先服务端核对过的 printTarget，其次本地码表精确匹配；都没命中返回 null
 * （码表分页未加载到该码时，由调用方在码表加载后再试一次）。手动查询的关键字恰好等于某个码时同样成立；
 * 商品名之类的模糊关键字不会精确命中，天然不设焦点。
 */
export function resolveScannedCodeFocus(
  detail: ProductDetail,
  scannedCode: string,
  printTarget?: ScanLabelPrintTarget | null,
): ScannedCodeFocus | null {
  if (printTarget) {
    return {
      kind: printTarget.kind,
      codeId: printTarget.codeId,
      barcode: printTarget.barcode,
      retailPrice: printTarget.retailPrice,
      // 服务端套装目标不带折扣（套装行没有折扣字段）：套装子码的折扣跟主档门店价；
      // 清货标签没有折扣；多码 / 主档服务端给 null 时按 0。
      discountRate:
        printTarget.kind === "clearance"
          ? 0
          : printTarget.kind === "set"
            ? detail.storePrice?.discountRate ?? 0
            : printTarget.discountRate ?? 0,
      source: "server",
    };
  }

  const resolved = resolveScannedCodeLabel(detail, scannedCode);
  if (!resolved) return null;
  if (resolved.kind === "set" || resolved.kind === "multi") {
    return { ...resolved, source: "local" };
  }
  if (resolved.kind === "clearance") {
    return {
      kind: "clearance",
      codeId: detail.clearancePrice?.uuid ?? null,
      barcode: detail.clearancePrice?.clearanceBarcode?.trim() || scannedCode.trim(),
      retailPrice: detail.clearancePrice?.clearancePrice ?? null,
      discountRate: 0,
      source: "local",
    };
  }
  return {
    kind: "product",
    codeId: detail.storePrice?.uuid ?? null,
    barcode: detail.barcode?.trim() || scannedCode.trim(),
    retailPrice: detail.storePrice?.retailPrice ?? null,
    discountRate: detail.storePrice?.discountRate ?? 0,
    source: "local",
  };
}

/**
 * 焦点码对应的子码打印目标，价格按「当前」详情取，而不是解析时的快照：
 * - 套装：优先码表里同一行（同 setCodeId 或同条码）的套装价，码表没加载到该行时才用快照；折扣跟主档门店价；
 * - 多码：主档门店价 + 主档折扣（与 resolveMultiCodeLabelPrice 同口径），调价保存后自动跟新价。
 * 主条码 / 清货码不是子码标签，返回 null，调用方走原有主档 / 清货打印。
 */
export function resolveFocusLabelTarget(
  detail: Pick<ProductDetail, "storePrice" | "setCodes">,
  focus: ScannedCodeFocus,
): CodeLabelTarget | null {
  if (focus.kind === "set") {
    const code = normalizeCode(focus.barcode);
    const row = detail.setCodes.find(
      (item) => item.setCodeId === focus.codeId || normalizeCode(item.setBarcode) === code,
    );
    return {
      kind: "set",
      codeId: row?.setCodeId ?? focus.codeId ?? "",
      barcode: row?.setBarcode?.trim() || focus.barcode,
      retailPrice: row?.setRetailPrice ?? focus.retailPrice,
      discountRate: detail.storePrice?.discountRate ?? 0,
    };
  }
  if (focus.kind === "multi") {
    return {
      kind: "multi",
      codeId: focus.codeId ?? "",
      barcode: focus.barcode,
      ...resolveMultiCodeLabelPrice(detail),
    };
  }
  return null;
}

/** 服务端查询按不区分大小写的排序规则匹配，本地核对同样忽略大小写，避免货号大小写不同就拒绝打印。 */
function normalizeCode(value?: string | null): string {
  return value?.trim().toUpperCase() ?? "";
}
