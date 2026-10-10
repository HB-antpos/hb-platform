// 供应商表一次取回全量、前端切页；商品明细由后端分页（后端每页上限 500，见 EnhancedProductDetailMaxPageSize）。
export const SUPPLIER_PAGE_SIZE_OPTIONS = [50, 100] as const;
export const PRODUCT_PAGE_SIZE_OPTIONS = [100, 200, 500] as const;
export const SUPPLIER_PAGE_SIZE: number = SUPPLIER_PAGE_SIZE_OPTIONS[0];
export const PRODUCT_PAGE_SIZE: number = PRODUCT_PAGE_SIZE_OPTIONS[0];

export function getPageRows<T>(rows: T[], page: number, pageSize: number) {
  return rows.slice((page - 1) * pageSize, page * pageSize);
}
