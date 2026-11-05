import type { SyncTargetStore } from "./types";

export interface SyncFieldSelection {
  syncRetailPrice: boolean;
  syncDiscountRate: boolean;
  syncPurchasePrice: boolean;
}

export const DEFAULT_SYNC_FIELDS: SyncFieldSelection = {
  syncRetailPrice: true,
  syncDiscountRate: true,
  syncPurchasePrice: false,
};

/**
 * 仓库商品的进货价由仓库价统一管理：分店扫码对账时会静默按仓库进货价覆盖，
 * 从某个分店同步进货价既没有意义也会被立刻覆盖回去，所以强制不同步、面板也不再显示该开关。
 */
export function resolveEffectiveSyncFields(
  fields: SyncFieldSelection,
  warehouseApplicable: boolean
): SyncFieldSelection {
  return warehouseApplicable ? { ...fields, syncPurchasePrice: false } : fields;
}

/** 仓库商品且本次勾选了零售价或折扣时，同步会一并更新仓库价（后端以同样条件判断）。 */
export function willSyncWarehousePrice(
  fields: Pick<SyncFieldSelection, "syncRetailPrice" | "syncDiscountRate">,
  warehouseApplicable: boolean
): boolean {
  return warehouseApplicable && (fields.syncRetailPrice || fields.syncDiscountRate);
}

export function isSyncTargetSelectable(target: SyncTargetStore) {
  return target.hasRecord;
}

/**
 * 默认全选；特殊商品的分店价是店里刻意维护的，默认不选（仍可手动勾选）；
 * 没有该商品记录的分店无法同步，始终排除。
 */
export function getDefaultSelectedSyncTargets(targets: readonly SyncTargetStore[]): Set<string> {
  return new Set(
    targets
      .filter((target) => isSyncTargetSelectable(target) && !target.isSpecialProduct)
      .map((target) => target.storeCode)
  );
}

export function getSelectableSyncTargetCodes(targets: readonly SyncTargetStore[]): string[] {
  return targets.filter(isSyncTargetSelectable).map((target) => target.storeCode);
}

export type SyncTargetChange =
  | { kind: "noRecord" }
  | { kind: "same" }
  | { kind: "discountOnly" }
  | { kind: "price"; from: number | null; to: number | null };

function sameMoney(left: number | null, right: number | null) {
  return left != null && right != null && Math.round(left * 100) === Math.round(right * 100);
}

function sameRate(left: number | null, right: number | null) {
  return Math.abs((left ?? 0) - (right ?? 0)) <= 0.00005;
}

/** 每行右侧的「现价 → 新价 / 已一致 / 仅折扣变化」，只比较本次勾选要同步的字段。 */
export function describeSyncTargetChange(
  target: SyncTargetStore,
  source: { retailPrice: number | null; discountRate: number | null },
  fields: Pick<SyncFieldSelection, "syncRetailPrice" | "syncDiscountRate">
): SyncTargetChange {
  if (!target.hasRecord) {
    return { kind: "noRecord" };
  }
  const priceChanges = fields.syncRetailPrice && !sameMoney(target.retailPrice, source.retailPrice);
  const discountChanges = fields.syncDiscountRate && !sameRate(target.discountRate, source.discountRate);
  if (priceChanges) {
    return { kind: "price", from: target.retailPrice, to: source.retailPrice };
  }
  return discountChanges ? { kind: "discountOnly" } : { kind: "same" };
}
