import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { getAllStores, getStoresByUserGuid } from "@/modules/shop/api";
import { useAuthStore } from "@/store/auth-store";
import { useCartStore } from "@/store/cart-store";
import { useDeviceStore } from "@/store/device-store";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { STORE_SELECTION_STORAGE_KEY, type Store } from "@/modules/shop/types";
import { normalizeShopStores, sortShopStores } from "@/modules/shop/store-normalization";
import { getAssignedStoresForSession, resolveScopedStoreSelection } from "@/modules/shop/store-scope";
import { shouldLoadAllStoresForWarehouseCart } from "@/modules/shop/warehouse-cart-access";
import {
  buildStoreSelectionScopeKey,
  isStoreSelectionReadyForScope,
} from "@/modules/shop/store-selection-readiness";

export function useStores() {
  const user = useAuthStore((state) => state.user);
  const access = useAuthStore((state) => state.access);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const deviceSession = useDeviceStore((state) => state.session);
  const userStores = useCartStore((state) => state.userStores);
  const selectedStore = useCartStore((state) => state.selectedStore);
  const setUserStores = useCartStore((state) => state.setUserStores);
  const setSelectedStore = useCartStore((state) => state.setSelectedStore);
  const setCartSummary = useCartStore((state) => state.setCartSummary);
  const [isHydratingSelection, setIsHydratingSelection] = useState(false);
  const [hydratedSelectionScopeKey, setHydratedSelectionScopeKey] = useState<string | null>(null);
  const userGuid = user?.userGUID ?? null;
  const hasUserSession = Boolean(isAuthenticated && userGuid);
  const useAllStoresForWarehouseCart = shouldLoadAllStoresForWarehouseCart(access);
  const isDeviceMode = Boolean(
    deviceSession?.hardwareId &&
      deviceSession.authCode &&
      deviceSession.storeCode &&
      !hasUserSession
  );
  const deviceBoundStore = useMemo<Store | null>(
    () =>
      deviceSession?.storeCode
        ? {
            storeCode: deviceSession.storeCode,
            storeName: deviceSession.storeName || deviceSession.storeCode,
          }
        : null,
    [deviceSession?.storeCode, deviceSession?.storeName]
  );
  const storesQuery = useQuery({
    queryKey: ["userStores", userGuid, deviceSession?.storeCode, useAllStoresForWarehouseCart],
    enabled: !isDeviceMode && Boolean(userGuid),
    queryFn: async () => {
      if (!userGuid) {
        return [];
      }

      const embeddedStores = sortShopStores(normalizeShopStores(user?.stores));

      if (useAllStoresForWarehouseCart) {
        const stores = sortShopStores(await getAllStores());
        console.info("[useStores] loaded all stores for warehouse cart", {
          count: stores.length,
          endpoint: "/stores/all-by-name",
          userGuid,
        });
        return stores;
      }

      try {
        const apiStores = sortShopStores(await getStoresByUserGuid(userGuid));

        if (apiStores.length) {
          console.info("[useStores] loaded stores from user endpoint", {
            canReadStore: access.canReadStore,
            count: apiStores.length,
            endpoint: `/Users/guid/${userGuid}/stores`,
            embeddedCount: embeddedStores.length,
            userGuid,
          });
          return apiStores;
        }

        if (embeddedStores.length) {
          // 账号模式优先使用用户分店接口；接口空数据时才回退登录态中的已分配分店。
          console.warn("[useStores] user endpoint returned empty; fallback to embedded assigned stores", {
            canReadStore: access.canReadStore,
            embeddedCount: embeddedStores.length,
            endpoint: `/Users/guid/${userGuid}/stores`,
            permissions: user?.permissions,
            userGuid,
          });
          return embeddedStores;
        }

        console.warn("[useStores] no stores available from endpoint or embedded user data", {
          canReadStore: access.canReadStore,
          endpoint: `/Users/guid/${userGuid}/stores`,
          permissions: user?.permissions,
          roleNames: user?.roleNames,
          userGuid,
        });
        return [];
      } catch (error) {
        if (embeddedStores.length) {
          console.warn("[useStores] user endpoint failed; fallback to embedded stores", {
            canReadStore: access.canReadStore,
            embeddedCount: embeddedStores.length,
            endpoint: `/Users/guid/${userGuid}/stores`,
            error,
            userGuid,
          });
          return embeddedStores;
        }

        console.error("[useStores] failed to load stores", {
          canReadStore: access.canReadStore,
          endpoint: `/Users/guid/${userGuid}/stores`,
          error,
          permissions: user?.permissions,
          roleNames: user?.roleNames,
          userGuid,
        });
        throw error;
      }
    },
  });
  const selectionScopeKey = useMemo(
    () =>
      userGuid && storesQuery.isSuccess
        ? buildStoreSelectionScopeKey({
            storeCodes: (storesQuery.data ?? []).map((store) => store.storeCode),
            useAllStores: useAllStoresForWarehouseCart,
            userGuid,
          })
        : null,
    [storesQuery.data, storesQuery.isSuccess, useAllStoresForWarehouseCart, userGuid]
  );

  useEffect(() => {
    if (isDeviceMode) {
      if (!deviceBoundStore) {
        return;
      }

      setUserStores([deviceBoundStore]);
      setSelectedStore(deviceBoundStore);
      void AppAsyncStorage.setString(STORE_SELECTION_STORAGE_KEY, deviceBoundStore.storeCode);
      return;
    }

    if (!userGuid) {
      setUserStores([]);
      setSelectedStore(null);
      setCartSummary(null);
      return;
    }

    if (!storesQuery.isSuccess) {
      return;
    }

    let cancelled = false;

    async function syncStores() {
      setIsHydratingSelection(true);

      const stores = storesQuery.data ?? [];
      setUserStores(stores);

      const { selectedStore: currentSelectedStore, selectedStoreIsAuto: currentIsAuto } = useCartStore.getState();
      const persistedStoreCode = await AppAsyncStorage.getString(STORE_SELECTION_STORAGE_KEY);

      if (cancelled) {
        return;
      }

      const { storeCode: nextStoreCode, isAuto } = resolveScopedStoreSelection({
        currentStoreCode: currentSelectedStore?.storeCode,
        currentIsAuto,
        persistedStoreCode,
        isDeviceMode: false,
        stores,
      });
      const nextSelectedStore = nextStoreCode ? stores.find((item) => item.storeCode === nextStoreCode) ?? null : null;

      setSelectedStore(nextSelectedStore ?? null, { auto: isAuto });

      // 自动默认的分店不写入本机记住，避免下次启动被当成用户选择
      if (nextSelectedStore?.storeCode && !isAuto) {
        await AppAsyncStorage.setString(STORE_SELECTION_STORAGE_KEY, nextSelectedStore.storeCode);
      } else {
        await AppAsyncStorage.removeItem(STORE_SELECTION_STORAGE_KEY);
      }

      if (!cancelled) {
        setHydratedSelectionScopeKey(selectionScopeKey);
        setIsHydratingSelection(false);
      }
    }

    void syncStores();

    return () => {
      cancelled = true;
    };
  }, [deviceBoundStore, isDeviceMode, selectionScopeKey, setCartSummary, setSelectedStore, setUserStores, storesQuery.data, storesQuery.isSuccess, userGuid]);

  const effectiveStores = getAssignedStoresForSession({
    stores: userStores,
    isDeviceMode,
    deviceBoundStore,
  });
  const effectiveSelectedStore = isDeviceMode ? deviceBoundStore : selectedStore;
  const selectedStoreIsAuto = useCartStore((state) => state.selectedStoreIsAuto);
  const isSelectedStoreAuto = !isDeviceMode && Boolean(effectiveSelectedStore) && selectedStoreIsAuto;
  const selectedStoreCode = effectiveSelectedStore?.storeCode ?? null;
  const isStoreSelectionReady = isStoreSelectionReadyForScope({
    currentScopeKey: selectionScopeKey,
    deviceStoreCode: deviceBoundStore?.storeCode,
    hydratedScopeKey: hydratedSelectionScopeKey,
    isDeviceMode,
    userGuid,
  });
  const embeddedStores = useMemo(() => sortShopStores(normalizeShopStores(user?.stores)), [user?.stores]);
  const debugInfo = useMemo(
    () => ({
      canReadStore: access.canReadStore,
      deviceStoreCode: deviceSession?.storeCode ?? null,
      embeddedStoreCount: embeddedStores.length,
      enabled: Boolean(userGuid),
      errorMessage: storesQuery.error instanceof Error ? storesQuery.error.message : null,
      fetchStatus: storesQuery.fetchStatus,
      hasUserGuid: Boolean(userGuid),
      queryStatus: storesQuery.status,
      roleNames: user?.roleNames ?? [],
      storeCount: effectiveStores.length,
      useAllStoresForWarehouseCart,
      userGuid: userGuid ?? "",
    }),
    [
      access.canReadStore,
      deviceSession?.storeCode,
      embeddedStores.length,
      effectiveStores.length,
      storesQuery.error,
      storesQuery.fetchStatus,
      storesQuery.status,
      user?.roleNames,
      useAllStoresForWarehouseCart,
      userGuid,
    ]
  );

  const actions = useMemo(
    () => ({
      async selectStore(store: Store | null) {
        if (isDeviceMode && deviceBoundStore) {
          await AppAsyncStorage.setString(STORE_SELECTION_STORAGE_KEY, deviceBoundStore.storeCode);
          setSelectedStore(deviceBoundStore);
          return;
        }

        if (store?.storeCode) {
          await AppAsyncStorage.setString(STORE_SELECTION_STORAGE_KEY, store.storeCode);
        } else {
          await AppAsyncStorage.removeItem(STORE_SELECTION_STORAGE_KEY);
        }

        // 先确保持久化成功，避免写入失败后内存门店与购物车被部分切换。
        setSelectedStore(store);
        setCartSummary(null);
      },
    }),
    [deviceBoundStore, isDeviceMode, setCartSummary, setSelectedStore]
  );

  return {
    stores: effectiveStores,
    selectedStore: effectiveSelectedStore,
    selectedStoreCode,
    /** 当前分店是自动默认的第一个（用户没选过）；「没选过分店时看全部」的页面据此仍按全部显示 */
    isSelectedStoreAuto,
    isDeviceMode,
    deviceBoundStore,
    isHydratingSelection,
    isStoreSelectionReady,
    debugInfo,
    ...storesQuery,
    ...actions,
  };
}
