import {
  getAssignedStoresForSession,
  getManageableStoresForSession,
  getPosEnabledStores,
  isStoreManageable,
  resolveScopedStoreCode,
  resolveScopedStoreSelection,
} from "./store-scope";
import { IOS_REVIEW_STORES } from "../ios-review/identity";
import { normalizeShopStores } from "./store-normalization";
import type { Store } from "./types";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

const assignedStores = [
  { storeCode: "1006", storeName: "HB WARE HOUSE", isPrimary: false },
  { storeCode: "1004", storeName: "Campbelltown", isPrimary: true },
];

const mixedPosStores = [
  { storeCode: "INACTIVE", storeName: "Inactive primary", isActive: false, isPrimary: true },
  { storeCode: "ENABLED_VIEW", storeName: "Zulu", isActive: true, isPrimary: false },
  { storeCode: "MISSING", storeName: "Missing flag", isPrimary: true },
  { storeCode: "STRING_FALSE", storeName: "Invalid false", isActive: "false", isPrimary: true } as unknown as Store,
  { storeCode: "ENABLED_MANAGE", storeName: "Alpha", isActive: true, isPrimary: true },
];

const posEnabledStores = getPosEnabledStores(mixedPosStores);

assertEqual(
  posEnabledStores.map((store) => store.storeCode).join(","),
  "ENABLED_VIEW,ENABLED_MANAGE",
  "POS scope keeps only literal true flags in the existing order"
);
assertEqual(posEnabledStores[0]?.isPrimary, false, "enabled read-only store keeps isPrimary=false");
assertEqual(posEnabledStores[1]?.isPrimary, true, "enabled manageable store keeps isPrimary=true");

const deviceBoundStoreWithoutPosFlag = {
  storeCode: "DEVICE_BOUND",
  storeName: "Device Bound Store",
};
const devicePickerStores = getPosEnabledStores([deviceBoundStoreWithoutPosFlag]);

assertEqual(
  devicePickerStores.length,
  0,
  "device-bound store without an explicit POS flag is not added to picker candidates"
);
assertEqual(
  resolveScopedStoreCode({
    currentStoreCode: null,
    persistedStoreCode: null,
    deviceBoundStoreCode: deviceBoundStoreWithoutPosFlag.storeCode,
    isDeviceMode: true,
    stores: devicePickerStores,
  }),
  "DEVICE_BOUND",
  "device mode keeps the bound query store even when it is absent from picker candidates"
);

const manageableAssignedStores = getManageableStoresForSession({
  deviceBoundStore: null,
  isAdmin: false,
  isDeviceMode: false,
  stores: mixedPosStores,
});

assertEqual(
  manageableAssignedStores.map((store) => store.storeCode).join(","),
  "INACTIVE,MISSING,STRING_FALSE,ENABLED_MANAGE",
  "picker filtering does not narrow the original isPrimary operation scope"
);
assertEqual(
  isStoreManageable("INACTIVE", manageableAssignedStores),
  true,
  "inactive assigned primary store keeps its existing operation authorization"
);

const inactiveDeviceBoundStore = mixedPosStores[0];
const manageableDeviceStores = getManageableStoresForSession({
  deviceBoundStore: inactiveDeviceBoundStore,
  isAdmin: false,
  isDeviceMode: true,
  stores: posEnabledStores,
});

assertEqual(
  manageableDeviceStores[0]?.storeCode,
  "INACTIVE",
  "device binding remains authoritative even when the store is absent from picker candidates"
);

const normalizedIosReviewStores = normalizeShopStores(IOS_REVIEW_STORES);
const iosReviewPickerStores = getPosEnabledStores(normalizedIosReviewStores);
const iosReviewManageableStores = getManageableStoresForSession({
  deviceBoundStore: null,
  isAdmin: false,
  isDeviceMode: false,
  stores: normalizedIosReviewStores,
});

assertEqual(iosReviewPickerStores.length, 28, "iOS review keeps all 28 POS-enabled picker stores");
assertEqual(
  iosReviewManageableStores.map((store) => store.storeCode).join(","),
  "REV001",
  "iOS review normalization keeps the original primary management scope"
);
assertEqual(
  resolveScopedStoreCode({
    currentStoreCode: "INACTIVE",
    persistedStoreCode: "INACTIVE",
    deviceBoundStoreCode: null,
    isDeviceMode: false,
    stores: posEnabledStores,
  }),
  "ENABLED_VIEW",
  "stale inactive selection is rejected and falls back to the first enabled store"
);

assertEqual(
  resolveScopedStoreCode({
    currentStoreCode: null,
    persistedStoreCode: null,
    deviceBoundStoreCode: null,
    isDeviceMode: false,
    stores: assignedStores,
  }),
  assignedStores[0]?.storeCode ?? null,
  "account sessions without a selection default to the first store"
);

assertEqual(
  resolveScopedStoreCode({
    currentStoreCode: null,
    persistedStoreCode: null,
    deviceBoundStoreCode: null,
    isDeviceMode: false,
    stores: [],
  }),
  null,
  "no stores means no selection"
);

assertEqual(
  getAssignedStoresForSession({
    deviceBoundStore: null,
    isDeviceMode: false,
    stores: assignedStores,
  }).length,
  2,
  "account sessions keep all assigned stores for read access"
);

assertEqual(
  getManageableStoresForSession({
    deviceBoundStore: null,
    isAdmin: false,
    isDeviceMode: false,
    stores: assignedStores,
  }).map((store) => store.storeCode).join(","),
  "1004",
  "non-admin account sessions only manage isPrimary stores"
);

const deviceScopedStores = getAssignedStoresForSession({
  deviceBoundStore: { storeCode: "1024", storeName: "Bankstown" },
  isDeviceMode: true,
  stores: assignedStores,
});

assertEqual(deviceScopedStores.length, 1, "device sessions expose only the bound store");
assertEqual(deviceScopedStores[0]?.storeCode, "1024", "device sessions ignore account stores");

assertEqual(
  resolveScopedStoreCode({
    currentStoreCode: "1006",
    deviceBoundStoreCode: "1024",
    isDeviceMode: true,
    stores: assignedStores,
  }),
  "1024",
  "device sessions lock selection to the bound store"
);

assertEqual(
  resolveScopedStoreCode({
    currentStoreCode: "1004",
    deviceBoundStoreCode: null,
    isDeviceMode: false,
    stores: assignedStores,
  }),
  "1004",
  "account sessions keep an assigned current selection"
);

// 自动默认分店标记：用户选的 → 本机记住 → 自动默认的当前分店 → 第一个
const twoStores = [
  { storeCode: "A", storeName: "Alpha" },
  { storeCode: "B", storeName: "Beta" },
];
assertEqual(
  JSON.stringify(resolveScopedStoreSelection({ isDeviceMode: false, stores: twoStores })),
  JSON.stringify({ storeCode: "A", isAuto: true }),
  "no selection defaults to the first store and marks it auto"
);
assertEqual(
  JSON.stringify(resolveScopedStoreSelection({ currentStoreCode: "A", currentIsAuto: true, isDeviceMode: false, stores: twoStores })),
  JSON.stringify({ storeCode: "A", isAuto: true }),
  "an auto-selected store stays auto across re-syncs"
);
assertEqual(
  JSON.stringify(resolveScopedStoreSelection({ currentStoreCode: "A", currentIsAuto: true, persistedStoreCode: "B", isDeviceMode: false, stores: twoStores })),
  JSON.stringify({ storeCode: "B", isAuto: false }),
  "a remembered user choice wins over an auto-selected store"
);
assertEqual(
  JSON.stringify(resolveScopedStoreSelection({ currentStoreCode: "B", persistedStoreCode: "A", isDeviceMode: false, stores: twoStores })),
  JSON.stringify({ storeCode: "B", isAuto: false }),
  "a user-selected current store is kept"
);
assertEqual(
  JSON.stringify(resolveScopedStoreSelection({ currentStoreCode: "B", deviceBoundStoreCode: "D", isDeviceMode: true, stores: twoStores })),
  JSON.stringify({ storeCode: "D", isAuto: false }),
  "device mode is never auto"
);
assertEqual(
  JSON.stringify(resolveScopedStoreSelection({ isDeviceMode: false, stores: [] })),
  JSON.stringify({ storeCode: null, isAuto: false }),
  "no stores means no selection and not auto"
);
