export interface ProductInsightRange {
  startDate: string;
  endDate: string;
}

export interface ProductInsightProduct {
  productCode: string;
  productName: string;
  itemNumber: string | null;
  barcode: string | null;
  productImage: string | null;
  localSupplierCode: string | null;
  localSupplierName: string | null;
}

export interface ProductInsightMovement {
  id: string;
  date: string;
  documentNo: string;
  quantity: number;
  supplierName: string | null;
}

export interface ProductInsightOrder {
  id: string;
  date: string;
  documentNo: string;
  quantity: number;
  deliveredQuantity: number;
  deliveryDate: string | null;
  status: string;
}

export interface ProductInsightDailySales {
  date: string;
  quantity: number;
  amount: number;
}

export interface StoreProductInsight {
  range: ProductInsightRange;
  generatedAt: string;
  salesStatisticLastUpdatedAt: string | null;
  store: { storeCode: string; storeName: string };
  product: ProductInsightProduct;
  sourceType: "local" | "warehouse";
  sales: {
    quantity: number;
    amount: number;
    records: ProductInsightDailySales[];
  };
  purchases: {
    quantity: number;
    documentCount: number;
    records: ProductInsightMovement[];
    lastRecord: ProductInsightMovement | null;
  };
  warehouse: {
    orderedQuantity: number;
    deliveredQuantity: number;
    orders: ProductInsightOrder[];
    deliveries: ProductInsightMovement[];
    lastDelivery: ProductInsightMovement | null;
  };
}

export interface ProductBranchSalesRow {
  storeCode: string;
  storeName: string;
  quantity: number;
  amount: number;
  /** 本区间进货（或配货）数量；旧版服务器未返回时为 null，界面显示「—」。 */
  purchaseQuantity: number | null;
}

/** 进货口径：local = 本地进货单；warehouse = 仓库已出库配货。null 表示服务器未返回进货数据。 */
export type ProductBranchPurchaseSource = "local" | "warehouse";

export interface ProductBranchSales {
  range: ProductInsightRange;
  generatedAt: string;
  salesStatisticLastUpdatedAt: string | null;
  productCode: string;
  scope: "all-pos" | "authorized-pos";
  totalPosStoreCount: number;
  includedStoreCount: number;
  quantity: number;
  amount: number;
  purchaseSource: ProductBranchPurchaseSource | null;
  purchaseQuantity: number | null;
  rows: ProductBranchSalesRow[];
}
