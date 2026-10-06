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

type ScopedStoreInput = {
  currentStoreCode?: string | null;
  /** 当前分店是否为自动默认（不是用户选的） */
  currentIsAuto?: boolean;
  persistedStoreCode?: string | null;
  deviceBoundStoreCode?: string | null;
  isDeviceMode: boolean;
  stores: Store[];
};

/**
 * 解析当前应选中的分店，并标明是否为自动默认。优先级：用户选的当前分店 → 本机记住的分店 → 自动默认的当前分店 → 列表第一个。
 * 自动默认让所有需要分店的页面进来即有分店；订货单、分店进货单、用户管理等「没选过分店时看全部」的页面据 isAuto 仍按全部显示。
 */
export function resolveScopedStoreSelection({
  currentStoreCode,
  currentIsAuto = false,
  persistedStoreCode,
  deviceBoundStoreCode,
  isDeviceMode,
  stores,
}: ScopedStoreInput): { storeCode: string | null; isAuto: boolean } {
  if (isDeviceMode) {
    return { storeCode: deviceBoundStoreCode ?? null, isAuto: false };
  }

  const isAvailable = (storeCode?: string | null): storeCode is string =>
    Boolean(storeCode && stores.some((store) => store.storeCode === storeCode));

  if (isAvailable(currentStoreCode) && !currentIsAuto) {
    return { storeCode: currentStoreCode, isAuto: false };
  }

  if (isAvailable(persistedStoreCode)) {
    return { storeCode: persistedStoreCode, isAuto: false };
  }

  if (isAvailable(currentStoreCode)) {
    return { storeCode: currentStoreCode, isAuto: true };
  }

  const firstStoreCode = stores[0]?.storeCode ?? null;
  return { storeCode: firstStoreCode, isAuto: Boolean(firstStoreCode) };
}

export function resolveScopedStoreCode(input: ScopedStoreInput) {
  return resolveScopedStoreSelection(input).storeCode;
}
