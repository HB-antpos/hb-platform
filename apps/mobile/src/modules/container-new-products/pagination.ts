export const CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS = [50, 100, 200] as const;
export type ContainerNewProductsPageSize = (typeof CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS)[number];
export const CONTAINER_NEW_PRODUCTS_PAGE_SIZE: ContainerNewProductsPageSize = 50;

// 本地记住的每页条数可能来自旧版本或被篡改，不在可选项里就回到默认 50
export function normalizePageSize(value: unknown): ContainerNewProductsPageSize {
  const parsed = typeof value === "string" ? Number(value) : value;
  return CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS.find((option) => option === parsed) ?? CONTAINER_NEW_PRODUCTS_PAGE_SIZE;
}

// 跳页输入框：只接受 1..pageCount 的整数，其余返回 null（按钮置灰）
export function parsePageInput(value: string, pageCount: number): number | null {
  const trimmed = value.trim();
  if (!/^\d+$/.test(trimmed)) return null;
  const page = Number(trimmed);
  return page >= 1 && page <= pageCount ? page : null;
}

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
