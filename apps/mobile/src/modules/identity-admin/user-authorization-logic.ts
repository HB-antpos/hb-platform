import type {
  DirectPermissionDraft,
  UserAccessPermissionState,
  UserAccessStoreAssignment,
} from "@/modules/users/access-management-types";
import {
  buildAssignableRoleGuids,
  buildDirectPermissionDraft,
  buildUserAccessStoreAssignments,
  getAccessPermissionSelectionState,
  getAccessRoleSelectionState,
  getUserAccessStoreState,
  hasPrivilegedAccessRole,
  isStoreManagerRoleName,
  limitUserAccessRolesForActor,
  setUserAccessStoreState,
  toggleDirectPermission,
} from "@/modules/users/access-management";

export type AuthorizationSection = "roles" | "stores" | "permissions" | "mobile";

export type MobileMenuSection = "bottom" | "store" | "operations" | "reports";

export interface MobileMenuItem {
  key: string;
  title: string;
  subtitle?: string;
  permissionCodes: string[];
  visible: boolean;
  locked?: boolean;
}

export interface AuthorizationDrafts {
  stores: UserAccessStoreAssignment[];
  baselineStores: UserAccessStoreAssignment[];
  roles: string[];
  baselineRoles: string[];
  permissions: DirectPermissionDraft | null;
  mobile: Record<MobileMenuSection, string[]>;
  baselineMobile: Record<MobileMenuSection, string[]>;
}

export function splitMobileMenuItems<T extends MobileMenuItem>(items: T[]) {
  const sections: Record<MobileMenuSection, T[]> = {
    bottom: [],
    store: [],
    operations: [],
    reports: [],
  };
  const bottomKeys = new Set(["settings", "dashboard"]);
  const reportKeys = new Set(["reports", "reports-sales"]);
  const storeKeys = new Set(["home", "orders", "cart", "product-query", "local-supplier-invoices", "store-settings"]);
  items.forEach((item) => {
    const key = item.key.toLowerCase();
    const section: MobileMenuSection = reportKeys.has(key)
      ? "reports"
      : bottomKeys.has(key)
        ? "bottom"
        : storeKeys.has(key)
          ? "store"
          : "operations";
    sections[section].push(item);
  });
  return sections;
}

export function createAuthorizationDrafts({
  stores,
  roles,
  permissions,
  mobile,
}: {
  stores: UserAccessStoreAssignment[];
  roles: string[];
  permissions: UserAccessPermissionState | null;
  mobile: Record<MobileMenuSection, string[]>;
}): AuthorizationDrafts {
  const permissionDraft = permissions ? buildDirectPermissionDraft(permissions) : null;
  const cloneMobile = (value: Record<MobileMenuSection, string[]>) => ({
    bottom: [...value.bottom],
    store: [...value.store],
    operations: [...value.operations],
    reports: [...value.reports],
  });
  return {
    stores: [...stores],
    baselineStores: [...stores],
    roles: [...roles],
    baselineRoles: [...roles],
    permissions: permissionDraft,
    mobile: cloneMobile(mobile),
    baselineMobile: cloneMobile(mobile),
  };
}

export function areStringSetsEqual(left: string[], right: string[]) {
  if (left.length !== right.length) return false;
  const rightSet = new Set(right);
  return left.every((item) => rightSet.has(item));
}

export function togglePermissionCodes(
  draft: DirectPermissionDraft,
  permissionCodes: string[],
  checked: boolean,
  assignableCodes: string[] = permissionCodes,
) {
  const accepted = acceptedMenuPermissionCodes(permissionCodes);
  const assignable = new Set(assignableCodes.map(code => code.toLowerCase()));
  const inherited = new Set(draft.inheritedPermissionCodes.map(code => code.toLowerCase()));
  if (accepted.some(code => inherited.has(code.toLowerCase()))) return draft;
  if (checked) {
    if (accepted.some(code => draft.selectedCodes.some(selected => selected.toLowerCase() === code.toLowerCase()))) return draft;
    // 菜单权限是 OR 条件，只添加一个可分配项，不能顺带授予编辑/管理权限。
    const code = accepted.find(item => assignable.has(item.toLowerCase()));
    return code ? toggleDirectPermission(draft, code, true) : draft;
  }
  const removable = new Set(accepted.filter(code => assignable.has(code.toLowerCase())).map(code => code.toLowerCase()));
  return { ...draft, selectedCodes: draft.selectedCodes.filter(code => !removable.has(code.toLowerCase())) };
}

// 与 Web Expo 菜单预览的兼容别名保持一致；目标状态不能使用当前操作者的菜单接口推断。
const MENU_ALIASES = [
  ["LocalPurchase.View", "LocalInvocie.View"],
  ["Reports.ProductMovement.View", "Reports.View"],
  ["Warehouse.ManageProducts", "Warehouse.Manage"],
];
export function acceptedMenuPermissionCodes(codes: string[]) {
  return [...new Set(codes.flatMap(code => [code, ...(MENU_ALIASES.find(group => group.some(item => item.toLowerCase() === code.toLowerCase())) ?? [])]))];
}

export function userMenuPermissionState(
  definition: { permissionCodes: string[]; fixed?: boolean; requireAdmin?: boolean },
  draft: DirectPermissionDraft,
  assignableCodes: string[],
  state: { isSuperAdmin: boolean; implicitAllPermissions: boolean },
) {
  const codes = acceptedMenuPermissionCodes(definition.permissionCodes);
  const includesAny = (selected: string[]) => codes.some(code => selected.some(item => item.toLowerCase() === code.toLowerCase()));
  const inherited = includesAny(draft.inheritedPermissionCodes);
  const direct = includesAny(draft.selectedCodes);
  const visible = definition.requireAdmin ? state.isSuperAdmin : !!definition.fixed || !codes.length || state.implicitAllPermissions || state.isSuperAdmin || inherited || direct;
  const assignable = new Set(assignableCodes.map(code => code.toLowerCase()));
  const unremovableDirect = draft.selectedCodes.some(code => codes.some(item => item.toLowerCase() === code.toLowerCase()) && !assignable.has(code.toLowerCase()));
  const locked = !!definition.fixed || !!definition.requireAdmin || state.implicitAllPermissions || state.isSuperAdmin || inherited || !codes.length || (visible ? unremovableDirect : !codes.some(code => assignable.has(code.toLowerCase())));
  return { visible, inherited, direct, locked };
}

export function areMobileDraftsEqual(
  left: Record<MobileMenuSection, string[]>,
  right: Record<MobileMenuSection, string[]>,
) {
  return (["bottom", "store", "operations", "reports"] as MobileMenuSection[]).every(
    (section) => areStringSetsEqual(left[section], right[section]),
  );
}

export function hasAuthorizationDraftChanges(draft: AuthorizationDrafts) {
  const permissionsDirty = Boolean(
    draft.permissions &&
      !areStringSetsEqual(draft.permissions.selectedCodes, draft.permissions.baselineCodes),
  );
  return (
    !areStringSetsEqual(draft.stores.map(encodeStore), draft.baselineStores.map(encodeStore)) ||
    !areStringSetsEqual(draft.roles, draft.baselineRoles) ||
    permissionsDirty ||
    !areMobileDraftsEqual(draft.mobile, draft.baselineMobile)
  );
}

function encodeStore(item: UserAccessStoreAssignment) {
  return `${item.storeGUID}:${item.isPrimary ? "manage" : "view"}`;
}

export {
  buildAssignableRoleGuids,
  buildDirectPermissionDraft,
  buildUserAccessStoreAssignments,
  getAccessPermissionSelectionState,
  getAccessRoleSelectionState,
  getUserAccessStoreState,
  hasPrivilegedAccessRole,
  isStoreManagerRoleName,
  limitUserAccessRolesForActor,
  setUserAccessStoreState,
  toggleDirectPermission,
};
