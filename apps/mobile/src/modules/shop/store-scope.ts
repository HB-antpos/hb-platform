import type { Store } from "@/modules/shop/types";

export function getPosEnabledStores(stores: Store[]) {
  // 考勤相关页面只展示明确启用 POS 的门店，不改变全局已分配门店和购物车范围。
  return stores.filter((store) => store.isActive === true);
}

export type StoreScopeInput = {
  stores: Store[];
  isDeviceMode: boolean;
  deviceBoundStore?: Store | null;
};

export function getAssignedStoresForSession({
  stores,
  isDeviceMode,
  deviceBoundStore,
}: StoreScopeInput) {
  if (isDeviceMode) {
    return deviceBoundStore ? [deviceBoundStore] : [];
  }

  return stores;
}

export function getManageableStoresForSession({
  stores,
  isDeviceMode,
  deviceBoundStore,
  isAdmin,
}: StoreScopeInput & { isAdmin: boolean }) {
  if (isDeviceMode) {
    return deviceBoundStore ? [deviceBoundStore] : [];
  }

  if (isAdmin) {
    return stores;
  }

  // 账号模式下后端用 isPrimary=true 标记可修改分店，false 仅用于查看。
  return stores.filter((store) => store.isPrimary === true);
}

export function isStoreManageable(storeCode: string | null | undefined, manageableStores: Store[]) {
  if (!storeCode) {
    return false;
  }

  return manageableStores.some((store) => store.storeCode === storeCode);
}

export function resolveScopedStoreCode({
  currentStoreCode,
  persistedStoreCode,
  deviceBoundStoreCode,
  isDeviceMode,
  stores,
}: {
  currentStoreCode?: string | null;
  persistedStoreCode?: string | null;
  deviceBoundStoreCode?: string | null;
  isDeviceMode: boolean;
  stores: Store[];
}) {
  if (isDeviceMode) {
    return deviceBoundStoreCode ?? null;
  }

  if (currentStoreCode && stores.some((store) => store.storeCode === currentStoreCode)) {
    return currentStoreCode;
  }

  if (persistedStoreCode && stores.some((store) => store.storeCode === persistedStoreCode)) {
    return persistedStoreCode;
  }

  // 没有当前选择、也没有记住的有效分店时默认第一个（列表已排序），所有需要分店的页面进来即有分店，可再手动切换
  return stores[0]?.storeCode ?? null;
}
