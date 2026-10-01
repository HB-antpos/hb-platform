import type { StoreSupplyStatus } from "@/modules/supply-notice/types";
import type { StoreOrderProductItem } from "@/modules/shop/types";

export type ScanSource = "camera" | "hid";

export interface ScanFeedbackState {
  status: "ready" | "scanning" | "found" | "added" | "multiple" | "not_found" | "blocked" | "error" | "price_update_required" | "supply_paused";
  message: string;
  barcode?: string;
  productName?: string;
  /** 暂停供货命中时展示货号，便于和同名商品区分。 */
  itemNumber?: string;
  addedQuantity?: number;
  /** 未找到的原因是仓库暂停供货（而非扫错码）；首页据此把条码转成搜索词展示恢复计划。 */
  pausedSupply?: boolean;
  /** 暂停供货命中时的供货说明（后续计划、预计恢复时间），用于提示栏。 */
  supplyStatus?: StoreSupplyStatus;
}

export interface ScanSelectionState {
  barcode: string;
  scanTraceId?: string;
  storeCode?: string | null;
  source: ScanSource;
  items: StoreOrderProductItem[];
}
