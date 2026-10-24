import { sortStoresByName } from "@/shared/utils/name-sort";

export type NormalizedShopStore = {
  storeGUID?: string;
  storeCode: string;
  storeName: string;
  isActive?: boolean;
  postcode?: string;
  stateCode?: string;
  isPrimary?: boolean;
  assignedAt?: string;
};

type RawStoreRecord = Record<string, unknown>;

function asRecord(value: unknown): RawStoreRecord | null {
  return value && typeof value === "object" ? (value as RawStoreRecord) : null;
}

function getStringValue(...values: unknown[]) {
  for (const value of values) {
    if (typeof value !== "string") {
      continue;
    }

    const trimmed = value.trim();
    if (trimmed) {
      return trimmed;
    }
  }

  return undefined;
}

function getStoreItems(payload: unknown) {
  if (Array.isArray(payload)) {
    return payload;
  }

  const data = asRecord(payload);
  const items = data?.items ?? data?.Items ?? data?.branches ?? data?.Branches;
  return Array.isArray(items) ? items : [];
}

export function normalizeShopStores(payload: unknown): NormalizedShopStore[] {
  return getStoreItems(payload)
    .map<NormalizedShopStore | null>((item) => {
      const record = asRecord(item);
      if (!record) {
        return null;
      }

      // 后端可下单分店接口会返回 Branch* 字段，旧接口仍可能返回 Store* 字段。
      const storeCode = getStringValue(
        record.storeCode,
        record.StoreCode,
        record.branchCode,
        record.BranchCode,
        record.code,
        record.Code
      );

      if (!storeCode) {
        return null;
      }

      return {
        storeGUID: getStringValue(
          record.storeGUID,
          record.storeGuid,
          record.StoreGUID,
          record.StoreGuid,
          record.branchGUID,
          record.branchGuid,
          record.BranchGUID,
          record.BranchGuid
        ),
        storeCode,
        isActive:
          typeof record.isActive === "boolean"
            ? record.isActive
            : typeof record.IsActive === "boolean"
              ? record.IsActive
              : undefined,
        storeName:
          getStringValue(
            record.storeName,
            record.StoreName,
            record.branchName,
            record.BranchName,
            record.name,
            record.Name
          ) ?? storeCode,
        postcode: getStringValue(record.postcode, record.postCode, record.Postcode, record.PostCode),
        stateCode: getStringValue(record.stateCode, record.StateCode, record.state, record.State),
        isPrimary:
          typeof record.isPrimary === "boolean"
            ? record.isPrimary
            : typeof record.IsPrimary === "boolean"
              ? record.IsPrimary
              : undefined,
        assignedAt: getStringValue(record.assignedAt, record.AssignedAt),
      };
    })
    .filter((item): item is NormalizedShopStore => Boolean(item));
}

export function normalizeShopStoresApiResponse(payload: unknown): NormalizedShopStore[] {
  const data = asRecord(payload);
  // 后端有的接口返回 { data }，有的旧接口返回 { Data }；归一化前先统一解包。
  return normalizeShopStores(data?.data ?? data?.Data ?? payload);
}

export function sortShopStores<T extends { storeCode: string; storeName?: string }>(stores: T[]) {
  // 统一走「按名字排序」比较器（名字为空退回编号，数字按数值，同名按编号）。
  return sortStoresByName(stores);
}
