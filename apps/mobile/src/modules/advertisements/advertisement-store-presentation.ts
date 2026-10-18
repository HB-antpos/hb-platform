import type { AdvertisementStoreScope } from "@/modules/advertisements/types";
import { withCode } from "@/shared/i18n/punctuation";

export interface AdvertisementStorePresentationItem {
  storeCode: string;
  label: string;
}

export interface AdvertisementStoreSummary {
  isAllStores: boolean;
  items: AdvertisementStorePresentationItem[];
  remainingCount: number;
}

function normalizeStoreCodeKey(storeCode: string) {
  return storeCode.trim().toLocaleLowerCase();
}

export function buildAdvertisementStoreNameMap(
  stores: readonly { storeCode: string; storeName?: string }[]
) {
  const storeNamesByCode = new Map<string, string>();

  for (const store of stores) {
    const storeCodeKey = normalizeStoreCodeKey(store.storeCode);
    const storeName = store.storeName?.trim();
    if (storeCodeKey && storeName) {
      storeNamesByCode.set(storeCodeKey, storeName);
    }
  }

  return storeNamesByCode;
}

export function hasSameAdvertisementStoreCodes(
  leftStoreCodes: readonly string[],
  rightStoreCodes: readonly string[]
) {
  const leftCodes = new Set(leftStoreCodes.map(normalizeStoreCodeKey).filter(Boolean));
  const rightCodes = new Set(rightStoreCodes.map(normalizeStoreCodeKey).filter(Boolean));

  return leftCodes.size === rightCodes.size &&
    [...leftCodes].every((storeCode) => rightCodes.has(storeCode));
}

export function resolveAdvertisementDraftStoreScopes(
  storeCodes: readonly string[],
  responseStores: readonly AdvertisementStoreScope[],
  storeNamesByCode: ReadonlyMap<string, string>
): AdvertisementStoreScope[] {
  const responseStoresByCode = new Map(
    responseStores.map((store) => [normalizeStoreCodeKey(store.storeCode), store])
  );

  return storeCodes.map((rawStoreCode) => {
    const storeCode = rawStoreCode.trim();
    const storeCodeKey = normalizeStoreCodeKey(storeCode);
    const responseStoreName = responseStoresByCode.get(storeCodeKey)?.storeName?.trim();
    const localStoreName = storeNamesByCode.get(storeCodeKey)?.trim();

    return {
      storeCode,
      storeName: responseStoreName || localStoreName || undefined,
    };
  });
}

export function formatAdvertisementStoreLabel(
  store: AdvertisementStoreScope,
  storeNamesByCode: ReadonlyMap<string, string>
) {
  const storeCode = store.storeCode.trim();
  const responseStoreName = store.storeName?.trim();
  const localStoreName = storeNamesByCode.get(normalizeStoreCodeKey(storeCode))?.trim();
  const storeName = responseStoreName || localStoreName;

  if (!storeName || storeName.localeCompare(storeCode, undefined, { sensitivity: "accent" }) === 0) {
    return storeCode;
  }

  return withCode(storeName, storeCode);
}

export function buildAdvertisementStoreSummary(
  stores: AdvertisementStoreScope[],
  storeNamesByCode: ReadonlyMap<string, string>,
  visibleLimit = 3
): AdvertisementStoreSummary {
  if (stores.length === 0) {
    return {
      isAllStores: true,
      items: [],
      remainingCount: 0,
    };
  }

  const normalizedLimit = Math.max(0, Math.trunc(visibleLimit));
  const visibleStores = stores.slice(0, normalizedLimit);

  return {
    isAllStores: false,
    items: visibleStores.map((store) => ({
      storeCode: store.storeCode,
      label: formatAdvertisementStoreLabel(store, storeNamesByCode),
    })),
    remainingCount: Math.max(0, stores.length - visibleStores.length),
  };
}
