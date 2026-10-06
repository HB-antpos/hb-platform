import { create } from "zustand";
import type { Store, StoreOrderCart } from "@/modules/shop/types";

interface CartStoreState {
  userStores: Store[];
  selectedStore: Store | null;
  /** 当前分店是否为「没选过分店时自动默认的第一个」；用户手动选过或本机记住的为 false */
  selectedStoreIsAuto: boolean;
  cartSummary: StoreOrderCart | null;
  cartSyncPendingByStore: Record<string, number>;
  adjustCartSyncPending: (storeCode: string, delta: number) => void;
  setUserStores: (stores: Store[]) => void;
  setSelectedStore: (store: Store | null, options?: { auto?: boolean }) => void;
  setCartSummary: (cartSummary: StoreOrderCart | null) => void;
  reset: () => void;
}

function normalizeStoreCode(storeCode?: string | null) {
  const normalized = storeCode?.trim();
  return normalized ? normalized : null;
}

export const useCartStore = create<CartStoreState>((set) => ({
  userStores: [],
  selectedStore: null,
  selectedStoreIsAuto: false,
  cartSummary: null,
  cartSyncPendingByStore: {},
  adjustCartSyncPending: (storeCode, delta) =>
    set((state) => {
      const normalizedStoreCode = normalizeStoreCode(storeCode);
      if (!normalizedStoreCode || delta === 0) {
        return state;
      }

      const nextCount = Math.max(0, (state.cartSyncPendingByStore[normalizedStoreCode] ?? 0) + delta);
      const nextPendingByStore = { ...state.cartSyncPendingByStore };
      if (nextCount > 0) {
        nextPendingByStore[normalizedStoreCode] = nextCount;
      } else {
        // 同步完成后移除门店键，避免长期保留零值状态。
        delete nextPendingByStore[normalizedStoreCode];
      }

      return {
        cartSyncPendingByStore: nextPendingByStore,
      };
    }),
  setUserStores: (stores) =>
    set((state) => {
      const selectedStore = state.selectedStore
        ? stores.find((item) => item.storeCode === state.selectedStore?.storeCode) ?? null
        : null;

      return {
        userStores: stores,
        selectedStore,
        selectedStoreIsAuto: selectedStore ? state.selectedStoreIsAuto : false,
      };
    }),
  setSelectedStore: (selectedStore, options) => set({ selectedStore, selectedStoreIsAuto: Boolean(selectedStore && options?.auto) }),
  setCartSummary: (cartSummary) => set({ cartSummary }),
  reset: () =>
    set({
      userStores: [],
      selectedStore: null,
      selectedStoreIsAuto: false,
      cartSummary: null,
      cartSyncPendingByStore: {},
    }),
}));
