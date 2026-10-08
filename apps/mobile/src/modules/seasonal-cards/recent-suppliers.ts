/**
 * 「最近使用」供应商的纯逻辑（按分店记录，最多 3 个，最近的排最前）。
 * 读写本地存储放在 recent-suppliers-storage.ts，避免测试加载原生模块。
 */
import type { SeasonalCardSupplierOption } from "@/modules/seasonal-cards/types";

export const SEASONAL_CARD_RECENT_SUPPLIER_LIMIT = 3;

export function buildSeasonalCardRecentSuppliersKey(storeCode: string) {
  return `seasonalCards.recentSuppliers.v1:${storeCode.trim()}`;
}

/** 容错解析本地存储内容：只保留有编码的项，按编码去重，截到上限。 */
export function normalizeSeasonalCardRecentSuppliers(raw: unknown): SeasonalCardSupplierOption[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const seen = new Set<string>();
  const result: SeasonalCardSupplierOption[] = [];
  raw.forEach((item) => {
    if (!item || typeof item !== "object") {
      return;
    }
    const record = item as Record<string, unknown>;
    const supplierCode =
      typeof record.supplierCode === "string" ? record.supplierCode.trim() : "";
    if (!supplierCode || seen.has(supplierCode.toLowerCase())) {
      return;
    }
    seen.add(supplierCode.toLowerCase());
    result.push({
      supplierCode,
      supplierName:
        typeof record.supplierName === "string" && record.supplierName.trim()
          ? record.supplierName.trim()
          : supplierCode,
    });
  });
  return result.slice(0, SEASONAL_CARD_RECENT_SUPPLIER_LIMIT);
}

/** 把刚用过的供应商放到最前，去掉重复，最多保留 3 个。 */
export function pushSeasonalCardRecentSupplier(
  current: SeasonalCardSupplierOption[],
  supplier: SeasonalCardSupplierOption
): SeasonalCardSupplierOption[] {
  const code = supplier.supplierCode.trim();
  if (!code) {
    return current.slice(0, SEASONAL_CARD_RECENT_SUPPLIER_LIMIT);
  }
  return normalizeSeasonalCardRecentSuppliers([
    { supplierCode: code, supplierName: supplier.supplierName },
    ...current.filter((item) => item.supplierCode.toLowerCase() !== code.toLowerCase()),
  ]);
}

/**
 * 最近使用里的名称可能过期（供应商改名），有最新供应商列表时用列表里的名称；
 * 已停用（不在列表里）的供应商从最近使用中隐藏，避免选了提交时报 SUPPLIER_NOT_FOUND。
 */
export function reconcileSeasonalCardRecentSuppliers(
  recent: SeasonalCardSupplierOption[],
  activeSuppliers: SeasonalCardSupplierOption[] | null
) {
  if (!activeSuppliers) {
    return recent;
  }
  const byCode = new Map(
    activeSuppliers.map((item) => [item.supplierCode.toLowerCase(), item] as const)
  );
  return recent
    .map((item) => byCode.get(item.supplierCode.toLowerCase()))
    .filter((item): item is SeasonalCardSupplierOption => Boolean(item));
}
