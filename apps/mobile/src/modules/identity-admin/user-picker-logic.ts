// 「选人弹层」（权限 → 分配用户、角色 → 添加成员）的分店 / 角色 / 排序筛选：纯逻辑。
// 不依赖 React Native，可在 Node 下测试。服务端接口是现成的 GET /Users/optimized
// （roleGuid / storeGuid / sortBy / sortDirection），这里只负责把弹层里的筛选状态翻译成查询参数，
// 复用「人员管理」列表的同一套 buildAdminUserQuery，两处的排序口径不会分叉。
import {
  ADMIN_SORT_ORDER,
  buildAdminFilterKey,
  buildAdminUserQuery,
  type AdminSortKey,
  type AdminUserFilters,
} from "./admin-user-list-logic";
import type { IdentityUserQuery } from "./types";

/** 弹层每页条数：沿用原来的 50（添加成员弹层自己传 100）。 */
export const USER_PICKER_PAGE_SIZE = 50;

/** 搜索框停手多久才把关键字交给页面去查（页面不再随每个按键重绘，见 useBufferedSearchText）。 */
export const USER_PICKER_SEARCH_DELAY_MS = 300;

/** 弹层里可选的排序：与「人员管理」列表一致（最近登录 / 账号 / 姓名），默认最近登录。 */
export const USER_PICKER_SORT_ORDER: readonly AdminSortKey[] = ADMIN_SORT_ORDER;

export interface UserPickerFilters {
  /** 空串 = 全部分店。 */
  storeGuid: string;
  /** 空串 = 全部角色。 */
  roleGuid: string;
  sort: AdminSortKey;
}

export const DEFAULT_USER_PICKER_FILTERS: UserPickerFilters = { storeGuid: "", roleGuid: "", sort: "lastLoginAt" };

/** 二级选择页：主页 / 分店 / 角色 / 排序。 */
export type UserPickerPageKind = "main" | "store" | "role" | "sort";

function toAdminFilters(search: string, filters: UserPickerFilters): AdminUserFilters {
  // 选人弹层不区分启用 / 停用（停用账号照旧显示「已禁用」标签），所以状态固定为 all。
  return { search, storeGuid: filters.storeGuid, roleGuid: filters.roleGuid, status: "all", sort: filters.sort };
}

export function buildUserPickerQuery(search: string, filters: UserPickerFilters, page: number, pageSize = USER_PICKER_PAGE_SIZE): IdentityUserQuery {
  return buildAdminUserQuery(toAdminFilters(search, filters), page, pageSize);
}

/** react-query key 的一部分：搜索词 / 分店 / 角色 / 排序任何一个变化都从第一页重新开始。 */
export function buildUserPickerFilterKey(search: string, filters: UserPickerFilters): readonly string[] {
  return buildAdminFilterKey(toAdminFilters(search, filters));
}

/** 分店或角色是否在筛选（排序不算筛选：它只改顺序，不改结果集）。 */
export function hasActiveUserPickerFilters(filters: UserPickerFilters): boolean {
  return filters.storeGuid !== "" || filters.roleGuid !== "";
}

/**
 * 已勾选、但不在当前结果里的人数。
 * 只有「结果已经全部加载」时才有意义：还有下一页时，已选的人可能只是在没加载的页里，此时返回 0（不提示）。
 */
export function countSelectedOutsideResults(
  selectedGuids: readonly string[],
  visibleGuids: readonly string[],
  allLoaded: boolean,
): number {
  if (!allLoaded) return 0;
  const visible = new Set(visibleGuids);
  let outside = 0;
  for (const guid of selectedGuids) if (!visible.has(guid)) outside += 1;
  return outside;
}

/** 行内「角色 / 分店」摘要：只写第一项，其余记个数（「Auburn 等 3 家」「Cashier +1」由界面拼）。 */
export function summarizeNames(names: readonly string[] | undefined): { first: string; more: number } | null {
  const cleaned = (names ?? []).map((name) => name.trim()).filter(Boolean);
  if (cleaned.length === 0) return null;
  return { first: cleaned[0], more: cleaned.length - 1 };
}

/** 把文字按关键字拆成「命中 / 未命中」片段（不区分大小写），用来在行内高亮命中的部分。关键字里的正则符号按字面处理。 */
export function highlightParts(text: string, keyword: string): { text: string; match: boolean }[] {
  const needle = keyword.trim();
  if (!needle) return [{ text, match: false }];
  const escaped = needle.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  // 带捕获组的 split：命中的片段落在奇数下标。
  return text
    .split(new RegExp(`(${escaped})`, "i"))
    .map((part, index) => ({ text: part, match: index % 2 === 1 }))
    .filter((part) => part.text !== "");
}

/** 行内主名字与副名字：有姓名且与账号不同时，姓名为主、账号为副；否则只显示账号。 */
export function resolveUserPickerName(user: { username: string; fullName?: string }): { primary: string; secondary: string } {
  const fullName = user.fullName?.trim();
  if (fullName && fullName.toLocaleLowerCase() !== user.username.trim().toLocaleLowerCase()) {
    return { primary: fullName, secondary: user.username };
  }
  return { primary: user.username, secondary: "" };
}
