import assert from "node:assert/strict";
import Module, { createRequire } from "node:module";
import test from "node:test";
import type { Store } from "./types";

type ModuleLoader = (request: string, parent: unknown, isMain: boolean) => unknown;
type CartStateMock = {
  userStores: Store[];
  selectedStore: Store | null;
  setUserStores: () => void;
  setSelectedStore: (store: Store | null) => void;
  setCartSummary: (summary: unknown) => void;
};

test("门店选择持久化失败时保留原门店与购物车摘要", async () => {
  const previousStore: Store = {
    storeCode: "S001",
    storeName: "Existing Store",
  };
  const nextStore: Store = {
    storeCode: "S002",
    storeName: "Next Store",
  };
  const previousCartSummary = { storeCode: previousStore.storeCode };
  const persistenceError = new Error("AsyncStorage write failed");
  let selectedStore: Store | null = previousStore;
  let cartSummary: unknown = previousCartSummary;

  const cartState: CartStateMock = {
    userStores: [previousStore, nextStore],
    selectedStore,
    setUserStores: () => undefined,
    setSelectedStore: (store: Store | null) => {
      selectedStore = store;
      cartState.selectedStore = store;
    },
    setCartSummary: (summary: unknown) => {
      cartSummary = summary;
    },
  };
  const authState = {
    user: {
      userGUID: "user-guid",
      stores: [previousStore, nextStore],
      permissions: [],
      roleNames: [],
    },
    access: { canReadStore: false },
    isAuthenticated: true,
  };
  const deviceState = { session: null };
  const mocks = new Map<string, unknown>([
    [
      "react",
      {
        useEffect: () => undefined,
        useMemo: (factory: () => unknown) => factory(),
        useState: (initialValue: unknown) => [initialValue, () => undefined],
      },
    ],
    [
      "@tanstack/react-query",
      {
        useQuery: () => ({
          data: [],
          error: null,
          fetchStatus: "idle",
          isSuccess: false,
          status: "pending",
        }),
      },
    ],
    [
      "@/modules/shop/api",
      {
        getAllStores: async () => [],
        getStoresByUserGuid: async () => [],
      },
    ],
    ["@/store/auth-store", { useAuthStore: (selector: (state: typeof authState) => unknown) => selector(authState) }],
    [
      "@/store/cart-store",
      {
        useCartStore: Object.assign(
          (selector: (state: typeof cartState) => unknown) => selector(cartState),
          { getState: () => cartState },
        ),
      },
    ],
    ["@/store/device-store", { useDeviceStore: (selector: (state: typeof deviceState) => unknown) => selector(deviceState) }],
    [
      "@/shared/storage/async-storage",
      {
        AppAsyncStorage: {
          getString: async () => null,
          setString: async () => {
            throw persistenceError;
          },
          removeItem: async () => undefined,
        },
      },
    ],
    ["@/modules/shop/types", { STORE_SELECTION_STORAGE_KEY: "shop:selected-store-code" }],
    [
      "@/modules/shop/store-normalization",
      {
        normalizeShopStores: (stores: Store[] | undefined) => stores ?? [],
        sortShopStores: (stores: Store[]) => stores,
      },
    ],
    [
      "@/modules/shop/store-scope",
      {
        getAssignedStoresForSession: ({ stores }: { stores: Store[] }) => stores,
        resolveScopedStoreCode: () => null,
        resolveScopedStoreSelection: () => ({ storeCode: null, isAuto: false }),
      },
    ],
    ["@/modules/shop/warehouse-cart-access", { shouldLoadAllStoresForWarehouseCart: () => false }],
  ]);
  const moduleWithLoader = Module as unknown as { _load: ModuleLoader };
  const originalLoad = moduleWithLoader._load;
  const loadModule = createRequire(__filename);

  moduleWithLoader._load = function mockedLoad(request, parent, isMain) {
    if (mocks.has(request)) {
      return mocks.get(request);
    }

    return originalLoad.call(this, request, parent, isMain);
  };

  try {
    const { useStores } = loadModule("./use-stores") as {
      useStores: () => { selectStore: (store: Store | null) => Promise<void> };
    };
    const { selectStore } = useStores();

    await assert.rejects(selectStore(nextStore), persistenceError);
    assert.deepEqual(
      { selectedStore, cartSummary },
      { selectedStore: previousStore, cartSummary: previousCartSummary },
      "持久化失败时必须原子保留原门店与原购物车摘要",
    );
  } finally {
    moduleWithLoader._load = originalLoad;
  }
});
