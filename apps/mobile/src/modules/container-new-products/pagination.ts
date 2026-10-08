import { clampPage, getPageCount } from "@/components/ui/pagination/pagination-logic";

export const CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS = [50, 100, 200] as const;
export type ContainerNewProductsPageSize = (typeof CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS)[number];
export const CONTAINER_NEW_PRODUCTS_PAGE_SIZE: ContainerNewProductsPageSize = 50;

export interface PageSlice<T> {
  page: number;
  pageCount: number;
  items: T[];
}

// 接口一次返回全部新品，这里在前端切页；页码越界时（下拉刷新后条数变少、换店）夹回有效范围。
export function paginate<T>(items: readonly T[], page: number, pageSize: number = CONTAINER_NEW_PRODUCTS_PAGE_SIZE): PageSlice<T> {
  const pageCount = getPageCount(items.length, pageSize);
  const current = clampPage(page, pageCount);
  const start = (current - 1) * pageSize;
  return { page: current, pageCount, items: items.slice(start, start + pageSize) };
}
