import type {
  DirectPermissionDraft,
  UserAccessEligibility,
  UserAccessEligibilityContext,
  UserAccessErrorKind,
  UserAccessPermissionState,
  UserAccessRole,
  UserAccessStore,
  UserAccessStoreAssignment,
  UserAccessStoreState,
} from "./access-management-types";

const ADMIN_ROLE_NAMES = new Set([
  "admin",
  "管理员",
  "superadmin",
  "超级管理员",
]);

const STORE_MANAGER_ROLE_NAMES = new Set(["storemanager", "店长", "经理"]);

const STORE_STAFF_ROLE_NAMES = new Set([
  "storestaff",
  "employee",
  "店铺员工",
  "店员",
  "员工",
]);

const PRIVILEGED_ROLE_NAMES = new Set([
  ...ADMIN_ROLE_NAMES,
  ...STORE_MANAGER_ROLE_NAMES,
  "warehousemanager",
  "warehouse",
  "仓库经理",
  "仓库管理员",
  "warehouseadmin",
]);

function normalizeIdentityValue(value: string) {
  return value.trim().toLowerCase();
}

function uniqueCodes(codes: string[]) {
  const result: string[] = [];
  const seen = new Set<string>();

  codes.forEach((code) => {
    const normalizedCode = code.trim();
    if (!normalizedCode || seen.has(normalizedCode)) return;
    seen.add(normalizedCode);
    result.push(normalizedCode);
  });

  return result;
}

export function isAccessAdminRoleName(roleName: string) {
  return ADMIN_ROLE_NAMES.has(normalizeIdentityValue(roleName));
}

export function isStoreManagerRoleName(roleName: string) {
  return STORE_MANAGER_ROLE_NAMES.has(normalizeIdentityValue(roleName));
}

export function isStoreStaffRoleName(roleName: string) {
  return STORE_STAFF_ROLE_NAMES.has(normalizeIdentityValue(roleName));
}

export function hasPrivilegedAccessRole(roleNames: string[]) {
  return roleNames.some((roleName) =>
    PRIVILEGED_ROLE_NAMES.has(normalizeIdentityValue(roleName)),
  );
}

export function buildUserAccessStoreAssignments(stores: UserAccessStore[]) {
  return stores.reduce<UserAccessStoreAssignment[]>((assignments, store) => {
    const storeGUID = store.storeGUID.trim();
    if (
      !storeGUID ||
      assignments.some((item) => item.storeGUID === storeGUID)
    ) {
      return assignments;
    }

    assignments.push({ storeGUID, isPrimary: store.isManageable });
    return assignments;
  }, []);
}

export function getUserAccessStoreState(
  assignments: UserAccessStoreAssignment[],
  storeGuid: string,
): UserAccessStoreState {
  const normalizedStoreGuid = storeGuid.trim();
  const assignment = assignments.find(
    (item) => item.storeGUID === normalizedStoreGuid,
  );
  if (!assignment) return "unassigned";
  return assignment.isPrimary ? "manage" : "view";
}

export function setUserAccessStoreState(
  assignments: UserAccessStoreAssignment[],
  storeGuid: string,
  state: UserAccessStoreState,
) {
  const normalizedStoreGuid = storeGuid.trim();
  if (!normalizedStoreGuid) return assignments;

  const nextAssignments = assignments.filter(
    (item) => item.storeGUID !== normalizedStoreGuid,
  );
  if (state === "unassigned") return nextAssignments;

  const nextAssignment = {
    storeGUID: normalizedStoreGuid,
    isPrimary: state === "manage",
  };
  const currentIndex = assignments.findIndex(
    (item) => item.storeGUID === normalizedStoreGuid,
  );

  // 保留原有顺序，避免切换查看/管理状态时列表发生无意义跳动。
  if (currentIndex >= 0) {
    const result = [...assignments];
    result[currentIndex] = nextAssignment;
    return result;
  }

  return [...nextAssignments, nextAssignment];
}

export function buildDirectPermissionDraft(
  state: UserAccessPermissionState,
): DirectPermissionDraft {
  const directCodes = uniqueCodes(state.directPermissionCodes);
  return {
    baselineCodes: [...directCodes],
    selectedCodes: [...directCodes],
    inheritedPermissionCodes: uniqueCodes(state.inheritedPermissionCodes),
  };
}

export function getAccessPermissionSelectionState(
  draft: DirectPermissionDraft,
  permissionCode: string,
) {
  const normalizedCode = permissionCode.trim();
  const inherited = draft.inheritedPermissionCodes.includes(normalizedCode);
  const direct = draft.selectedCodes.includes(normalizedCode);
  return {
    checked: inherited || direct,
    direct,
    inherited,
    locked: inherited,
  };
}

export function toggleDirectPermission(
  draft: DirectPermissionDraft,
  permissionCode: string,
  checked: boolean,
): DirectPermissionDraft {
  const normalizedCode = permissionCode.trim();
  if (
    !normalizedCode ||
    draft.inheritedPermissionCodes.includes(normalizedCode)
  ) {
    return draft;
  }

  const nextCodes = new Set(draft.selectedCodes);
  if (checked) nextCodes.add(normalizedCode);
  else nextCodes.delete(normalizedCode);

  return {
    ...draft,
    selectedCodes: uniqueCodes(Array.from(nextCodes)),
  };
}

export function getAccessRoleSelectionState({
  role,
  selectedRoleGuids,
  hasManagedStoreAssignment,
}: {
  role: UserAccessRole;
  selectedRoleGuids: string[];
  hasManagedStoreAssignment: boolean;
}) {
  const derived = isStoreManagerRoleName(role.roleName);
  return {
    // 店长角色由分店管理关系派生，不能通过角色接口直接增删。
    selected: derived
      ? hasManagedStoreAssignment
      : selectedRoleGuids.includes(role.roleGUID),
    locked: derived,
    derived,
  };
}

export function buildAssignableRoleGuids(
  roles: UserAccessRole[],
  selectedRoleGuids: string[],
) {
  const selectedRoleGuidSet = new Set(uniqueCodes(selectedRoleGuids));
  return roles
    .filter(
      (role) =>
        !isStoreManagerRoleName(role.roleName) &&
        selectedRoleGuidSet.has(role.roleGUID),
    )
    .map((role) => role.roleGUID);
}

export function filterUserAccessRoles(
  roles: UserAccessRole[],
  keyword: string,
) {
  const normalizedKeyword = normalizeIdentityValue(keyword);
  if (!normalizedKeyword) return roles;

  return roles.filter((role) =>
    [role.roleName, role.description ?? ""].some((value) =>
      normalizeIdentityValue(value).includes(normalizedKeyword),
    ),
  );
}

export function limitUserAccessRolesForActor(
  roles: UserAccessRole[],
  isAdmin: boolean,
) {
  if (isAdmin) return roles;
  // 店长只能增删员工角色；其他既有角色由后端保留且不在可编辑目录中暴露。
  return roles.filter((role) => isStoreStaffRoleName(role.roleName));
}

function deniedEligibility(
  reason: NonNullable<UserAccessEligibility["reason"]>,
): UserAccessEligibility {
  return {
    canOpen: false,
    storesMode: "hidden",
    rolesMode: "hidden",
    permissionsMode: "hidden",
    canManagePos: false,
    canGrantStoreManagement: false,
    reason,
  };
}

export function applyUserAccessSectionRestrictions(
  eligibility: UserAccessEligibility,
  restrictions: {
    rolesForbidden: boolean;
    permissionsForbidden: boolean;
  },
): UserAccessEligibility {
  // 全局授权失败只关闭受影响分段，不能连带阻断仍在范围内的分店关系维护。
  const rolesMode = restrictions.rolesForbidden
    ? "hidden"
    : eligibility.rolesMode;
  const permissionsMode =
    restrictions.rolesForbidden || restrictions.permissionsForbidden
      ? "hidden"
      : eligibility.permissionsMode;
  // 按店 POS 覆盖有独立 storeGUID 安全校验，不受账号级角色分段拒绝影响。
  const canManagePos = eligibility.canManagePos;

  return {
    ...eligibility,
    rolesMode,
    permissionsMode,
    canManagePos,
    canOpen:
      eligibility.storesMode !== "hidden" ||
      rolesMode !== "hidden" ||
      permissionsMode !== "hidden" ||
      canManagePos,
  };
}

export function getUserAccessEligibility(
  context: UserAccessEligibilityContext,
): UserAccessEligibility {
  if (context.isDeviceMode) return deniedEligibility("deviceMode");

  const currentUserGuid = context.currentUserGuid?.trim().toLowerCase();
  const targetUserGuid = context.targetUserGuid.trim().toLowerCase();
  if (
    !context.isAdmin &&
    currentUserGuid &&
    currentUserGuid === targetUserGuid
  ) {
    return deniedEligibility("self");
  }
  if (!context.isAdmin && hasPrivilegedAccessRole(context.targetRoleNames)) {
    return deniedEligibility("privilegedTarget");
  }

  // 账号授权只开放给管理员和店长；普通员工即使误带管理权限码也不能进入。
  if (!context.isAdmin && !context.isStoreManager) {
    return deniedEligibility("noPermission");
  }

  const canUseStoreScope = context.isAdmin || context.hasManageableStores;
  const targetIsEmployee = context.targetRoleNames.some((roleName) =>
    isStoreStaffRoleName(roleName),
  );
  const storesMode = context.isAdmin
    ? "editable"
    : canUseStoreScope
      ? "editable"
      : "hidden";
  const rolesMode = canUseStoreScope ? "editable" : "hidden";
  const permissionsMode =
    context.isAdmin ||
    (context.isStoreManager &&
      canUseStoreScope &&
      targetIsEmployee &&
      context.targetStatus === 1)
      ? "editable"
      : "hidden";
  const canManagePos = Boolean(
    (context.isAdmin ||
      (context.isStoreManager && context.canManagePos && targetIsEmployee)) &&
    canUseStoreScope &&
    context.targetStatus === 1,
  );
  const canOpen =
    storesMode !== "hidden" ||
    rolesMode !== "hidden" ||
    permissionsMode !== "hidden" ||
    canManagePos;

  if (!canOpen) return deniedEligibility("noPermission");

  return {
    canOpen,
    storesMode,
    rolesMode,
    permissionsMode,
    canManagePos,
    canGrantStoreManagement: context.isAdmin,
    reason: null,
  };
}

export function areAccessCodeSetsEqual(left: string[], right: string[]) {
  const leftSet = new Set(uniqueCodes(left));
  const rightSet = new Set(uniqueCodes(right));
  return (
    leftSet.size === rightSet.size &&
    Array.from(leftSet).every((code) => rightSet.has(code))
  );
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

export function classifyUserAccessError(error: unknown): UserAccessErrorKind {
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

/**
 * 管理员点「店长」这个派生角色行时该做什么。店长角色不能直接勾选：它由「至少有一家『管理』分店」自动派生，
 * 所以「升店长」的真实操作是把某家分店设为「管理」并保存门店。这里把这个引导做成纯函数，页面按结果执行：
 * - denied    ：不是管理员 / 门店区不可编辑——保持锁定，什么都不做；
 * - already   ：草稿里已经有「管理」分店（店长已由它派生）——提示取消方法，不动草稿，避免误降级；
 * - noStore   ：还没分配任何分店——提示先去分配；
 * - set       ：只有一家分店——直接在草稿里把它设为「管理」（仍须点「保存门店」才生效）；
 * - choose    ：有多家分店——不替管理员选，引导去「门店」自己选。
 */
export type StoreManagerGrantPlan =
  | { kind: "denied" }
  | { kind: "already" }
  | { kind: "noStore" }
  | { kind: "set"; storeGUID: string }
  | { kind: "choose" };

export function planStoreManagerGrant(input: {
  canGrantStoreManagement: boolean;
  storesEditable: boolean;
  assignments: readonly UserAccessStoreAssignment[];
}): StoreManagerGrantPlan {
  if (!input.canGrantStoreManagement || !input.storesEditable) return { kind: "denied" };
  if (input.assignments.some((item) => item.isPrimary)) return { kind: "already" };
  if (input.assignments.length === 0) return { kind: "noStore" };
  if (input.assignments.length === 1) return { kind: "set", storeGUID: input.assignments[0].storeGUID };
  return { kind: "choose" };
}
