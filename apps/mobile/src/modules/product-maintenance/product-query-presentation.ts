import type { ProductDetail } from "./types";

/** 毛利变化方向：相对修改前的基线。null 表示无可比数据或没有变化。 */
export type MarginTrend = "up" | "down" | null;

/**
 * 毛利率（百分比数值），规则与页面 calcGpPercent 一致：售价须大于 0、进价须非负。
 * 返回四舍五入到整数的百分比，保证与界面显示的「35%」口径一致，避免显示相同却判定为变化。
 */
export function calcGrossMarginPercent(
  sellPrice?: number | null,
  purchasePrice?: number | null,
): number | null {
  if (sellPrice == null || !Number.isFinite(sellPrice) || sellPrice <= 0) return null;
  if (purchasePrice == null || !Number.isFinite(purchasePrice) || purchasePrice < 0) return null;
  const gp = ((sellPrice - purchasePrice) / sellPrice) * 100;
  return Number.isFinite(gp) ? Math.round(gp) : null;
}

export function getMarginTrend(current: number | null, baseline: number | null): MarginTrend {
  if (current == null || baseline == null || current === baseline) return null;
  return current > baseline ? "up" : "down";
}

/** 毛利升高用成功绿，降低用危险红；无变化沿用默认强调色。 */
export function getMarginTrendColor(trend: MarginTrend, fallback: string) {
  if (trend === "up") return "#067647";
  if (trend === "down") return "#B42318";
  return fallback;
}

export function getMarginTrendArrow(trend: MarginTrend) {
  return trend === "up" ? "↑" : trend === "down" ? "↓" : "";
}

/**
 * 字段显示「原 X」的判定：当前显示值与修改前显示值不同才返回原值。
 * 原值为空时用 "--" 占位，便于用户看出是从空值改过来的。
 */
export function resolveOriginalDisplay(current: string, original: string | null | undefined): string | null {
  if (original === undefined || original === null) return null;
  if (current === original) return null;
  return original || "--";
}

/** 套装价相当于多少件单品价：保留 1 位小数；主零售价为空或 0 时不给参考。 */
export function getSetUnitEquivalent(
  setRetailPrice: number | null | undefined,
  unitRetailPrice: number | null | undefined,
): number | null {
  if (unitRetailPrice == null || !Number.isFinite(unitRetailPrice) || unitRetailPrice <= 0) return null;
  if (setRetailPrice == null || !Number.isFinite(setRetailPrice) || setRetailPrice <= 0) return null;
  return Math.round((setRetailPrice / unitRetailPrice) * 10) / 10;
}

/** 条码区默认折叠：只有有条码时才提供「显示条码」切换，展开时才渲染条码图。 */
export function resolveBarcodeDisclosure(barcode: string | null | undefined, expanded: boolean) {
  const value = barcode?.trim() ?? "";
  return {
    value,
    canToggle: value.length > 0,
    showImage: value.length > 0 && expanded,
  };
}

/**
 * 编码页签展示哪一类列表。条件与原页面内联判断保持一致：
 * 套装 = 类型 1，或非多码类型下只有套装码；多码 = 类型 2 或存在多码。
 */
export function resolveCodeSections(
  detail: Pick<ProductDetail, "productType" | "setCodeCount" | "multiCodeCount"> | null | undefined,
) {
  if (!detail) return { hasCodeSection: false, showSet: false, showMulti: false, count: 0 };
  const hasCodeSection =
    detail.productType === 1 ||
    detail.productType === 2 ||
    detail.setCodeCount > 0 ||
    detail.multiCodeCount > 0;
  const showSet =
    detail.productType === 1 ||
    (detail.productType !== 2 && detail.setCodeCount > 0 && detail.multiCodeCount === 0);
  const showMulti = detail.productType === 2 || detail.multiCodeCount > 0;
  const count = (showSet ? detail.setCodeCount : 0) + (showMulti ? detail.multiCodeCount : 0);
  return { hasCodeSection, showSet, showMulti, count };
}

/**
 * 编码行相对基线（修改前详情）的差异。当前编码都是单行即时保存，
 * 正常流程下差异为空；保留比对是为了让行高亮与页签橙点和基线口径一致。
 */
export function getDirtyCodeIds(
  current: Pick<ProductDetail, "setCodes" | "multiCodes"> | null | undefined,
  initial: Pick<ProductDetail, "setCodes" | "multiCodes"> | null | undefined,
): Set<string> {
  const dirty = new Set<string>();
  if (!current || !initial) return dirty;
  const initialSets = new Map(initial.setCodes.map((item) => [item.setCodeId, item]));
  for (const item of current.setCodes) {
    const base = initialSets.get(item.setCodeId);
    if (base && (base.setBarcode !== item.setBarcode || base.setRetailPrice !== item.setRetailPrice)) {
      dirty.add(item.setCodeId);
    }
  }
  const multiId = (item: { setCodeId: string; uuid: string }) => item.setCodeId || item.uuid;
  const initialMultis = new Map(initial.multiCodes.map((item) => [multiId(item), item]));
  for (const item of current.multiCodes) {
    const base = initialMultis.get(multiId(item));
    if (base && (base.barcode !== item.barcode || base.retailPrice !== item.retailPrice)) {
      dirty.add(multiId(item));
    }
  }
  return dirty;
}

/** 新增编码面板的提交前校验：条码必填；套装零售价须为正数，多码不收零售价（跟随主条码）。 */
export function isCodeAddDraftValid(codeType: "set" | "multi", barcode: string, retailPrice: string) {
  if (!barcode.trim()) return false;
  if (codeType === "multi") return true;
  const trimmed = retailPrice.trim();
  if (!trimmed) return false;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) && parsed > 0;
}

export function parseOptionalPrice(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}
