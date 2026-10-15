export type WarehouseShelfStatus = "onShelf" | "offShelf";

export interface WarehouseProduct {
  productCode: string;
  productName: string;
  itemNumber?: string | null;
  barcode?: string | null;
  productImage?: string | null;
  productType?: number | null;
  productTypeLabel?: string | null;
  localSupplierCode?: string | null;
  supplierCode?: string | null;
  supplierName?: string | null;
  grade?: string | null;
  warehouseIsActive: boolean;
  warehouseStatus: WarehouseShelfStatus;
  isActive: boolean;
  purchasePrice?: number | null;
  retailPrice?: number | null;
  domesticPrice?: number | null;
  oemPrice?: number | null;
  importPrice?: number | null;
  stockQuantity?: number | null;
  middlePackageQuantity?: number | null;
  packingQuantity?: number | null;
  volume?: number | null;
  locationGuid?: string | null;
  locationCode?: string | null;
  locationBarcode?: string | null;
  updatedAt?: string | null;
}

export interface WarehouseProductPatchRequest {
  warehouseIsActive?: boolean;
  /** 下架时随请求登记的供货说明；上架时不传。 */
  supplyNotice?: import("@/modules/supply-notice/types").SupplyNoticeInput;
  isActive?: boolean;
  purchasePrice?: number | null;
  retailPrice?: number | null;
  /** 零售价保存时是否把各启用分店的零售价直接改成新价；后端缺省按 true 处理。 */
  syncStoreRetailPrices?: boolean;
  /** 零售价保存时是否给分店生成价格任务（通知分店）；后端缺省按 true 处理。 */
  generateTasks?: boolean;
  domesticPrice?: number | null;
  oemPrice?: number | null;
  importPrice?: number | null;
  stockQuantity?: number | null;
  middlePackageQuantity?: number | null;
  packingQuantity?: number | null;
  volume?: number | null;
  grade?: string | null;
  productImage?: string | null;
}

export interface WarehouseLocation {
  locationGuid: string;
  locationCode?: string | null;
  locationBarcode?: string | null;
  status?: number | null;
  locationType?: number | null;
  productCount: number;
  updatedAt?: string | null;
  updatedBy?: string | null;
  products?: Array<{
    productCode?: string | null;
    itemNumber?: string | null;
    productName?: string | null;
    productImage?: string | null;
    middlePackageQuantity?: number | null;
  }>;
}

export interface WarehouseLocationDetail {
  locationGuid: string;
  locationCode?: string | null;
  locationBarcode?: string | null;
  status?: number | null;
  locationType?: number | null;
  products: Array<{
    productCode?: string | null;
    itemNumber?: string | null;
    productName?: string | null;
    productImage?: string | null;
    middlePackageQuantity?: number | null;
  }>;
  updatedAt?: string | null;
  updatedBy?: string | null;
}

export interface WarehouseLocationMutation {
  locationCode: string;
  locationBarcode?: string | null;
  locationType?: number | null;
  status?: number | null;
}

export interface WarehouseLocationBindRequest {
  productIdentifier: string;
  initialQuantity?: number | null;
}

export interface DirectUploadSignature {
  url: string;
  objectKey: string;
  headers: Record<string, string>;
}

export interface WarehouseProductPrintPayload {
  productCode: string;
  productName: string;
  itemNumber?: string | null;
  barcode?: string | null;
  supplierName?: string | null;
  middlePackageQuantity?: number | null;
  purchasePrice?: number | null;
  retailPrice?: number | null;
  domesticPrice?: number | null;
  oemPrice?: number | null;
  importPrice?: number | null;
  locationCode?: string | null;
  locationBarcode?: string | null;
}

export interface WarehouseLocationPrintPayload {
  locationGuid: string;
  locationCode?: string | null;
  locationBarcode?: string | null;
  itemNumber?: string | null;
  productName?: string | null;
  middlePackageQuantity?: number | null;
  productCount: number;
}
