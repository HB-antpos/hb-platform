// 「人员管理」店长版列表（StoreStaffList）的纯逻辑：搜索、状态筛选与计数、「我」置顶分组、各分店人数。
// 从原 app/(shell)/users/index.tsx 的 filteredUsers / statusCounts / listRows 抽出，业务规则不变，
// 新增：「不能登录」筛选（loginEnabled === false）与昵称 / 姓名各段搜索。
// 不依赖 React Native，可在 Node 下测试。
import { resolveRowPosition, type ListRowPosition } from "../people/list-position";
import { buildPersonSearchText, resolvePersonName } from "../people/person-name";
import { hasDeliverableEmail } from "./staff-email-username";
import type { StoreUserListItem } from "./types";

export type StaffStatusFilter = "all" | "active" | "disabled" | "pendingFirstLogin" | "cannotLogin" | "noEmail";

/** 筛选芯片顺序即界面顺序：全部 / 启用 / 停用 / 待首次登录 / 不能登录 / 无邮箱。 */
export const STAFF_STATUS_FILTER_ORDER: readonly StaffStatusFilter[] = [
  "all",
  "active",
  "disabled",
  "pendingFirstLogin",
  "cannotLogin",
  "noEmail",
];

export type StaffStatusCounts = Record<StaffStatusFilter, number>;

/** 单个员工是否满足某个状态筛选；计数与筛选共用这一个判断，保证芯片上的数字和点开后的人数一致。 */
export function matchesStaffStatusFilter(user: StoreUserListItem, filter: StaffStatusFilter): boolean {
  switch (filter) {
    case "active":
      return user.status === 1;
    case "disabled":
      return user.status !== 1;
    case "pendingFirstLogin":
      return user.mustChangePassword === true;
    case "cannotLogin":
      // 后端缺省（旧后端）视为允许登录，只有明确 false 才算「不能登录」。
      return user.loginEnabled === false;
    case "noEmail":
      // 没有可用邮箱的账号收不到设置密码验证码，筛出来方便店长逐个补邮箱。
      return !hasDeliverableEmail(user.email);
    default:
      return true;
  }
}

export function countStaffStatuses(users: readonly StoreUserListItem[]): StaffStatusCounts {
  const counts = {} as StaffStatusCounts;
  for (const filter of STAFF_STATUS_FILTER_ORDER) {
    counts[filter] = users.filter((user) => matchesStaffStatusFilter(user, filter)).length;
  }
  return counts;
}

function stripPhone(value: string) {
  return value.replace(/[\s()+-]/g, "");
}

/**
 * 客户端搜索：姓名各段 / 昵称 / 账号（buildPersonSearchText）+ 邮箱 + 电话。
 * 关键词按空白切成多个词，必须全部命中（「chen emily」也能找到 Emily Chen）；
 * 纯数字关键词另外忽略电话里的空格与连字符再比一次。
 */
export function matchesStaffKeyword(user: StoreUserListItem, keyword: string): boolean {
  const needle = keyword.trim().toLowerCase();
  if (!needle) return true;
  const haystack = `${buildPersonSearchText(user)} ${(user.email ?? "").toLowerCase()} ${(user.phone ?? "").toLowerCase()}`;
  const tokens = needle.split(/\s+/).filter(Boolean);
  if (tokens.every((token) => haystack.includes(token))) return true;
  const digits = stripPhone(needle);
  return /^\d+$/.test(digits) && stripPhone(user.phone ?? "").includes(digits);
}

/** 按姓名排序（昵称不参与排序）；同名再按账号，保证顺序稳定。 */
export function compareStaffByName(left: StoreUserListItem, right: StoreUserListItem): number {
  const byName = resolvePersonName(left).primary.localeCompare(resolvePersonName(right).primary);
  return byName !== 0 ? byName : left.username.localeCompare(right.username);
}

export function filterStoreStaff(
  users: readonly StoreUserListItem[],
  { keyword = "", statusFilter = "all" }: { keyword?: string; statusFilter?: StaffStatusFilter },
): StoreUserListItem[] {
  return users
    .filter((user) => matchesStaffStatusFilter(user, statusFilter) && matchesStaffKeyword(user, keyword))
    .sort(compareStaffByName);
}

// ───────────────────────── 分组：「我」置顶 + 店员 ─────────────────────────

/** 卡片分段位置：同一组连续的行共用一张圆角卡片，只有首行有上圆角、末行有下圆角。 */
export type StaffRowPosition = ListRowPosition;
export { resolveRowPosition };

export type StaffListRow =
  | { kind: "header"; key: string; label: string; hint?: string }
  | { kind: "user"; key: string; user: StoreUserListItem; isSelf: boolean; position: StaffRowPosition };

export interface StaffListLabels {
  me: string;
  /** 「店员 16」之类，由调用方带上人数。 */
  staffSection: (count: number) => string;
  sortedByName?: string;
}

/**
 * 「我」分组置顶：后端只返回本店店员 + 登录店长本人；本人行的编辑 / 停用等接口会被拒绝，
 * 所以放在最上面单独成组，不提供任何操作。
 */
export function buildStaffListRows(
  users: readonly StoreUserListItem[],
  actorGuid: string,
  labels: StaffListLabels,
): StaffListRow[] {
  const actorKey = actorGuid.trim().toLowerCase();
  const isSelf = (user: StoreUserListItem) => Boolean(actorKey) && user.userGUID.trim().toLowerCase() === actorKey;
  const selfUsers = users.filter(isSelf);
  const staffUsers = users.filter((user) => !isSelf(user));
  const rows: StaffListRow[] = [];
  if (selfUsers.length) {
    rows.push({ kind: "header", key: "header-me", label: labels.me });
    selfUsers.forEach((user, index) =>
      rows.push({ kind: "user", key: user.userGUID, user, isSelf: true, position: resolveRowPosition(index, selfUsers.length) }),
    );
  }
  if (staffUsers.length) {
    rows.push({
      kind: "header",
      key: "header-staff",
      label: labels.staffSection(staffUsers.length),
      ...(labels.sortedByName ? { hint: labels.sortedByName } : {}),
    });
    staffUsers.forEach((user, index) =>
      rows.push({ kind: "user", key: user.userGUID, user, isSelf: false, position: resolveRowPosition(index, staffUsers.length) }),
    );
  }
  return rows;
}

// ───────────────────────── 分店人数 / 副标题 ─────────────────────────

/** 「全部已分配分店」数据里按所属分店统计人数（同一人在同一分店只算一次）；total 按人去重。 */
export function countStaffByStore(users: readonly StoreUserListItem[]): { total: number; byStore: Record<string, number> } {
  const everyone = new Set<string>();
  const perStore = new Map<string, Set<string>>();
  for (const user of users) {
    everyone.add(user.userGUID);
    const storeCode = user.storeCode?.trim();
    if (!storeCode) continue;
    const members = perStore.get(storeCode) ?? new Set<string>();
    members.add(user.userGUID);
    perStore.set(storeCode, members);
  }
  const byStore: Record<string, number> = {};
  perStore.forEach((members, storeCode) => {
    byStore[storeCode] = members.size;
  });
  return { total: everyone.size, byStore };
}

export type StoreListSubtitleKind = "noStore" | "allStores" | "manageable" | "viewOnly";

/**
 * 标题栏副标题的语义：未选分店（分店列表还没加载）/ 全部已分配分店（只能查看）/ 选中的分店 + 你可管理 / 仅查看。
 * `canManageSelectedStore` 沿用原来的「可管理」判断：只要有新增 / 编辑 / 重置密码任一权限且该分店可管理。
 */
export function resolveStoreListSubtitleKind(input: {
  managedStoreCode: string | null;
  hasSelectedStore: boolean;
  canManageSelectedStore: boolean;
}): StoreListSubtitleKind {
  if (!input.managedStoreCode) return "allStores";
  if (!input.hasSelectedStore) return "noStore";
  return input.canManageSelectedStore ? "manageable" : "viewOnly";
}
