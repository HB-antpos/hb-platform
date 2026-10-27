import type { PromotionListItem } from "./types";

export interface ActivePromotionRequestContext {
  productCode: string;
  storeCode: string;
}

interface ActivePromotionRequestCoordinatorOptions {
  fetchPromotions: (
    productCode: string,
    storeCode: string,
  ) => Promise<PromotionListItem[]>;
  applyPromotions: (items: PromotionListItem[]) => void;
  onFailure?: (error: unknown, context: ActivePromotionRequestContext) => void;
}

export function createActivePromotionRequestCoordinator({
  fetchPromotions,
  applyPromotions,
  onFailure,
}: ActivePromotionRequestCoordinatorOptions) {
  let latestRequestId = 0;

  return {
    invalidate() {
      latestRequestId += 1;
      applyPromotions([]);
    },
    /** 返回本次请求拿到的促销；被后一次请求取代或请求失败时返回 null（扫码提示音据此不报促销）。 */
    async load(productCode: string, storeCode: string): Promise<PromotionListItem[] | null> {
      const requestId = ++latestRequestId;
      try {
        const items = await fetchPromotions(productCode, storeCode);
        // 只允许最后一次商品/分店请求更新界面，避免连续扫码的迟到响应串数据。
        if (requestId === latestRequestId) {
          applyPromotions(items);
          return items;
        }
        return null;
      } catch (error) {
        if (requestId === latestRequestId) {
          applyPromotions([]);
          onFailure?.(error, { productCode, storeCode });
        }
        return null;
      }
    },
  };
}
