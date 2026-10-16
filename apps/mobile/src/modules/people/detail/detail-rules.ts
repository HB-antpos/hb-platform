import {
  getUserAccessEligibility,
  hasPrivilegedAccessRole,
  isStoreStaffRoleName,
} from "@/modules/users/access-management";
import type { UserAccessEligibility } from "@/modules/users/access-management-types";
import { canManageStaffBarcode } from "@/modules/users/staff-barcode/eligibility";
import { cleanNamePart, type PersonNameSource } from "@/modules/people/person-name";
import { PERMISSIONS } from "@/shared/utils/access";

/**
 * 统一人员详情的显示规则（纯函数，不依赖 React / 网络）。
 *
 * 设计原则（A2）：没有权限的操作直接不显示，不再灰着占位；本人详情不提供停用、授权等操作；
 * 受保护目标（管理员 / 店长 / 仓库管理员，对非管理员操作者）一律只读；设备模式不开放授权类入口。
 * 页面只负责把这些结果渲染出来，所有「能不能做」都在这里算。
 */

/** store = 店长版（store-users 接口）；admin = 管理员版（identity-admin 接口）。 */
export type DetailVariant = "store" | "admin";

export function resolveDetailVariant(input: {
  /** useIdentitySession().allowed：account 会话 + Users.View + 非 iOS 审核离线守卫。 */
  identityAllowed: boolean;
  isDeviceMode: boolean;
  isIosReview: boolean;
  /** 店长（非管理员、非仓库经理）：即使持有 Users.View 也只能用店长版（与列表同一条规则）。 */
  isStoreLevelManager?: boolean;
}): DetailVariant {
  // 管理员接口在设备模式与 iOS 审核模式下一律屏蔽，必须回落到店长版（走 store-users / 审核模拟接口）。
  // 店长永远是店长版：列表只给他看自己管理分店里的店员，详情也不能走全局用户接口去读别的角色。
  return input.identityAllowed && !input.isStoreLevelManager && !input.isDeviceMode && !input.isIosReview
    ? "admin"
    : "store";
}

export interface DetailActorFacts {
  userGuid?: string | null;
  roleNames: readonly string[];
  isAdmin: boolean;
  isStoreManager: boolean;
  /** 店长级管理者：只能在自己的分店范围内操作，不能动本人和更高权限账号。 */
  isStoreLevelManager: boolean;
  hasPermission: (permission: string) => boolean;
}

export interface DetailTargetFacts {
  userGuid: string;
  active: boolean;
  roleNames: readonly string[];
}

export interface DetailScopeFacts {
  authenticated: boolean;
  isDeviceMode: boolean;
  /** 目标所在分店是否在操作者可管理分店内（管理员恒为 true；店长 = isPrimary 分店；设备模式 = 绑定分店）。 */
  storeManageable: boolean;
  /** 个人码专用：该分店既可管理又已启用 POS。 */
  barcodeStoreAllowed: boolean;
  hasManageableStores: boolean;
  /** 能否确定一个分店来发「设置密码邮件」（店长版 = 路由 storeCode；管理员版 = 目标有关联分店）。 */
  hasStoreContext: boolean;
}

export interface DetailPermissionInput {
  variant: DetailVariant;
  actor: DetailActorFacts;
  target: DetailTargetFacts;
  scope: DetailScopeFacts;
}

export interface DetailPermissions {
  variant: DetailVariant;
  isSelf: boolean;
  /** 目标是管理员 / 店长 / 仓库管理员等受保护账号，且操作者不是管理员：一律只读。 */
  isProtectedTarget: boolean;
  canEdit: boolean;
  /** 停用 / 启用账号。 */
  canToggleStatus: boolean;
  canResetPassword: boolean;
  /** 重置密码弹层里可用的方式：邮件（店长与管理员）/ 直接设置新密码（仅管理员版）。 */
  passwordModes: { email: boolean; direct: boolean };
  canPrintBarcode: boolean;
  canOpenAccess: boolean;
  canOpenPos: boolean;
  canCopyPermissions: boolean;
  canViewLoginRecords: boolean;
  access: UserAccessEligibility;
}

function normalizeGuid(value: string | null | undefined) {
  return (value ?? "").trim().toLowerCase();
}

export function resolveDetailPermissions(input: DetailPermissionInput): DetailPermissions {
  const { variant, actor, target, scope } = input;
  const isSelf = normalizeGuid(actor.userGuid) !== "" && normalizeGuid(actor.userGuid) === normalizeGuid(target.userGuid);
  const targetRoles = [...target.roleNames];
  const isProtectedTarget = !actor.isAdmin && hasPrivilegedAccessRole(targetRoles);

  // 客户端 hasPermission 不认 Users.ViewStoreStaff ⊂ Users.View 这类别名，
  // 所以必须显式写「全局码 OR 店员码」，不能收敛成单个码（契约测试在锁这些写法）。
  const canEditUsers = actor.isAdmin
    || actor.hasPermission(PERMISSIONS.Users.Edit)
    || actor.hasPermission(PERMISSIONS.Users.EditStoreStaff);
  const canResetPasswords = actor.isAdmin
    || actor.hasPermission(PERMISSIONS.Users.ResetPassword)
    || actor.hasPermission(PERMISSIONS.Users.ResetStoreStaffPassword);

  // 管理员版走全局接口，只认全局码；店长级管理者额外不能动本人（后端 SELF_PROFILE_FIELDS_DENIED 同样拒绝）。
  const adminScopeOk = !isProtectedTarget && (!actor.isStoreLevelManager || !isSelf);
  const canEdit = variant === "store"
    ? canEditUsers && scope.storeManageable && !isSelf && !isProtectedTarget
    : actor.hasPermission(PERMISSIONS.Users.Edit) && adminScopeOk;
  // 本人永远不能停用自己（接口会拒绝，也避免把自己锁在门外）。
  const canToggleStatus = canEdit && !isSelf;

  const emailMode = canResetPasswords
    && scope.hasStoreContext
    && !isSelf
    && !isProtectedTarget
    && (variant === "store"
      ? scope.storeManageable
      // 管理员版：设置密码邮件走店员接口，只对店员账号有意义。
      : targetRoles.some((roleName) => isStoreStaffRoleName(roleName)));
  const directMode = variant === "admin"
    && actor.hasPermission(PERMISSIONS.Users.ResetPassword)
    && adminScopeOk;
  const passwordModes = { email: emailMode, direct: directMode };

  const access = getUserAccessEligibility({
    isDeviceMode: scope.isDeviceMode,
    isAdmin: actor.isAdmin,
    isStoreManager: actor.isStoreManager,
    canManageStores: actor.hasPermission(PERMISSIONS.Users.ManageStores),
    canManageRoles: actor.hasPermission(PERMISSIONS.Users.ManageRoles),
    canManagePos: actor.hasPermission(PERMISSIONS.Users.ManagePos),
    currentUserGuid: actor.userGuid,
    targetUserGuid: target.userGuid,
    targetStatus: target.active ? 1 : 0,
    targetRoleNames: targetRoles,
    hasManageableStores: actor.isAdmin || scope.hasManageableStores,
  });
  // 本人详情不提供授权类操作（getUserAccessEligibility 对管理员本人是放行的，这里统一收紧）。
  const canOpenAccess = !isSelf && access.canOpen;

  const canPrintBarcode = !isSelf && canManageStaffBarcode({
    authenticated: scope.authenticated,
    deviceOnly: scope.isDeviceMode,
    canEditUsers,
    canManagePosStore: scope.barcodeStoreAllowed,
    actorGuid: actor.userGuid ?? "",
    actorRoles: [...actor.roleNames],
    targetGuid: target.userGuid,
    targetStatus: target.active ? 1 : 0,
    targetRoles,
  });

  return {
    variant,
    isSelf,
    isProtectedTarget,
    canEdit,
    canToggleStatus,
    canResetPassword: passwordModes.email || passwordModes.direct,
    passwordModes,
    canPrintBarcode,
    canOpenAccess,
    canOpenPos: !isSelf && access.canManagePos,
    canCopyPermissions: canOpenAccess && access.permissionsMode === "editable",
    canViewLoginRecords: variant === "admin" && actor.hasPermission(PERMISSIONS.Users.View),
    access,
  };
}

// ───────────────────────── 头部与快捷操作 ─────────────────────────

export type StatusIntent = "disable" | "enable";

/** 停用 / 启用：停用是危险操作（红色实心确认），启用走常规确认。 */
export function resolveStatusAction(active: boolean): { intent: StatusIntent; nextActive: boolean; danger: boolean } {
  return active
    ? { intent: "disable", nextActive: false, danger: true }
    : { intent: "enable", nextActive: true, danger: false };
}

export type QuickActionKey = "edit" | "password" | "barcode" | "status";

export interface QuickAction {
  key: QuickActionKey;
  /** 仅 status 有意义：当前点下去是停用还是启用。 */
  intent?: StatusIntent;
  danger?: boolean;
}

export type MoreItemKey = "barcode" | "access" | "pos" | "copyPermissions" | "loginRecords";

export interface DetailLayout {
  /** 店长版头部卡片里的快捷操作（按顺序，没权限的不出现）。 */
  quickActions: QuickAction[];
  /** 管理员版右上角 ✎ 编辑。 */
  headerEdit: boolean;
  /** 「更多」弹层里的普通项（按顺序）。 */
  moreItems: MoreItemKey[];
  /** 「更多」弹层底部的红色停用 / 启用项。 */
  moreStatus: StatusIntent | null;
  /** 右上角 ⋮ 是否显示：弹层没有任何可用项时整个隐藏。 */
  showMore: boolean;
  /** 管理员版页面底部的整行停用 / 启用按钮。 */
  footerStatus: StatusIntent | null;
}

export function buildDetailLayout(permissions: DetailPermissions, active: boolean): DetailLayout {
  const status = resolveStatusAction(active);
  const isStore = permissions.variant === "store";

  const quickActions: QuickAction[] = [];
  if (isStore) {
    if (permissions.canEdit) quickActions.push({ key: "edit" });
    if (permissions.canResetPassword) quickActions.push({ key: "password" });
    if (permissions.canPrintBarcode) quickActions.push({ key: "barcode" });
    if (permissions.canToggleStatus) {
      quickActions.push({ key: "status", intent: status.intent, danger: status.danger });
    }
  }

  const moreItems: MoreItemKey[] = [];
  if (permissions.canPrintBarcode) moreItems.push("barcode");
  if (permissions.canOpenAccess) moreItems.push("access");
  if (permissions.canOpenPos) moreItems.push("pos");
  if (permissions.canCopyPermissions) moreItems.push("copyPermissions");
  if (permissions.canViewLoginRecords) moreItems.push("loginRecords");
  const moreStatus = permissions.canToggleStatus ? status.intent : null;

  return {
    quickActions,
    headerEdit: !isStore && permissions.canEdit,
    moreItems,
    moreStatus,
    showMore: moreItems.length > 0 || moreStatus !== null,
    // 管理员版把停用放到页面最底部，与店长版的快捷按钮二选一。
    footerStatus: isStore ? null : moreStatus,
  };
}

export type HeaderTagKey = "active" | "disabled" | "pendingFirstLogin" | "loginDisabled";

/** 头部状态标签：启用 / 停用二选一；待首次登录只对启用账号显示；不能登录是例外标签。 */
export function buildHeaderStatusTags(input: {
  active: boolean;
  mustChangePassword?: boolean;
  loginEnabled?: boolean;
}): HeaderTagKey[] {
  const tags: HeaderTagKey[] = [input.active ? "active" : "disabled"];
  if (input.active && input.mustChangePassword) tags.push("pendingFirstLogin");
  // 旧后端不返回 loginEnabled（undefined）视为允许登录。
  if (input.loginEnabled === false) tags.push("loginDisabled");
  return tags;
}

// ───────────────────────── 姓名展示 ─────────────────────────

export type NamePartKind = "family" | "given" | "middle" | "full";

export interface NameDisplayPart {
  kind: NamePartKind;
  value: string;
}

/**
 * 员工资料「姓名」行：姓 Chen · 名 Emily · 中间名 Rose。
 * 没有结构化姓名（旧后端 / 存量账号）时回落到全名，再回落到账号，保证这一行永远有内容。
 */
export function buildNameDisplayParts(source: PersonNameSource): NameDisplayPart[] {
  const parts: NameDisplayPart[] = [];
  const family = cleanNamePart(source.familyName);
  const given = cleanNamePart(source.givenName);
  const middle = cleanNamePart(source.middleName);
  if (family) parts.push({ kind: "family", value: family });
  if (given) parts.push({ kind: "given", value: given });
  if (middle) parts.push({ kind: "middle", value: middle });
  if (parts.length) return parts;
  const full = cleanNamePart(source.fullName) ?? cleanNamePart(source.username);
  return full ? [{ kind: "full", value: full }] : [];
}

// ───────────────────────── 排班与记录入口 ─────────────────────────

export type ScheduleEntryKey = "schedule" | "records";

/** 店长版展示「排班 / 考勤记录」两行入口（需要分店）；管理员版没有员工资料与排班。 */
export function resolveScheduleEntries(input: { variant: DetailVariant; hasStoreCode: boolean }): ScheduleEntryKey[] {
  return input.variant === "store" && input.hasStoreCode ? ["schedule", "records"] : [];
}

export interface ScheduleLike {
  workDate: string;
  startTime: string;
}

export interface WeekLike {
  days: { workDate: string; schedules: ScheduleLike[] }[];
}

/** 周一为 0，与 attendance 命名空间 weekdays.0~6 的顺序一致。 */
export function weekdayIndexFromDate(workDate: string): number | null {
  const parsed = new Date(`${workDate.slice(0, 10)}T00:00:00`);
  if (Number.isNaN(parsed.getTime())) return null;
  return (parsed.getDay() + 6) % 7;
}

function toHourMinute(value: string): string | null {
  const text = value.includes("T") ? value.split("T").pop() ?? "" : value;
  const match = /^(\d{1,2}):(\d{2})/.exec(text);
  return match ? `${match[1].padStart(2, "0")}:${match[2]}` : null;
}

export interface WeekScheduleSummary {
  shiftCount: number;
  next: { weekdayIndex: number; time: string } | null;
}

/** 本周几班 + 下一班（从 now 起最近一班；本周已无班次则没有 next）。 */
export function summarizeWeekSchedule(week: WeekLike | undefined, now: Date): WeekScheduleSummary {
  const upcoming: { at: number; weekdayIndex: number; time: string }[] = [];
  let shiftCount = 0;
  for (const day of week?.days ?? []) {
    for (const schedule of day.schedules) {
      shiftCount += 1;
      const time = toHourMinute(schedule.startTime);
      const weekdayIndex = weekdayIndexFromDate(schedule.workDate || day.workDate);
      if (!time || weekdayIndex === null) continue;
      const at = new Date(`${(schedule.workDate || day.workDate).slice(0, 10)}T${time}:00`).getTime();
      if (!Number.isNaN(at) && at >= now.getTime()) upcoming.push({ at, weekdayIndex, time });
    }
  }
  upcoming.sort((left, right) => left.at - right.at);
  return {
    shiftCount,
    next: upcoming[0] ? { weekdayIndex: upcoming[0].weekdayIndex, time: upcoming[0].time } : null,
  };
}

export interface RecordCountSummary {
  count: number;
  /** 取到的条数已顶满 limit 且最旧一条仍在窗口内：真实数量可能更多，界面写「N+ 次」。 */
  capped: boolean;
}

/** 近 N 天考勤记录次数（按 workDate 计）。 */
export function summarizeRecordCount(
  records: { workDate: string }[] | undefined,
  now: Date,
  options: { windowDays?: number; limit: number },
): RecordCountSummary {
  const windowDays = options.windowDays ?? 30;
  const cutoff = new Date(now);
  cutoff.setHours(0, 0, 0, 0);
  cutoff.setDate(cutoff.getDate() - windowDays);
  const inWindow = (records ?? []).filter((record) => {
    const parsed = new Date(`${record.workDate.slice(0, 10)}T00:00:00`);
    return !Number.isNaN(parsed.getTime()) && parsed.getTime() >= cutoff.getTime();
  });
  const capped = (records?.length ?? 0) >= options.limit && inWindow.length === (records?.length ?? 0);
  return { count: inWindow.length, capped };
}

// ───────────────────────── 管理员版的汇总 ─────────────────────────

export interface StoreRelationLike {
  storeGUID: string;
  storeCode: string;
  storeName: string;
  isManageable: boolean;
}

export interface StoreRelationSummary {
  count: number;
  manageableCount: number;
  firstName: string;
  firstIsManageable: boolean;
}

/** 「2 家 · Bankstown 管理」：管理分店排前面作为代表。 */
export function summarizeStoreRelations(stores: readonly StoreRelationLike[]): StoreRelationSummary {
  const ordered = [...stores].sort((left, right) => Number(right.isManageable) - Number(left.isManageable));
  const first = ordered[0];
  return {
    count: stores.length,
    manageableCount: stores.filter((store) => store.isManageable).length,
    firstName: first ? first.storeName || first.storeCode : "",
    firstIsManageable: Boolean(first?.isManageable),
  };
}

/** 店长角色由「管理」分店派生（不能直接授予），详情里的角色行要标明。 */
export function isStoreManagerRoleDerived(
  roleNames: readonly string[],
  stores: readonly StoreRelationLike[],
  isStoreManagerRoleName: (roleName: string) => boolean,
) {
  return stores.some((store) => store.isManageable) && roleNames.some((roleName) => isStoreManagerRoleName(roleName));
}

export interface SessionCountSummary {
  active: number;
  /** 只读了第一页：有效会话数是下限，界面写「N+」。 */
  capped: boolean;
}

export function summarizeActiveSessions(
  records: readonly { status: string }[],
  total: number,
): SessionCountSummary {
  const active = records.filter((record) => record.status === "active").length;
  return { active, capped: total > records.length };
}

// ───────────────────────── 账号信息展示格式 ─────────────────────────

function pad2(value: number) {
  return String(value).padStart(2, "0");
}

/** 本地时间 YYYY-MM-DD HH:mm；空值 / 非法值返回 null，界面显示「—」。 */
export function formatLocalDateTime(value: string | undefined | null): string | null {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return null;
  return `${parsed.getFullYear()}-${pad2(parsed.getMonth() + 1)}-${pad2(parsed.getDate())} ${pad2(parsed.getHours())}:${pad2(parsed.getMinutes())}`;
}

/** 本地日期 YYYY-MM-DD。 */
export function formatLocalDate(value: string | undefined | null): string | null {
  const dateTime = formatLocalDateTime(value);
  return dateTime ? dateTime.slice(0, 10) : null;
}

/** 原始 GUID 不整串展示：只露首 4 尾 4，完整值通过复制按钮取得。 */
export function shortenGuid(value: string): string {
  const text = value.trim();
  return text.length <= 10 ? text : `${text.slice(0, 4)}…${text.slice(-4)}`;
}

/**
 * 管理员版发「设置密码邮件」要走某个分店的店员接口：优先用路由里的 storeCode，
 * 其次目标的第一个管理分店，再其次任意关联分店；都没有则不能发邮件（只剩直接设置新密码）。
 */
export function resolveEmailStoreCode(
  routeStoreCode: string | undefined,
  stores: readonly StoreRelationLike[],
): string | null {
  const fromRoute = routeStoreCode?.trim();
  if (fromRoute) return fromRoute;
  const preferred = stores.find((store) => store.isManageable) ?? stores[0];
  return preferred?.storeCode?.trim() || null;
}

/**
 * 管理员版「收银权限」可选分店：收银权限是按分店设置的。
 * 店长级管理者只能选自己管理的分店；其他人可选目标的全部关联分店。
 */
export function selectPosStores<T extends { storeGUID: string }>(
  linkedStores: readonly T[],
  options: { restrictToGuids: readonly string[] | null },
): T[] {
  if (!options.restrictToGuids) return [...linkedStores];
  const allowed = new Set(options.restrictToGuids.map((guid) => guid.trim().toLowerCase()));
  return linkedStores.filter((store) => allowed.has(store.storeGUID.trim().toLowerCase()));
}
