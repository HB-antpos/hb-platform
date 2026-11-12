import type { AccessControl } from "../auth/types";

/**
 * 订货（仓库本职）需要全部分店的账号：纯仓库员工且能建单；以及后端口径收口后的仓库经理且能建单
 * （门店口径只剩分配的分店，但替任意分店订货 / 发货仍是仓库本职）。
 */
export function shouldLoadAllStoresForWarehouseCart(
  access: Pick<AccessControl, "canCreateOrder" | "isWarehouseStaffOnly" | "hasWarehouseWideScope" | "hasAllStoreScope">
) {
  if (!access.canCreateOrder) return false;
  return access.isWarehouseStaffOnly || (access.hasWarehouseWideScope && !access.hasAllStoreScope);
}

/**
 * 全部分店列表合并当前账号的真实分店关系：保留「管理」标记（isPrimary），考勤、工时等按 isPrimary 过滤的门店管理页仍只出现自己管理的分店。
 */
export function mergeAssignedStoreFlags<T extends { storeCode: string; isPrimary?: boolean }>(
  allStores: T[],
  assignedStores: readonly { storeCode: string; isPrimary?: boolean }[],
): T[] {
  const primaryCodes = new Set(
    assignedStores.filter((store) => store.isPrimary === true).map((store) => store.storeCode.toUpperCase()),
  );
  return allStores.map((store) => ({ ...store, isPrimary: primaryCodes.has(store.storeCode.toUpperCase()) }));
}
