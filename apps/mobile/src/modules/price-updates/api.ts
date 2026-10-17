import type { AxiosRequestConfig } from "axios";
import { apiClient } from "@/shared/api/client";
import { useDeviceStore } from "@/store/device-store";
import { runBatchInChunks } from "./batch-chunks";
import {
  normalizeCancelPriceUpdateTasksResult,
  normalizePendingStoreCount,
  normalizePriceNotificationPreview,
  normalizePriceUpdateBatchResult,
  normalizePriceUpdatePendingCount,
  normalizePriceUpdateSummary,
  normalizePriceUpdateTaskPage,
  normalizeSuggestedDiscountLookup,
  normalizeSyncTargets,
} from "./normalize";
import { parsePriceNotificationHeader, type PriceNotificationSummary } from "./price-notification";
import type {
  PriceNotificationPreview,
  PriceUpdateApplyItem,
  PriceUpdateBatchResult,
  PriceUpdateLabelMode,
  PriceUpdateTaskQuery,
  StorePriceUpdateSummary,
  StorePriceUpdateTaskPage,
  SuggestedDiscountSource,
  SyncTargetsResult,
  SyncToOtherStoresRequest,
} from "./types";

const BASE_PATH = "/react/v1/store-product-maintenance";
const MONITOR_BASE_PATH = "/react/v1/store-price-update-tasks";

// 与商品维护模块一致的设备/账号双轨：设备会话带设备头，账号会话由拦截器补 Bearer。
function buildRequestConfig(): AxiosRequestConfig {
  const session = useDeviceStore.getState().session;
  if (!session?.hardwareId || !session.authCode) {
    return {};
  }

  return {
    headers: {
      "X-Device-Id": session.hardwareId,
      "X-Auth-Code": session.authCode,
    },
  };
}

export async function getPriceUpdateTasks(query: PriceUpdateTaskQuery): Promise<StorePriceUpdateTaskPage> {
  const response = await apiClient.get(`${BASE_PATH}/price-update-tasks`, {
    ...buildRequestConfig(),
    params: {
      storeCode: query.storeCode,
      status: query.status,
      ...(query.kind ? { kind: query.kind } : {}),
      ...(query.overdueOnly ? { overdueOnly: true } : {}),
      ...(query.keyword?.trim() ? { keyword: query.keyword.trim() } : {}),
      ...(query.hqSyncFailedOnly ? { hqSyncFailedOnly: true } : {}),
      page: query.page,
      pageSize: query.pageSize,
    },
  });
  return normalizePriceUpdateTaskPage(response.data);
}

export async function getPriceUpdatePendingCount(storeCode: string): Promise<number> {
  const response = await apiClient.get(`${BASE_PATH}/price-update-tasks/count`, {
    ...buildRequestConfig(),
    params: { storeCode },
  });
  return normalizePriceUpdatePendingCount(response.data);
}

/**
 * 按分店汇总：当前账号 / 设备可访问的每家分店的未完成数与超期数。
 * 每小时提醒、分店切换条、工作台角标和报表锁定都读这一份，不用逐店查询。
 */
export async function getPriceUpdateSummary(): Promise<StorePriceUpdateSummary> {
  const response = await apiClient.get(`${BASE_PATH}/price-update-tasks/summary`, buildRequestConfig());
  return normalizePriceUpdateSummary(response.data);
}

// 下面三个批量接口都按 200 条一批串行提交，见 runBatchInChunks。
export async function applyPriceUpdateTasks(
  storeCode: string,
  items: PriceUpdateApplyItem[]
): Promise<PriceUpdateBatchResult> {
  return runBatchInChunks({
    items,
    taskIdOf: (item) => item.taskId,
    call: async (chunk) => {
      const response = await apiClient.post(
        `${BASE_PATH}/price-update-tasks/apply`,
        { storeCode, items: chunk },
        buildRequestConfig()
      );
      return normalizePriceUpdateBatchResult(response.data);
    },
  });
}

export async function keepStorePriceForTasks(
  storeCode: string,
  taskIds: number[]
): Promise<PriceUpdateBatchResult> {
  return runBatchInChunks({
    items: taskIds,
    taskIdOf: (taskId) => taskId,
    call: async (chunk) => {
      const response = await apiClient.post(
        `${BASE_PATH}/price-update-tasks/keep`,
        { storeCode, taskIds: chunk },
        buildRequestConfig()
      );
      return normalizePriceUpdateBatchResult(response.data);
    },
  });
}

/**
 * 按商品撤销未完成任务（与 Web「价格变更任务」页「按商品」同一接口 / 同一后端策略）。
 * 不传 storeCodes = 该商品在**所有分店**的未完成任务；只把未完成任务标记为已取消并记录撤销人，
 * 不改任何价格，已处理或不存在的会被跳过并逐条回报。仅账号会话可用（设备会话没有账号权限）。
 */
export async function cancelPriceUpdateTasksForProduct(
  productCode: string,
  note?: string
): Promise<PriceUpdateBatchResult> {
  const trimmedNote = note?.trim();
  const response = await apiClient.post(`${MONITOR_BASE_PATH}/cancel`, {
    productCode,
    ...(trimmedNote ? { note: trimmedNote } : {}),
  });
  return normalizeCancelPriceUpdateTasksResult(response.data);
}

/**
 * 某商品目前有未完成任务的分店数，用于确认框写「共 N 家分店」。
 * 取不到（没有监控查看权限、网络失败、商品不在结果里）一律返回 null，确认框退回不带数量的说明，不阻塞撤销。
 */
export async function getPendingStoreCountForProduct(productCode: string): Promise<number | null> {
  try {
    const response = await apiClient.get(`${MONITOR_BASE_PATH}/by-product`, {
      params: { keyword: productCode, onlyIncomplete: true, page: 1, pageSize: 100 },
    });
    return normalizePendingStoreCount(response.data, productCode);
  } catch {
    return null;
  }
}

export async function completePriceUpdateLabels(
  storeCode: string,
  taskIds: number[],
  mode: PriceUpdateLabelMode
): Promise<PriceUpdateBatchResult> {
  return runBatchInChunks({
    items: taskIds,
    taskIdOf: (taskId) => taskId,
    call: async (chunk) => {
      const response = await apiClient.post(
        `${BASE_PATH}/price-update-tasks/labels`,
        { storeCode, taskIds: chunk, mode },
        buildRequestConfig()
      );
      return normalizePriceUpdateBatchResult(response.data);
    },
  });
}

export async function getSyncTargets(productCode: string, sourceStoreCode: string): Promise<SyncTargetsResult> {
  const response = await apiClient.get(
    `${BASE_PATH}/products/${encodeURIComponent(productCode)}/sync-targets`,
    { ...buildRequestConfig(), params: { sourceStoreCode } }
  );
  return normalizeSyncTargets(response.data);
}

export async function syncToOtherStores(
  payload: SyncToOtherStoresRequest
): Promise<{ updatedStoreCount: number; notification: PriceNotificationSummary | null }> {
  const response = await apiClient.post(
    `${BASE_PATH}/products/sync-to-other-stores`,
    payload,
    buildRequestConfig()
  );
  const data = (response.data && typeof response.data === "object" ? response.data : {}) as Record<string, unknown>;
  const updatedStoreCount = Number(data.updatedStoreCount ?? data.UpdatedStoreCount ?? 0);
  return {
    updatedStoreCount: Number.isFinite(updatedStoreCount) ? updatedStoreCount : 0,
    notification: parsePriceNotificationHeader(response.headers),
  };
}

export async function getPriceNotificationPreview(input: {
  productCode: string;
  /** 省略 = 零售价不变。 */
  retailPrice?: number | null;
  /** undefined = 建议折扣不变；null = 清空为未设置。 */
  suggestedDiscountRate?: number | null;
}): Promise<PriceNotificationPreview> {
  const suggestedDiscountSpecified = input.suggestedDiscountRate !== undefined;
  const response = await apiClient.get(`${MONITOR_BASE_PATH}/preview`, {
    params: {
      productCode: input.productCode,
      ...(input.retailPrice != null ? { retailPrice: input.retailPrice } : {}),
      suggestedDiscountSpecified,
      ...(suggestedDiscountSpecified && input.suggestedDiscountRate != null
        ? { suggestedDiscountRate: input.suggestedDiscountRate }
        : {}),
    },
  });
  return normalizePriceNotificationPreview(response.data);
}

export async function lookupSuggestedDiscount(productCode: string): Promise<number | null> {
  const response = await apiClient.post(`${MONITOR_BASE_PATH}/suggested-discounts/lookup`, {
    productCodes: [productCode],
  });
  return normalizeSuggestedDiscountLookup(response.data).get(productCode) ?? null;
}

export async function updateSuggestedDiscount(
  productCodes: string[],
  suggestedDiscountRate: number | null,
  source: SuggestedDiscountSource
): Promise<{ changedCount: number; notification: PriceNotificationSummary | null }> {
  const response = await apiClient.put(`${MONITOR_BASE_PATH}/suggested-discounts`, {
    productCodes,
    suggestedDiscountRate,
    source,
  });
  const data = (response.data && typeof response.data === "object" ? response.data : {}) as Record<string, unknown>;
  const changedCount = Number(data.changedCount ?? data.ChangedCount ?? 0);
  return {
    changedCount: Number.isFinite(changedCount) ? changedCount : 0,
    notification: parsePriceNotificationHeader(response.headers),
  };
}
