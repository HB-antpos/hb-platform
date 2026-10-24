/**
 * 统一「账号授权」页（分店 / 角色 / 直接权限 / 菜单 一页四段 + 一个吸底保存）的纯逻辑。
 *
 * 页面本身只负责渲染与副作用，凡是可以脱离 React 验证的规则都放在这里并有单元测试：
 * - 分段可见性、初始分段、分段上的计数；
 * - 店长收窄规则（只列自己管理的分店、没有「管理」档、只能动「店员」角色）；
 * - 角色行状态（店长由管理分店派生、高权限标签）；
 * - 直接权限的来源标签（继承 / 直接 / 未开放）与「收银权限不在这里」；
 * - 跨分段的改动汇总（吸底栏「N 项改动未保存」+ 明细）；
 * - 保存顺序与部分失败处理；
 * - 目标用户自取：路由参数仅作预填，服务端数据到位后以服务端为准。
 */
import { resolvePersonName, type ResolvedPersonName } from "@/modules/people/person-name";
import type { RoleMenuDefinition } from "@/modules/identity-admin/role-logic";
import { sortStoresByName } from "@/shared/utils/name-sort";
import {
  splitMobileMenuItems,
  userMenuPermissionState,
  type MobileMenuItem,
  type MobileMenuSection,
} from "@/modules/identity-admin/user-authorization-logic";
import {
  classifyUserAccessError,
  getAccessPermissionSelectionState,
  getAccessRoleSelectionState,
  getUserAccessStoreState,
  hasPrivilegedAccessRole,
  isStoreManagerRoleName,
} from "./access-management";
import type {
  AccessStoreOption,
  CopyPermissionDirection,
  DirectPermissionDraft,
  UserAccessErrorKind,
  UserAccessRole,
  UserAccessSectionMode,
  UserAccessStoreAssignment,
  UserAccessStoreState,
} from "./access-management-types";

// ───────────────────────── 分段 ─────────────────────────

export type AccessSectionKey = "stores" | "roles" | "permissions" | "menu";

/** 分段固定顺序；管理员才会多出最后的「菜单」。 */
export const ACCESS_SECTION_ORDER: readonly AccessSectionKey[] = [
  "stores",
  "roles",
  "permissions",
  "menu",
];

/** 解析路由参数 `section`；旧管理员授权页用 `mobile` 表示「移动端菜单」，统一映射成 `menu`。 */
export function parseAccessSectionParam(
  value: string | null | undefined,
): AccessSectionKey | null {
  const normalized = (value ?? "").trim().toLowerCase();
  if (normalized === "mobile") return "menu";
  return (ACCESS_SECTION_ORDER as readonly string[]).includes(normalized)
    ? (normalized as AccessSectionKey)
    : null;
}

/**
 * 路由参数 `copy`：进入页面后直接打开「复制权限」，返回要打开的方向，没有请求返回 null。
 * `copy=1`（详情页「复制权限给他人」入口）= 复制给他人；也接受显式的 `toOthers` / `fromColleague`。
 */
export function parseAccessCopyParam(
  value: string | null | undefined,
): CopyPermissionDirection | null {
  const normalized = (value ?? "").trim().toLowerCase();
  if (normalized === "1" || normalized === "true" || normalized === "toothers") return "toOthers";
  if (normalized === "fromcolleague") return "fromColleague";
  return null;
}

/**
 * 当前账号能看到哪些分段（按固定顺序）。
 * 「菜单」只给管理员，且依附在「直接权限」的数据上：权限分段不可见时菜单也不出现。
 */
export function listAccessSections(input: {
  storesMode: UserAccessSectionMode;
  rolesMode: UserAccessSectionMode;
  permissionsMode: UserAccessSectionMode;
  isAdmin: boolean;
}): AccessSectionKey[] {
  const sections: AccessSectionKey[] = [];
  if (input.storesMode !== "hidden") sections.push("stores");
  if (input.rolesMode !== "hidden") sections.push("roles");
  if (input.permissionsMode !== "hidden") sections.push("permissions");
  if (input.isAdmin && input.permissionsMode !== "hidden") sections.push("menu");
  return sections;
}

/** 选当前分段：期望的分段可见就用它，否则落到第一个可见分段；一个都没有返回 null。 */
export function pickAccessSection(
  available: readonly AccessSectionKey[],
  preferred: AccessSectionKey | null | undefined,
): AccessSectionKey | null {
  if (preferred && available.includes(preferred)) return preferred;
  return available[0] ?? null;
}

/**
 * 旧管理员授权路由（/user-admin/[userGuid]/access）改成重定向壳时要带到新路由的参数：
 * 原样透传所有字符串参数（旧版 App、推送、书签都可能带 userGuid / section 等），
 * 只把旧的 section=mobile 映射成 menu；无法识别的 section 丢掉，让新页面回落到第一个分段。
 */
export function buildAccessRedirectParams(
  params: Record<string, string | string[] | undefined>,
): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, raw] of Object.entries(params)) {
    const value = Array.isArray(raw) ? raw[0] : raw;
    if (value === undefined || value === "") continue;
    result[key] = value;
  }
  if (result.section !== undefined) {
    const section = parseAccessSectionParam(result.section);
    if (section) result.section = section;
    else delete result.section;
  }
  return result;
}

// ───────────────────────── 目标用户自取 ─────────────────────────

/** 旧版页面靠路由参数传入的目标信息；深链进入时可能全部缺失。 */
export interface AccessRouteHints {
  userName?: string;
  username?: string;
  targetStatus?: string;
  roleNames?: string[];
}

/** 服务端拉取到的目标（管理员接口 / 店员接口两种来源归一后的形状）。 */
export interface AccessTargetSnapshot {
  username: string;
  fullName?: string;
  givenName?: string;
  middleName?: string;
  familyName?: string;
  nickname?: string;
  isActive: boolean;
  roleNames: string[];
}

export interface AccessTarget {
  userGuid: string;
  username: string;
  name: ResolvedPersonName;
  /** 1 启用 / 0 停用，与 getUserAccessEligibility 的 targetStatus 同口径。 */
  status: number;
  roleNames: string[];
  /** true 表示这些值来自服务端；false 表示仍是路由参数预填。 */
  resolved: boolean;
}

/** 路由 `targetStatus` 只接受有限数字；缺失 / 非法返回 null，由调用方决定默认值。 */
export function parseAccessTargetStatus(value: string | null | undefined) {
  if (value === undefined || value === null || value.trim() === "") return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * 合并路由预填与服务端数据：服务端数据到位后一律以服务端为准，
 * 这样深链进入（参数缺失）时「受保护角色 / 停用账号」校验同样基于真实数据。
 */
export function mergeAccessTarget(
  userGuid: string,
  hints: AccessRouteHints,
  snapshot: AccessTargetSnapshot | null,
): AccessTarget {
  const username = snapshot?.username?.trim() || hints.username?.trim() || "";
  const fullName = snapshot?.fullName?.trim() || hints.userName?.trim() || undefined;
  const name = resolvePersonName({
    username,
    fullName,
    givenName: snapshot?.givenName,
    middleName: snapshot?.middleName,
    familyName: snapshot?.familyName,
    nickname: snapshot?.nickname,
  });
  return {
    userGuid,
    username,
    name,
    status: snapshot
      ? snapshot.isActive
        ? 1
        : 0
      : (parseAccessTargetStatus(hints.targetStatus) ?? 1),
    roleNames: snapshot ? [...snapshot.roleNames] : [...(hints.roleNames ?? [])],
    resolved: snapshot !== null,
  };
}

/** 管理员接口（/Users/guid/{guid}）的用户详情 → 目标快照。 */
export function accessTargetFromIdentityUser(user: {
  username: string;
  fullName?: string;
  givenName?: string;
  middleName?: string;
  familyName?: string;
  nickname?: string;
  isActive: boolean;
  roleNames: string[];
}): AccessTargetSnapshot {
  return {
    username: user.username,
    fullName: user.fullName,
    givenName: user.givenName,
    middleName: user.middleName,
    familyName: user.familyName,
    nickname: user.nickname,
    isActive: user.isActive,
    roleNames: user.roleNames,
  };
}

/** 店员接口（store-users）的详情 → 目标快照；status 为数字，1 表示启用。 */
export function accessTargetFromStoreUser(user: {
  username: string;
  fullName?: string;
  givenName?: string;
  middleName?: string;
  familyName?: string;
  nickname?: string;
  status: number;
  roleNames: string[];
}): AccessTargetSnapshot {
  return {
    username: user.username,
    fullName: user.fullName,
    givenName: user.givenName,
    middleName: user.middleName,
    familyName: user.familyName,
    nickname: user.nickname,
    isActive: user.status === 1,
    roleNames: user.roleNames,
  };
}

/**
 * 拉取目标失败时的分类：HTTP 状态优先；店员接口对「找不到 / 不在你范围内 / 高权限目标」
 * 返回的是 success:false 业务信封（无 HTTP 状态），按业务码区分「无权」与「已不可用」，
 * 其余无结论的异常才当作网络问题允许重试。
 */
export function classifyAccessTargetError(error: unknown): UserAccessErrorKind {
  const kind = classifyUserAccessError(error);
  if (kind !== "network") return kind;
  const candidate = error as { apiBusinessError?: boolean; code?: unknown } | null;
  if (candidate?.apiBusinessError === true) {
    const code = typeof candidate.code === "string" ? candidate.code.toUpperCase() : "";
    return code === "FORBIDDEN" ? "forbidden" : "notFound";
  }
  return "network";
}

// ───────────────────────── 分店 ─────────────────────────

/** 目录 + 已分配的分店按 GUID 去重（后者覆盖前者）并按名称排序。 */
export function mergeAccessStoreOptions(
  catalog: AccessStoreOption[] = [],
  assigned: AccessStoreOption[] = [],
) {
  const byGuid = new Map<string, AccessStoreOption>();
  [...catalog, ...assigned].forEach((store) => {
    if (store.storeGUID.trim()) byGuid.set(store.storeGUID, store);
  });
  // 统一走「按名字排序」比较器（名字为空退回编号，数字按数值，同名按编号）。
  return sortStoresByName(Array.from(byGuid.values()));
}

/**
 * 分店分段里列出的分店。
 * 管理员：全部分店目录 + 已分配；店长：只列自己管理（isPrimary）的分店，避免用全分店目录制造越权草稿。
 */
export function buildAccessStoreOptions(input: {
  isAdmin: boolean;
  catalog?: AccessStoreOption[];
  actorManaged?: AccessStoreOption[];
  assigned?: AccessStoreOption[];
  /** 操作者管理分店的编码（小写）。 */
  managedStoreCodes: ReadonlySet<string>;
}) {
  const stores = mergeAccessStoreOptions(
    input.isAdmin ? input.catalog : input.actorManaged,
    input.assigned,
  );
  if (input.isAdmin) return stores;
  return stores.filter((store) =>
    input.managedStoreCodes.has(store.storeCode.toLowerCase()),
  );
}

/** 搜索分店名称或编码（忽略大小写）。 */
export function filterAccessStores(stores: AccessStoreOption[], keyword: string) {
  const normalized = keyword.trim().toLowerCase();
  if (!normalized) return stores;
  return stores.filter((store) =>
    [store.storeName, store.storeCode].some((value) =>
      value.toLowerCase().includes(normalized),
    ),
  );
}

/** 分店授权筛选：全部，或按「管理 / 查看 / 未分配」。 */
export type AccessStoreFilter = "all" | UserAccessStoreState;

/** 各档分店数（按当前编辑中的分配计算，筛选芯片上的数字随改动实时变化）。 */
export function countAccessStoresByState(
  stores: AccessStoreOption[],
  assignments: UserAccessStoreAssignment[],
): Record<AccessStoreFilter, number> {
  const counts: Record<AccessStoreFilter, number> = { all: stores.length, manage: 0, view: 0, unassigned: 0 };
  for (const store of stores) {
    counts[getUserAccessStoreState(assignments, store.storeGUID)] += 1;
  }
  return counts;
}

/**
 * 选中某个筛选时，记下「此刻」属于该档的分店。
 * 之后在筛选结果里把某家分店改成别的档，这一行不会立刻消失（否则一改就跳走，用户找不到刚改的那行）；
 * 重新点筛选芯片才按最新分配重新取。选「全部」返回 null，表示不过滤。
 */
export function pinAccessStoresByState(
  stores: AccessStoreOption[],
  assignments: UserAccessStoreAssignment[],
  filter: AccessStoreFilter,
): ReadonlySet<string> | null {
  if (filter === "all") return null;
  return new Set(
    stores
      .filter((store) => getUserAccessStoreState(assignments, store.storeGUID) === filter)
      .map((store) => store.storeGUID),
  );
}

/** 先按关键字搜索，再按筛选时记下的分店集合过滤（null = 不过滤）。 */
export function applyAccessStoreFilter(
  stores: AccessStoreOption[],
  keyword: string,
  pinned: ReadonlySet<string> | null,
): AccessStoreOption[] {
  const matched = filterAccessStores(stores, keyword);
  return pinned ? matched.filter((store) => pinned.has(store.storeGUID)) : matched;
}

export interface AccessStoreStateOption {
  state: UserAccessStoreState;
  disabled: boolean;
}

/**
 * 一家分店右侧「未分配 | 查看 | 管理」三档里要显示哪几档、哪些可点。
 * - 只有管理员能授予「管理」，店长看不到这一档；
 * - 目标在这家分店已经是「管理」时（店长无权改动）仍要把这一档显示出来，并整行只读，
 *   避免店长在不知情时把别人的管理关系降级。
 */
export function resolveStoreStateOptions(input: {
  currentState: UserAccessStoreState;
  canGrantManage: boolean;
  editable: boolean;
}): AccessStoreStateOption[] {
  const showManage = input.canGrantManage || input.currentState === "manage";
  const rowLocked =
    !input.editable ||
    (input.currentState === "manage" && !input.canGrantManage);
  const options: AccessStoreStateOption[] = [
    { state: "unassigned", disabled: rowLocked },
    { state: "view", disabled: rowLocked },
  ];
  if (showManage) {
    options.push({
      state: "manage",
      disabled: rowLocked || !input.canGrantManage,
    });
  }
  return options;
}

/** 分段上的计数：目标在「可见分店」里已分配（查看 + 管理）的数量。 */
export function countAssignedStores(
  assignments: UserAccessStoreAssignment[],
  visibleStoreGuids: ReadonlySet<string>,
) {
  return assignments.filter((item) => visibleStoreGuids.has(item.storeGUID)).length;
}

// ───────────────────────── 角色 ─────────────────────────

export function mergeAccessRoleOptions(
  catalog: UserAccessRole[] = [],
  assigned: UserAccessRole[] = [],
) {
  const byGuid = new Map<string, UserAccessRole>();
  [...catalog, ...assigned].forEach((role) => {
    if (role.roleGUID.trim()) byGuid.set(role.roleGUID, role);
  });
  return Array.from(byGuid.values()).sort((left, right) =>
    left.roleName.localeCompare(right.roleName, "en-AU"),
  );
}

export interface AccessRoleRowState {
  selected: boolean;
  /** 店长角色：由「管理」分店派生，不能直接勾选。 */
  derived: boolean;
  /** 高权限角色（管理员 / 仓库经理等；店长行用「派生」标签，不算在内）。 */
  highPrivilege: boolean;
  /** 整行不可操作。 */
  disabled: boolean;
  /** 管理员点这一行时走 planStoreManagerGrant 引导（只对店长行有效）。 */
  canGuideManagerGrant: boolean;
}

/**
 * 角色行状态。
 * 店长行永远不能勾选；管理员（且分店分段可编辑）点它是「引导去把分店设为管理」，
 * 其他人点它保持锁定。非管理员碰到高权限角色一律不可操作。
 */
export function resolveAccessRoleRow(input: {
  role: UserAccessRole;
  selectedRoleGuids: string[];
  hasManagedStoreAssignment: boolean;
  isAdmin: boolean;
  rolesEditable: boolean;
  storesEditable: boolean;
  canGrantStoreManagement: boolean;
  busy: boolean;
}): AccessRoleRowState {
  const selection = getAccessRoleSelectionState({
    role: input.role,
    selectedRoleGuids: input.selectedRoleGuids,
    hasManagedStoreAssignment: input.hasManagedStoreAssignment,
  });
  const privileged = hasPrivilegedAccessRole([input.role.roleName]);
  const adminOnly = !input.isAdmin && privileged;
  const canGuideManagerGrant =
    selection.derived && input.canGrantStoreManagement && input.storesEditable;
  return {
    selected: selection.selected,
    derived: selection.derived,
    highPrivilege: !selection.derived && privileged,
    disabled:
      input.busy ||
      (selection.locked && !canGuideManagerGrant) ||
      adminOnly ||
      !input.rolesEditable,
    canGuideManagerGrant,
  };
}

/** 分段上的计数：可见角色里已选中的数量（店长角色按「是否有管理分店」计）。 */
export function countSelectedAccessRoles(
  roles: UserAccessRole[],
  selectedRoleGuids: string[],
  hasManagedStoreAssignment: boolean,
) {
  return roles.filter(
    (role) =>
      getAccessRoleSelectionState({
        role,
        selectedRoleGuids,
        hasManagedStoreAssignment,
      }).selected,
  ).length;
}

// ───────────────────────── 直接权限 ─────────────────────────

/** 收银（POS）相关权限码；它们统一放到「收银权限」页，不在授权页的直接权限里显示。 */
const POS_PERMISSION_PATTERN = /^(Permissions\.)?PosTerminal\./i;

export function isPosTerminalPermissionCode(code: string) {
  return POS_PERMISSION_PATTERN.test(code.trim());
}

/** 去掉收银权限后的分类目录；空分类整体去掉。草稿里的收银权限码保持不动（保存时原样带上）。 */
export function excludePosPermissionCategories<
  C extends { permissions: { name: string }[] },
>(categories: C[]): C[] {
  return categories
    .map((category) => ({
      ...category,
      permissions: category.permissions.filter(
        (permission) => !isPosTerminalPermissionCode(permission.name),
      ),
    }))
    .filter((category) => category.permissions.length > 0);
}

/**
 * 与 excludePosPermissionCategories 互补：只留收银（POS）权限，供授权页底部「收银权限 · 账号默认值」分组使用。
 * 旧授权页允许直接授予账号级 PosTerminal.* 默认值（对所有分店生效），这个能力不能因为改版丢掉；
 * 按分店覆盖仍然去「收银权限」页。
 */
export function pickPosPermissionCategories<
  C extends { permissions: { name: string }[] },
>(categories: C[]): C[] {
  return categories
    .map((category) => ({
      ...category,
      permissions: category.permissions.filter((permission) =>
        isPosTerminalPermissionCode(permission.name),
      ),
    }))
    .filter((category) => category.permissions.length > 0);
}

/** 按关键词过滤权限；`textOf` 返回该权限可被搜索的文字（名称、说明、权限码、分类名等）。 */
export function filterPermissionCategories<C extends { permissions: { name: string }[] }>(
  categories: C[],
  keyword: string,
  textOf: (permission: C["permissions"][number], category: C) => string,
): C[] {
  const normalized = keyword.trim().toLowerCase();
  if (!normalized) return categories;
  return categories
    .map((category) => ({
      ...category,
      permissions: category.permissions.filter((permission) =>
        textOf(permission, category).toLowerCase().includes(normalized),
      ),
    }))
    .filter((category) => category.permissions.length > 0);
}

export type AccessPermissionSourceKind =
  | "implicit"
  | "inherited"
  | "direct"
  | "none";

export interface AccessPermissionRowState {
  kind: AccessPermissionSourceKind;
  /** 开关是否打开（有效）。 */
  checked: boolean;
  /** 开关置灰：继承来的、管理员默认拥有的都不能在这里改。 */
  locked: boolean;
  /** 继承自哪些角色（kind=inherited 时有值）。 */
  roleNames: string[];
}

/** 一条权限的来源标签与开关状态：继承自角色 / 直接授权 / 未开放 / 管理员默认拥有。 */
export function resolveAccessPermissionRow(input: {
  draft: DirectPermissionDraft;
  code: string;
  implicitAll: boolean;
  inheritedRoleNames?: string[];
}): AccessPermissionRowState {
  if (input.implicitAll) {
    return { kind: "implicit", checked: true, locked: true, roleNames: [] };
  }
  const state = getAccessPermissionSelectionState(input.draft, input.code);
  if (state.inherited) {
    return {
      kind: "inherited",
      checked: true,
      locked: true,
      roleNames: input.inheritedRoleNames ?? [],
    };
  }
  if (state.direct) {
    return { kind: "direct", checked: true, locked: false, roleNames: [] };
  }
  return { kind: "none", checked: false, locked: false, roleNames: [] };
}

/** 分类标题里的「已开放 / 总数」，例如「报表 · 6 / 9」。 */
export function countEnabledPermissions(
  permissions: { name: string }[],
  rowOf: (code: string) => AccessPermissionRowState,
) {
  return {
    enabled: permissions.filter((permission) => rowOf(permission.name).checked).length,
    total: permissions.length,
  };
}

function uniqueCodes(codes: string[]) {
  return Array.from(new Set(codes.map((code) => code.trim()).filter(Boolean)));
}

export interface AccessPermissionCounts {
  /** 有效权限数 = 角色继承 + 直接授权（管理员默认拥有时为目录里的全部）。 */
  effective: number;
  inherited: number;
  direct: number;
}

/**
 * 顶部「有效 / 角色继承 / 直接授权」三个数字。收银权限不在这里，三个数字都不含收银权限码；
 * 直接授权只数「不是继承来的」那部分，保证 有效 = 继承 + 直接。
 */
export function summarizeAccessPermissionCounts(input: {
  draft: DirectPermissionDraft;
  implicitAll: boolean;
  catalogCodes: string[];
}): AccessPermissionCounts {
  const withoutPos = (codes: string[]) =>
    uniqueCodes(codes).filter((code) => !isPosTerminalPermissionCode(code));
  const inherited = withoutPos(input.draft.inheritedPermissionCodes);
  const inheritedSet = new Set(inherited);
  const direct = withoutPos(input.draft.selectedCodes).filter(
    (code) => !inheritedSet.has(code),
  );
  return {
    effective: input.implicitAll
      ? withoutPos(input.catalogCodes).length
      : inherited.length + direct.length,
    inherited: inherited.length,
    direct: direct.length,
  };
}

// ───────────────────────── 改动汇总 ─────────────────────────

export type AccessChangeItem =
  | {
      section: "stores";
      storeGuid: string;
      from: UserAccessStoreState;
      to: UserAccessStoreState;
    }
  | {
      section: "roles";
      roleGuid: string;
      roleName: string;
      change: "added" | "removed";
    }
  | { section: "permissions"; code: string; change: "added" | "removed" };

export interface AccessChangeSummary {
  /** 吸底栏「N 项改动未保存」里的 N；每个分店 / 角色 / 权限码的变化各算一项。 */
  total: number;
  items: AccessChangeItem[];
  counts: { stores: number; roles: number; permissions: number };
  /** 因「管理」分店变化而自动获得 / 移除店长角色（派生，不计入 total）。 */
  managerDerived: "gain" | "lose" | null;
}

export interface AccessChangeInput {
  storeAssignments: UserAccessStoreAssignment[];
  baselineStoreAssignments: UserAccessStoreAssignment[];
  /** 只统计这些分店的变化；不传表示全部。 */
  visibleStoreGuids?: ReadonlySet<string> | null;
  roleOptions: UserAccessRole[];
  selectedRoleGuids: string[];
  baselineRoleGuids: string[];
  permissionDraft: DirectPermissionDraft | null;
}

export function buildAccessChangeSummary(input: AccessChangeInput): AccessChangeSummary {
  const items: AccessChangeItem[] = [];

  // 分店：逐家比较基线与草稿的三档状态。
  const storeGuids = Array.from(
    new Set([
      ...input.baselineStoreAssignments.map((item) => item.storeGUID),
      ...input.storeAssignments.map((item) => item.storeGUID),
    ]),
  );
  let storeCount = 0;
  for (const storeGuid of storeGuids) {
    if (input.visibleStoreGuids && !input.visibleStoreGuids.has(storeGuid)) continue;
    const from = getUserAccessStoreState(input.baselineStoreAssignments, storeGuid);
    const to = getUserAccessStoreState(input.storeAssignments, storeGuid);
    if (from === to) continue;
    items.push({ section: "stores", storeGuid, from, to });
    storeCount += 1;
  }

  // 角色：店长角色由分店派生，不在角色草稿里，所以不会出现在这里。
  const selected = new Set(input.selectedRoleGuids);
  const baseline = new Set(input.baselineRoleGuids);
  let roleCount = 0;
  for (const role of input.roleOptions) {
    if (isStoreManagerRoleName(role.roleName)) continue;
    const now = selected.has(role.roleGUID);
    const before = baseline.has(role.roleGUID);
    if (now === before) continue;
    items.push({
      section: "roles",
      roleGuid: role.roleGUID,
      roleName: role.roleName,
      change: now ? "added" : "removed",
    });
    roleCount += 1;
  }

  // 直接权限（含从「菜单」分段改的：菜单本质上就是在改同一份直接权限草稿）。
  let permissionCount = 0;
  if (input.permissionDraft) {
    const nowCodes = new Set(uniqueCodes(input.permissionDraft.selectedCodes));
    const beforeCodes = new Set(uniqueCodes(input.permissionDraft.baselineCodes));
    nowCodes.forEach((code) => {
      if (beforeCodes.has(code)) return;
      items.push({ section: "permissions", code, change: "added" });
      permissionCount += 1;
    });
    beforeCodes.forEach((code) => {
      if (nowCodes.has(code)) return;
      items.push({ section: "permissions", code, change: "removed" });
      permissionCount += 1;
    });
  }

  const hadManaged = input.baselineStoreAssignments.some((item) => item.isPrimary);
  const hasManaged = input.storeAssignments.some((item) => item.isPrimary);

  return {
    total: items.length,
    items,
    counts: { stores: storeCount, roles: roleCount, permissions: permissionCount },
    managerDerived:
      hadManaged === hasManaged ? null : hasManaged ? "gain" : "lose",
  };
}

export interface AccessChangeFormatters {
  store: (item: Extract<AccessChangeItem, { section: "stores" }>) => string;
  role: (item: Extract<AccessChangeItem, { section: "roles" }>) => string;
  permission: (item: Extract<AccessChangeItem, { section: "permissions" }>) => string;
}

/** 把前 `limit` 项改动格式化成一行明细的各段；`hiddenCount` 是没展示的项数。 */
export function describeAccessChanges(
  items: AccessChangeItem[],
  formatters: AccessChangeFormatters,
  limit = 2,
) {
  const parts = items.slice(0, Math.max(0, limit)).map((item) => {
    if (item.section === "stores") return formatters.store(item);
    if (item.section === "roles") return formatters.role(item);
    return formatters.permission(item);
  });
  return { parts, hiddenCount: Math.max(0, items.length - parts.length) };
}

// ───────────────────────── 保存顺序与部分失败 ─────────────────────────

/**
 * 保存顺序固定：分店 → 角色 → 直接权限。
 * 分店先保存，因为「管理」分店会派生店长角色并改变继承来的权限；直接权限放最后。
 */
export const ACCESS_SAVE_ORDER = ["stores", "roles", "permissions"] as const;
export type AccessSaveStepKey = (typeof ACCESS_SAVE_ORDER)[number];

/** 只挑有改动的步骤，并按固定顺序排好。 */
export function planAccessSaveSteps(
  dirty: Partial<Record<AccessSaveStepKey, boolean>>,
): AccessSaveStepKey[] {
  return ACCESS_SAVE_ORDER.filter((key) => dirty[key] === true);
}

export interface AccessSaveStep {
  key: AccessSaveStepKey;
  run: () => Promise<void>;
}

export interface AccessSaveOutcome {
  /** 已经成功写入的步骤。 */
  saved: AccessSaveStepKey[];
  /** 失败的那一步；失败后后面的步骤不再执行。 */
  failed: { key: AccessSaveStepKey; error: unknown } | null;
  /** 因前面失败或会话变化而没执行的步骤。 */
  notRun: AccessSaveStepKey[];
  /** 执行中途登录身份变化（登出 / 切账号），余下步骤被放弃。 */
  aborted: boolean;
}

/**
 * 按顺序提交各分段。任何一步失败立即停下（不继续写后面的），
 * 把「哪些已保存 / 哪一步失败 / 哪些没执行」原样交给调用方展示；绝不吞错。
 */
export async function runAccessSaveSteps(
  steps: AccessSaveStep[],
  options: { isCurrent?: () => boolean } = {},
): Promise<AccessSaveOutcome> {
  const outcome: AccessSaveOutcome = {
    saved: [],
    failed: null,
    notRun: [],
    aborted: false,
  };
  for (let index = 0; index < steps.length; index += 1) {
    const step = steps[index];
    if (options.isCurrent && !options.isCurrent()) {
      outcome.aborted = true;
      outcome.notRun = steps.slice(index).map((item) => item.key);
      return outcome;
    }
    try {
      await step.run();
      outcome.saved.push(step.key);
    } catch (error) {
      outcome.failed = { key: step.key, error };
      outcome.notRun = steps.slice(index + 1).map((item) => item.key);
      return outcome;
    }
  }
  return outcome;
}

export interface AccessSaveReportText {
  sectionLabel: (key: AccessSaveStepKey) => string;
  /** 把多个分段名拼成一句（中文用顿号）。 */
  join: (items: string[]) => string;
  savedLine: (sections: string) => string;
  failedLine: (section: string, reason: string) => string;
  notRunLine: (sections: string) => string;
  retryHint: string;
  /** 把失败原因转成给用户看的一句话。 */
  reasonOf: (error: unknown) => string;
}

/**
 * 部分失败 / 全部失败时告诉用户的几行话：哪些已保存、哪一步失败（含原因）、哪些没执行，
 * 最后说明未保存的改动仍保留、可重试。全部成功或身份中止时不需要这些行，返回空数组。
 */
export function buildAccessSaveReportLines(
  outcome: AccessSaveOutcome,
  text: AccessSaveReportText,
): string[] {
  if (!outcome.failed) return [];
  const lines: string[] = [];
  if (outcome.saved.length > 0) {
    lines.push(text.savedLine(text.join(outcome.saved.map(text.sectionLabel))));
  }
  lines.push(
    text.failedLine(text.sectionLabel(outcome.failed.key), text.reasonOf(outcome.failed.error)),
  );
  if (outcome.notRun.length > 0) {
    lines.push(text.notRunLine(text.join(outcome.notRun.map(text.sectionLabel))));
  }
  lines.push(text.retryHint);
  return lines;
}

export type AccessSaveVerdict =
  "nothing" | "success" | "partial" | "failed" | "aborted";

/** success=全部成功；partial=有成功也有失败；failed=第一步就失败；aborted=身份变化被放弃。 */
export function classifyAccessSaveOutcome(outcome: AccessSaveOutcome): AccessSaveVerdict {
  if (outcome.aborted) return "aborted";
  if (outcome.failed) return outcome.saved.length > 0 ? "partial" : "failed";
  return outcome.saved.length > 0 ? "success" : "nothing";
}

// ───────────────────────── 菜单（管理员）─────────────────────────

export interface AccessMenuItem extends MobileMenuItem {
  /** 固定入口（始终可见，不能改）。 */
  fixed: boolean;
  /** 仅管理员可见的入口。 */
  requireAdmin: boolean;
  inherited: boolean;
  direct: boolean;
}

export interface AccessMenuModel {
  items: AccessMenuItem[];
  sections: Record<MobileMenuSection, AccessMenuItem[]>;
  /** 分段上的计数：已开放的菜单数。 */
  openCount: number;
}

/**
 * 「菜单」分段（原管理员授权页的「移动端菜单」，行为完全保留）：
 * 只取移动端菜单定义（按 key 去重），可见性 / 是否锁定都由目标当前的直接权限草稿推算，
 * 开关改的就是同一份直接权限草稿，所以它和「直接权限」共用同一个保存步骤。
 */
export function buildAccessMenuModel(input: {
  definitions: RoleMenuDefinition[];
  draft: DirectPermissionDraft | null;
  assignableCodes: string[];
  state: { isSuperAdmin: boolean; implicitAllPermissions: boolean } | null;
}): AccessMenuModel {
  const seen = new Set<string>();
  const definitions = input.definitions.filter((definition) => {
    if (definition.platform !== "mobile" || seen.has(definition.key)) return false;
    seen.add(definition.key);
    return true;
  });
  if (!input.draft || !input.state) {
    return { items: [], sections: splitMobileMenuItems([]), openCount: 0 };
  }
  const items = definitions.map((definition): AccessMenuItem => {
    const state = userMenuPermissionState(
      definition,
      input.draft!,
      input.assignableCodes,
      input.state!,
    );
    return {
      key: definition.key,
      title: definition.title,
      permissionCodes: definition.permissionCodes,
      visible: state.visible,
      locked: state.locked,
      fixed: definition.fixed === true,
      requireAdmin: definition.requireAdmin === true,
      inherited: state.inherited,
      direct: state.direct,
    };
  });
  return {
    items,
    sections: splitMobileMenuItems(items),
    openCount: items.filter((item) => item.visible).length,
  };
}
