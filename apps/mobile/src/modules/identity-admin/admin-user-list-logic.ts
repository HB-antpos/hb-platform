// 管理员版人员列表（AdminUserList）的纯逻辑：筛选条件 → /Users/optimized 查询参数、排序预设、
// 「下拉加载更多」的分页累加与去重。不依赖 React Native，可在 Node 下测试。
//
// 服务端只支持这些筛选：search（姓名 / 账号 / 邮箱 / 电话）、roleGuid、storeGuid、isActive、sortBy、sortDirection。
// 「待首次登录 / 不能登录 / 无邮箱」没有服务端筛选，列表接口也不返回 loginEnabled，所以管理员版不提供这组筛选（见汇报）。
import type { IdentityPagedResult, IdentityUser, IdentityUserQuery } from "./types";

/** 每页条数：沿用原分页参数（原默认 20），下拉到底再取下一页并累加。 */
export const ADMIN_USER_PAGE_SIZE = 20;

export type AdminSortKey = "lastLoginAt" | "username" | "fullName";
/** 排序选项顺序即弹层里的顺序；默认「最近登录」。 */
export const ADMIN_SORT_ORDER: readonly AdminSortKey[] = ["lastLoginAt", "username", "fullName"];

/** 每个排序键的默认方向：最近登录新的在前；账号、姓名按字母升序。 */
export function resolveAdminSort(key: AdminSortKey): { sortBy: string; sortDirection: "asc" | "desc" } {
  switch (key) {
    case "username":
      return { sortBy: "username", sortDirection: "asc" };
    case "fullName":
      return { sortBy: "fullName", sortDirection: "asc" };
    default:
      return { sortBy: "lastLoginAt", sortDirection: "desc" };
  }
}

export type AdminStatusFilter = "all" | "active" | "inactive";

export function statusFilterToIsActive(filter: AdminStatusFilter): boolean | undefined {
  if (filter === "active") return true;
  if (filter === "inactive") return false;
  return undefined;
}

export interface AdminUserFilters {
  search: string;
  /** 空串 = 全部分店。 */
  storeGuid: string;
  /** 空串 = 全部角色。 */
  roleGuid: string;
  status: AdminStatusFilter;
  sort: AdminSortKey;
}

export const DEFAULT_ADMIN_USER_FILTERS: AdminUserFilters = {
  search: "",
  storeGuid: "",
  roleGuid: "",
  status: "all",
  sort: "lastLoginAt",
};

export function buildAdminUserQuery(filters: AdminUserFilters, page: number, pageSize = ADMIN_USER_PAGE_SIZE): IdentityUserQuery {
  const sort = resolveAdminSort(filters.sort);
  const search = filters.search.trim();
  return {
    page,
    pageSize,
    ...(search ? { search } : {}),
    ...(filters.storeGuid ? { storeGuid: filters.storeGuid } : {}),
    ...(filters.roleGuid ? { roleGuid: filters.roleGuid } : {}),
    ...(statusFilterToIsActive(filters.status) !== undefined ? { isActive: statusFilterToIsActive(filters.status) } : {}),
    sortBy: sort.sortBy,
    sortDirection: sort.sortDirection,
  };
}

/** 计数查询（pageSize=1 只取 total）：忽略状态与排序，只带搜索 / 分店 / 角色，再单独指定 isActive。 */
export function buildAdminCountQuery(filters: AdminUserFilters, isActive?: boolean): IdentityUserQuery {
  const search = filters.search.trim();
  return {
    page: 1,
    pageSize: 1,
    ...(search ? { search } : {}),
    ...(filters.storeGuid ? { storeGuid: filters.storeGuid } : {}),
    ...(filters.roleGuid ? { roleGuid: filters.roleGuid } : {}),
    ...(isActive !== undefined ? { isActive } : {}),
  };
}

/** 弹层里「重置」的目标：保留搜索框里的关键词，只重置弹层管理的四项。 */
export function resetAdminSheetFilters(filters: AdminUserFilters): AdminUserFilters {
  return { ...DEFAULT_ADMIN_USER_FILTERS, search: filters.search };
}

/** 弹层管理的四项（分店 / 角色 / 状态 / 排序）是否都是默认值。 */
export function isAdminSheetDefault(filters: AdminUserFilters): boolean {
  return (
    filters.storeGuid === DEFAULT_ADMIN_USER_FILTERS.storeGuid &&
    filters.roleGuid === DEFAULT_ADMIN_USER_FILTERS.roleGuid &&
    filters.status === DEFAULT_ADMIN_USER_FILTERS.status &&
    filters.sort === DEFAULT_ADMIN_USER_FILTERS.sort
  );
}

/** 没有任何筛选（含搜索）时的列表才代表「全部分店 · N 人」的总人数。 */
export function isUnfilteredAdminList(filters: AdminUserFilters): boolean {
  return filters.search.trim() === "" && isAdminSheetDefault(filters);
}

/** 稳定的筛选键（作 react-query key 的一部分，筛选变化即重置分页）。 */
export function buildAdminFilterKey(filters: AdminUserFilters) {
  return [filters.search.trim(), filters.storeGuid, filters.roleGuid, filters.status, filters.sort] as const;
}

/** 累加所有已加载页；同一用户（排序键相同、分页期间数据变动）可能跨页重复出现，按 userGUID 去重并保持首次出现的顺序。 */
export function flattenUserPages(pages: readonly Pick<IdentityPagedResult<IdentityUser>, "items">[]): IdentityUser[] {
  const seen = new Set<string>();
  const result: IdentityUser[] = [];
  for (const page of pages) {
    for (const item of page.items) {
      if (seen.has(item.userGUID)) continue;
      seen.add(item.userGUID);
      result.push(item);
    }
  }
  return result;
}

/** 下一页页码；已是最后一页返回 undefined。优先信任服务端 totalPages，没有时按 total 推算。 */
export function resolveNextPage(last: Pick<IdentityPagedResult<unknown>, "page" | "pageSize" | "total" | "totalPages" | "items">): number | undefined {
  const totalPages = last.totalPages > 0 ? last.totalPages : Math.ceil(last.total / Math.max(1, last.pageSize));
  // 空页说明已经取尽，即使 total 估算偏大也不再翻页，避免无限请求。
  if (last.items.length === 0) return undefined;
  return last.page < totalPages ? last.page + 1 : undefined;
}
