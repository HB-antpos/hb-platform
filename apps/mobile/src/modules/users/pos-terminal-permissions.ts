import type {
  PosTerminalPermissionOption,
  StoreUserPosTerminalPermissions,
} from "./types";

export interface PosPermissionDraft {
  baselineCodes: string[];
  selectedCodes: string[];
}

export interface PosPermissionGroup {
  group: string;
  permissions: PosTerminalPermissionOption[];
}

export interface PosPermissionEntryContext {
  canManagePosTerminalPermissions: boolean;
  canManageStore: boolean;
  currentUserGuid?: string | null;
  targetUserGuid: string;
  targetStatus: number;
  targetRoleNames: string[];
  storeGuid?: string | null;
}

export type PosPermissionEntryState = "enabled" | "hidden" | "disabled";
export type PosPermissionErrorKind =
  | "unauthorized"
  | "forbidden"
  | "notFound"
  | "network";

const PRIVILEGED_ROLE_NAMES = new Set([
  "admin",
  "管理员",
  "superadmin",
  "超级管理员",
  "storemanager",
  "店长",
  "经理",
  "warehousemanager",
  "仓库经理",
  "仓库管理员",
  "warehouseadmin",
]);

function normalizeIdentityValue(value: string) {
  return value.trim().toLowerCase();
}

/** 目标是否持有高权限角色；这类账号的 POS 授权不由本页管理（复制同事候选同样排除）。 */
export function hasPrivilegedPosPermissionRole(roleNames: string[]) {
  return roleNames.some((roleName) =>
    PRIVILEGED_ROLE_NAMES.has(normalizeIdentityValue(roleName))
  );
}

function uniquePermissionCodes(permissionCodes: string[]) {
  return Array.from(new Set(permissionCodes));
}

function filterAssignablePermissionCodes(
  permissionCodes: string[],
  assignablePermissions: PosTerminalPermissionOption[]
) {
  const assignableCodes = new Set(
    assignablePermissions.map((permission) => permission.code)
  );

  // 后端白名单是保存和展示的唯一边界，未知代码不能进入移动端草稿。
  return uniquePermissionCodes(
    permissionCodes.filter((permissionCode) =>
      assignableCodes.has(permissionCode)
    )
  );
}

export function getEffectivePosPermissionCodes(
  permissions: StoreUserPosTerminalPermissions
) {
  return filterAssignablePermissionCodes(
    permissions.effectivePermissionCodes,
    permissions.assignablePermissions
  );
}

export function buildGrantedPosPermissionCodes(
  selectedCodes: string[],
  assignablePermissions: PosTerminalPermissionOption[]
): string[] {
  return filterAssignablePermissionCodes(
    selectedCodes,
    assignablePermissions
  );
}

export function buildPosPermissionDraft(
  permissions: StoreUserPosTerminalPermissions
): PosPermissionDraft {
  const effectiveCodes = getEffectivePosPermissionCodes(permissions);

  return {
    baselineCodes: [...effectiveCodes],
    selectedCodes: [...effectiveCodes],
  };
}

export interface PosPermissionResponseState {
  initializedScopeKey: string | null;
  nextScopeKey: string;
  dirty: boolean;
  busy: boolean;
  appliedDataUpdatedAt: number;
  nextDataUpdatedAt: number;
}

export function shouldApplyPosPermissionResponse({
  initializedScopeKey,
  nextScopeKey,
  dirty,
  busy,
  appliedDataUpdatedAt,
  nextDataUpdatedAt,
}: PosPermissionResponseState) {
  // 首次加载或切换用户/分店时必须重建；同一范围只允许新响应更新干净草稿。
  if (initializedScopeKey !== nextScopeKey) return true;

  return !dirty && !busy && nextDataUpdatedAt > appliedDataUpdatedAt;
}

export interface PosPermissionRemovalGuardState {
  dirty: boolean;
  busy: boolean;
  allowRemove: boolean;
}

export interface PosPermissionRemovalBypassState {
  isAuthenticated: boolean;
  terminalErrorKind: PosPermissionErrorKind | null;
}

export function shouldBypassPosPermissionRemovalGuard({
  isAuthenticated,
  terminalErrorKind,
}: PosPermissionRemovalBypassState) {
  // 会话失效后的登录跳转优先级高于草稿或请求中的普通离页保护。
  return !isAuthenticated || terminalErrorKind === "unauthorized";
}

export function shouldPreventPosPermissionRemoval({
  dirty,
  busy,
  allowRemove,
}: PosPermissionRemovalGuardState) {
  return (dirty || busy) && !allowRemove;
}

export function groupPosPermissions(
  permissions: PosTerminalPermissionOption[]
): PosPermissionGroup[] {
  const groups = new Map<string, PosTerminalPermissionOption[]>();

  permissions.forEach((permission) => {
    const groupPermissions = groups.get(permission.group) ?? [];
    groupPermissions.push(permission);
    groups.set(permission.group, groupPermissions);
  });

  return Array.from(groups, ([group, groupPermissions]) => ({
    group,
    // 现代 JavaScript 的稳定排序会保留同名权限原有顺序。
    permissions: [...groupPermissions].sort((left, right) =>
      left.name.localeCompare(right.name, "zh-CN")
    ),
  }));
}

export function togglePosPermissionCode(
  selectedCodes: string[],
  permissionCode: string
) {
  const nextCodes = new Set(selectedCodes);

  if (nextCodes.has(permissionCode)) nextCodes.delete(permissionCode);
  else nextCodes.add(permissionCode);

  return Array.from(nextCodes);
}

export function setPosPermissionGroupSelection(
  selectedCodes: string[],
  groupPermissionCodes: string[],
  checked: boolean
) {
  const nextCodes = new Set(selectedCodes);

  // 批量操作只改当前分组，避免覆盖用户在其他分组的草稿选择。
  groupPermissionCodes.forEach((permissionCode) => {
    if (checked) nextCodes.add(permissionCode);
    else nextCodes.delete(permissionCode);
  });

  return Array.from(nextCodes);
}

export function arePermissionCodeSetsEqual(
  leftCodes: string[],
  rightCodes: string[]
) {
  const leftSet = new Set(leftCodes);
  const rightSet = new Set(rightCodes);

  return (
    leftSet.size === rightSet.size &&
    Array.from(leftSet).every((permissionCode) => rightSet.has(permissionCode))
  );
}

export function getPosPermissionEntryState({
  canManagePosTerminalPermissions,
  canManageStore,
  currentUserGuid,
  targetUserGuid,
  targetStatus,
  targetRoleNames,
  storeGuid,
}: PosPermissionEntryContext): PosPermissionEntryState {
  const normalizedCurrentUserGuid = currentUserGuid
    ? normalizeIdentityValue(currentUserGuid)
    : "";
  const normalizedTargetUserGuid = normalizeIdentityValue(targetUserGuid);
  const isCurrentUser =
    normalizedCurrentUserGuid.length > 0 &&
    normalizedCurrentUserGuid === normalizedTargetUserGuid;
  const hasPrivilegedRole = targetRoleNames.some((roleName) =>
    PRIVILEGED_ROLE_NAMES.has(normalizeIdentityValue(roleName))
  );

  // 隐藏条件属于业务资格判断，应优先于分店参数是否完整。
  if (
    !canManagePosTerminalPermissions ||
    !canManageStore ||
    isCurrentUser ||
    targetStatus !== 1 ||
    hasPrivilegedRole
  ) {
    return "hidden";
  }

  // 仅有空白的分店标识不可用于请求，按缺失参数显示禁用入口。
  return storeGuid?.trim() ? "enabled" : "disabled";
}

function getHttpStatus(error: unknown): number | undefined {
  if (!error || typeof error !== "object") return undefined;

  const directStatus = Reflect.get(error, "status");
  if (typeof directStatus === "number") return directStatus;

  const response = Reflect.get(error, "response");
  if (!response || typeof response !== "object") return undefined;

  const responseStatus = Reflect.get(response, "status");
  return typeof responseStatus === "number" ? responseStatus : undefined;
}

export function classifyPosPermissionError(
  error: unknown
): PosPermissionErrorKind {
  switch (getHttpStatus(error)) {
    case 401:
      return "unauthorized";
    case 403:
      return "forbidden";
    case 404:
      return "notFound";
    default:
      return "network";
  }
}
