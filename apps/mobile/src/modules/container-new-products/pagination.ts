export const CONTAINER_NEW_PRODUCTS_PAGE_SIZE = 20;

export interface PageSlice<T> {
  page: number;
  pageCount: number;
  items: T[];
}

// 接口一次返回全部新品，这里在前端切页；页码越界时（下拉刷新后条数变少、换店）夹回有效范围。
export function paginate<T>(items: readonly T[], page: number, pageSize = CONTAINER_NEW_PRODUCTS_PAGE_SIZE): PageSlice<T> {
  const pageCount = Math.max(1, Math.ceil(items.length / pageSize));
  const current = Math.min(Math.max(1, Math.floor(page) || 1), pageCount);
  const start = (current - 1) * pageSize;
  return { page: current, pageCount, items: items.slice(start, start + pageSize) };
}
