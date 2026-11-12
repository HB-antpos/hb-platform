import { mergeAssignedStoreFlags, shouldLoadAllStoresForWarehouseCart } from "./warehouse-cart-access";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

const base = { hasWarehouseWideScope: false, hasAllStoreScope: false };

assertEqual(
  shouldLoadAllStoresForWarehouseCart({
    ...base,
    isWarehouseStaffOnly: true,
    canCreateOrder: true,
  }),
  true,
  "pure WarehouseStaff with Orders.Create loads all stores for dedicated cart"
);

assertEqual(
  shouldLoadAllStoresForWarehouseCart({
    ...base,
    isWarehouseStaffOnly: true,
    canCreateOrder: false,
  }),
  false,
  "pure WarehouseStaff without Orders.Create keeps normal scoped store loading"
);

assertEqual(
  shouldLoadAllStoresForWarehouseCart({
    isWarehouseStaffOnly: false,
    canCreateOrder: true,
    hasWarehouseWideScope: true,
    hasAllStoreScope: true,
  }),
  false,
  "admins and unrestricted warehouse managers keep normal store loading (user endpoint already returns all stores)"
);

assertEqual(
  shouldLoadAllStoresForWarehouseCart({
    isWarehouseStaffOnly: false,
    canCreateOrder: true,
    hasWarehouseWideScope: true,
    hasAllStoreScope: false,
  }),
  true,
  "store-scoped warehouse manager still orders for every store"
);

assertEqual(
  shouldLoadAllStoresForWarehouseCart({
    isWarehouseStaffOnly: false,
    canCreateOrder: false,
    hasWarehouseWideScope: true,
    hasAllStoreScope: false,
  }),
  false,
  "store-scoped warehouse manager without Orders.Create keeps assigned stores"
);

assertEqual(
  shouldLoadAllStoresForWarehouseCart({ ...base, isWarehouseStaffOnly: false, canCreateOrder: true }),
  false,
  "store managers keep normal store loading"
);

const merged = mergeAssignedStoreFlags(
  [
    { storeCode: "1001", storeName: "A" },
    { storeCode: "1002", storeName: "B" },
    { storeCode: "1003", storeName: "C", isPrimary: true },
  ],
  [
    { storeCode: "1001", isPrimary: true },
    { storeCode: "1002", isPrimary: false },
  ],
);
assertEqual(merged.map((store) => `${store.storeCode}:${store.isPrimary}`).join(","), "1001:true,1002:false,1003:false",
  "only stores the account really manages keep the manage flag");
