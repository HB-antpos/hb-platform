import assert from "node:assert/strict";
import {
  applyUserAccessSectionRestrictions,
  areAccessCodeSetsEqual,
  buildAssignableRoleGuids,
  buildDirectPermissionDraft,
  buildUserAccessStoreAssignments,
  classifyUserAccessError,
  filterUserAccessRoles,
  getAccessPermissionSelectionState,
  getAccessRoleSelectionState,
  getUserAccessEligibility,
  getUserAccessStoreState,
  hasPrivilegedAccessRole,
  isAccessAdminRoleName,
  isStoreStaffRoleName,
  isStoreManagerRoleName,
  limitUserAccessRolesForActor,
  planStoreManagerGrant,
  setUserAccessStoreState,
  toggleDirectPermission,
} from "./access-management";
import type {
  UserAccessPermissionState,
  UserAccessRole,
  UserAccessStore,
} from "./access-management-types";

const adminAliases = ["Admin", "管理员", "SuperAdmin", "超级管理员"];

adminAliases.forEach((roleName) => {
  assert.equal(isAccessAdminRoleName(` ${roleName.toUpperCase()} `), true);
});
assert.equal(isAccessAdminRoleName("StoreManager"), false);

[
  ...adminAliases,
  "StoreManager",
  "店长",
  "经理",
  "WarehouseManager",
  "Warehouse",
  "仓库经理",
  "仓库管理员",
  "WarehouseAdmin",
].forEach((roleName) => {
  assert.equal(
    hasPrivilegedAccessRole([roleName]),
    true,
    `${roleName} 应视为高权限角色`,
  );
});
assert.equal(hasPrivilegedAccessRole(["StoreStaff"]), false);
assert.equal(isStoreManagerRoleName("StoreManager"), true);
assert.equal(isStoreManagerRoleName("店长"), true);
assert.equal(isStoreManagerRoleName("经理"), true);
assert.equal(isStoreManagerRoleName("WarehouseManager"), false);
assert.equal(isStoreStaffRoleName("StoreStaff"), true);
assert.equal(isStoreStaffRoleName("Employee"), true);
assert.equal(isStoreStaffRoleName("店铺员工"), true);
assert.equal(isStoreStaffRoleName("员工"), true);
assert.equal(isStoreStaffRoleName("User"), false);

const assignedStores: UserAccessStore[] = [
  {
    storeGUID: "store-view",
    storeCode: "VIEW",
    storeName: "View store",
    isManageable: false,
  },
  {
    storeGUID: "store-manage",
    storeCode: "MANAGE",
    storeName: "Manage store",
    isManageable: true,
  },
];
const initialAssignments = buildUserAccessStoreAssignments(assignedStores);
assert.deepEqual(initialAssignments, [
  { storeGUID: "store-view", isPrimary: false },
  { storeGUID: "store-manage", isPrimary: true },
]);
assert.equal(
  getUserAccessStoreState(initialAssignments, "missing"),
  "unassigned",
);
assert.equal(getUserAccessStoreState(initialAssignments, "store-view"), "view");
assert.equal(
  getUserAccessStoreState(initialAssignments, "store-manage"),
  "manage",
);

const withNewViewStore = setUserAccessStoreState(
  initialAssignments,
  "store-new",
  "view",
);
assert.deepEqual(withNewViewStore.at(-1), {
  storeGUID: "store-new",
  isPrimary: false,
});
assert.deepEqual(
  setUserAccessStoreState(withNewViewStore, "store-view", "manage")[0],
  { storeGUID: "store-view", isPrimary: true },
);
assert.equal(
  setUserAccessStoreState(withNewViewStore, "store-new", "unassigned").some(
    (item) => item.storeGUID === "store-new",
  ),
  false,
);
assert.deepEqual(
  setUserAccessStoreState(initialAssignments, "  ", "manage"),
  initialAssignments,
  "空白 GUID 不应污染保存草稿",
);

const permissionState: UserAccessPermissionState = {
  userGuid: "user-1",
  isSuperAdmin: false,
  implicitAllPermissions: false,
  inheritedPermissionCodes: ["Users.View", "Reports.View"],
  directPermissionCodes: ["Users.Edit"],
  effectivePermissionCodes: ["Users.View", "Reports.View", "Users.Edit"],
  inheritedSources: [
    { roleName: "StoreStaff", permissionCodes: ["Users.View", "Reports.View"] },
  ],
};
const directDraft = buildDirectPermissionDraft(permissionState);
assert.deepEqual(directDraft, {
  baselineCodes: ["Users.Edit"],
  selectedCodes: ["Users.Edit"],
  inheritedPermissionCodes: ["Users.View", "Reports.View"],
});
assert.deepEqual(getAccessPermissionSelectionState(directDraft, "Users.View"), {
  checked: true,
  direct: false,
  inherited: true,
  locked: true,
});
assert.deepEqual(getAccessPermissionSelectionState(directDraft, "Users.Edit"), {
  checked: true,
  direct: true,
  inherited: false,
  locked: false,
});
assert.deepEqual(
  toggleDirectPermission(directDraft, "Users.View", false),
  directDraft,
  "角色继承权限必须锁定，不能从直接权限草稿中关闭",
);
assert.deepEqual(
  toggleDirectPermission(directDraft, "Orders.View", true).selectedCodes,
  ["Users.Edit", "Orders.View"],
);
assert.deepEqual(
  toggleDirectPermission(directDraft, "Users.Edit", false).selectedCodes,
  [],
);

const storeManagerRole: UserAccessRole = {
  roleGUID: "role-store-manager",
  roleName: "StoreManager",
  isActive: true,
};
assert.deepEqual(
  getAccessRoleSelectionState({
    role: storeManagerRole,
    selectedRoleGuids: [],
    hasManagedStoreAssignment: true,
  }),
  { selected: true, locked: true, derived: true },
  "任一管理分店应派生并锁定店长角色",
);
assert.deepEqual(
  getAccessRoleSelectionState({
    role: storeManagerRole,
    selectedRoleGuids: [storeManagerRole.roleGUID],
    hasManagedStoreAssignment: false,
  }),
  { selected: false, locked: true, derived: true },
  "店长角色不能通过角色全量替换直接保留",
);
const ordinaryRole: UserAccessRole = {
  roleGUID: "role-staff",
  roleName: "StoreStaff",
};
assert.deepEqual(
  limitUserAccessRolesForActor(
    [
      ordinaryRole,
      { roleGUID: "role-order", roleName: "Order" },
      storeManagerRole,
    ],
    false,
  ),
  [ordinaryRole],
  "店长的角色目录只能展示员工角色",
);
assert.equal(
  limitUserAccessRolesForActor(
    [ordinaryRole, { roleGUID: "role-order", roleName: "Order" }],
    true,
  ).length,
  2,
  "Admin 仍可查看并分配全部角色",
);
assert.deepEqual(
  filterUserAccessRoles(
    [
      storeManagerRole,
      { ...ordinaryRole, description: "Regular shop account" },
    ],
    "shop",
  ).map((role) => role.roleGUID),
  [ordinaryRole.roleGUID],
  "角色搜索同时匹配名称和说明",
);
assert.deepEqual(
  filterUserAccessRoles([storeManagerRole, ordinaryRole], "  "),
  [storeManagerRole, ordinaryRole],
  "空搜索词保留全部角色",
);
assert.deepEqual(
  getAccessRoleSelectionState({
    role: ordinaryRole,
    selectedRoleGuids: [ordinaryRole.roleGUID],
    hasManagedStoreAssignment: false,
  }),
  { selected: true, locked: false, derived: false },
);
assert.deepEqual(
  buildAssignableRoleGuids(
    [storeManagerRole, ordinaryRole],
    [storeManagerRole.roleGUID, ordinaryRole.roleGUID],
  ),
  [ordinaryRole.roleGUID],
  "角色全量替换必须排除由分店管理关系派生的店长角色",
);

assert.deepEqual(
  getUserAccessEligibility({
    isDeviceMode: false,
    isAdmin: true,
    isStoreManager: false,
    canManageStores: false,
    canManageRoles: false,
    canManagePos: false,
    currentUserGuid: "admin-1",
    targetUserGuid: "manager-1",
    targetStatus: 1,
    targetRoleNames: ["StoreManager"],
    hasManageableStores: true,
  }),
  {
    canOpen: true,
    storesMode: "editable",
    rolesMode: "editable",
    permissionsMode: "editable",
    canManagePos: true,
    canGrantStoreManagement: true,
    reason: null,
  },
  "Admin 可以管理高权限目标并拥有全部分段能力",
);
assert.equal(
  getUserAccessEligibility({
    isDeviceMode: false,
    isAdmin: true,
    isStoreManager: false,
    canManageStores: true,
    canManageRoles: true,
    canManagePos: true,
    currentUserGuid: "admin-1",
    targetUserGuid: "admin-1",
    targetStatus: 1,
    targetRoleNames: ["Admin"],
    hasManageableStores: true,
  }).canOpen,
  true,
  "Admin 全权语义允许维护本人权限，只有受限操作者禁止编辑本人",
);
assert.deepEqual(
  getUserAccessEligibility({
    isDeviceMode: false,
    isAdmin: false,
    isStoreManager: true,
    canManageStores: false,
    canManageRoles: false,
    canManagePos: true,
    currentUserGuid: "manager-1",
    targetUserGuid: "staff-1",
    targetStatus: 1,
    targetRoleNames: ["StoreStaff"],
    hasManageableStores: true,
  }),
  {
    canOpen: true,
    storesMode: "editable",
    rolesMode: "editable",
    permissionsMode: "editable",
    canManagePos: true,
    canGrantStoreManagement: false,
    reason: null,
  },
);
assert.equal(
  getUserAccessEligibility({
    isDeviceMode: false,
    isAdmin: false,
    isStoreManager: true,
    canManageStores: true,
    canManageRoles: true,
    canManagePos: true,
    currentUserGuid: "manager-1",
    targetUserGuid: "manager-1",
    targetStatus: 1,
    targetRoleNames: ["StoreManager"],
    hasManageableStores: true,
  }).reason,
  "self",
);
assert.equal(
  getUserAccessEligibility({
    isDeviceMode: true,
    isAdmin: true,
    isStoreManager: false,
    canManageStores: true,
    canManageRoles: true,
    canManagePos: true,
    currentUserGuid: "admin-1",
    targetUserGuid: "staff-1",
    targetStatus: 1,
    targetRoleNames: ["StoreStaff"],
    hasManageableStores: true,
  }).reason,
  "deviceMode",
);
assert.equal(
  getUserAccessEligibility({
    isDeviceMode: false,
    isAdmin: false,
    isStoreManager: true,
    canManageStores: true,
    canManageRoles: true,
    canManagePos: true,
    currentUserGuid: "manager-1",
    targetUserGuid: "manager-2",
    targetStatus: 1,
    targetRoleNames: ["StoreManager"],
    hasManageableStores: true,
  }).reason,
  "privilegedTarget",
);
const disabledTargetEligibility = getUserAccessEligibility({
  isDeviceMode: false,
  isAdmin: false,
  isStoreManager: true,
  canManageStores: true,
  canManageRoles: false,
  canManagePos: true,
  currentUserGuid: "manager-1",
  targetUserGuid: "staff-disabled",
  targetStatus: 0,
  targetRoleNames: ["StoreStaff"],
  hasManageableStores: true,
});
assert.equal(disabledTargetEligibility.canOpen, true);
assert.equal(disabledTargetEligibility.canManagePos, false);
assert.equal(
  disabledTargetEligibility.permissionsMode,
  "hidden",
  "店长不能给停用员工编辑账号级 POS 收银权限",
);

assert.equal(
  getUserAccessEligibility({
    isDeviceMode: false,
    isAdmin: false,
    isStoreManager: true,
    canManageStores: false,
    canManageRoles: true,
    canManagePos: false,
    currentUserGuid: "delegated-user",
    targetUserGuid: "staff-1",
    targetStatus: 1,
    targetRoleNames: ["StoreStaff"],
    hasManageableStores: false,
  }).reason,
  "noPermission",
  "非管理员即使具备角色委派权限，也必须先拥有可管理分店范围",
);

assert.equal(areAccessCodeSetsEqual(["b", "a", "a"], ["a", "b"]), true);
assert.equal(areAccessCodeSetsEqual(["a"], ["a", "b"]), false);
assert.equal(
  classifyUserAccessError({ response: { status: 401 } }),
  "unauthorized",
);
assert.equal(
  classifyUserAccessError({ response: { status: 403 } }),
  "forbidden",
);
assert.equal(classifyUserAccessError({ status: 404 }), "notFound");
assert.equal(classifyUserAccessError(new Error("offline")), "network");

const storeManagerAccess = getUserAccessEligibility({
  isDeviceMode: false,
  isAdmin: false,
  isStoreManager: true,
  canManageStores: true,
  canManageRoles: true,
  canManagePos: true,
  currentUserGuid: "manager-1",
  targetUserGuid: "staff-1",
  targetStatus: 1,
  targetRoleNames: ["StoreStaff"],
  hasManageableStores: true,
});
assert.deepEqual(
  storeManagerAccess,
  {
    canOpen: true,
    storesMode: "editable",
    rolesMode: "editable",
    permissionsMode: "editable",
    canManagePos: true,
    canGrantStoreManagement: false,
    reason: null,
  },
  "店长可维护范围内查看分店、员工角色和自己拥有的 POS 收银权限",
);
assert.deepEqual(
  applyUserAccessSectionRestrictions(storeManagerAccess, {
    rolesForbidden: true,
    permissionsForbidden: false,
  }),
  {
    ...storeManagerAccess,
    canOpen: true,
    rolesMode: "hidden",
    permissionsMode: "hidden",
    canManagePos: true,
  },
  "跨范围员工的全局授权被拒绝时，仍须保留分店关系和按店 POS 权限",
);

const nonEmployeeTargetAccess = getUserAccessEligibility({
  isDeviceMode: false,
  isAdmin: false,
  isStoreManager: true,
  canManageStores: true,
  canManageRoles: true,
  canManagePos: true,
  currentUserGuid: "manager-1",
  targetUserGuid: "ordinary-user-1",
  targetStatus: 1,
  targetRoleNames: ["User"],
  hasManageableStores: true,
});
assert.equal(
  nonEmployeeTargetAccess.permissionsMode,
  "hidden",
  "店长不能给非员工目标编辑账号级 POS 收银权限",
);
assert.equal(
  nonEmployeeTargetAccess.canManagePos,
  false,
  "店长不能从非员工目标进入按店 POS 权限页面",
);
assert.equal(
  nonEmployeeTargetAccess.canOpen,
  true,
  "非员工目标仍可进入分店和员工角色分段，以便先授予员工角色",
);

const delegatedStaffAccess = getUserAccessEligibility({
  isDeviceMode: false,
  isAdmin: false,
  isStoreManager: false,
  canManageStores: true,
  canManageRoles: true,
  canManagePos: true,
  currentUserGuid: "staff-operator",
  targetUserGuid: "staff-1",
  targetStatus: 1,
  targetRoleNames: ["StoreStaff"],
  hasManageableStores: true,
});
assert.equal(
  delegatedStaffAccess.canOpen,
  false,
  "普通员工即使误带管理权限码，也不能看到入口或通过深链加载授权数据",
);
assert.equal(delegatedStaffAccess.reason, "noPermission");

// ───────── 管理员点「店长」角色行：店长由「管理」分店派生，点它是引导而不是无反应 ─────────
{
  const view = (storeGUID: string) => ({ storeGUID, isPrimary: false });
  const manage = (storeGUID: string) => ({ storeGUID, isPrimary: true });
  const base = { canGrantStoreManagement: true, storesEditable: true };

  assert.deepEqual(planStoreManagerGrant({ ...base, assignments: [view("s1")] }), { kind: "set", storeGUID: "s1" }, "只有一家分店：直接设为管理");
  assert.deepEqual(planStoreManagerGrant({ ...base, assignments: [view("s1"), view("s2")] }), { kind: "choose" }, "多家分店：不替管理员选，引导去门店区选");
  assert.deepEqual(planStoreManagerGrant({ ...base, assignments: [] }), { kind: "noStore" }, "没有分店：提示先分配");
  assert.deepEqual(planStoreManagerGrant({ ...base, assignments: [manage("s1")] }), { kind: "already" }, "已有管理分店：不动草稿，避免误降级");
  assert.deepEqual(planStoreManagerGrant({ ...base, assignments: [manage("s1"), view("s2")] }), { kind: "already" });
  assert.deepEqual(planStoreManagerGrant({ ...base, assignments: [view("s1"), manage("s2")] }), { kind: "already" });
  // 非管理员（后端只允许管理员授予管理）/ 门店区不可编辑：保持锁定
  assert.deepEqual(planStoreManagerGrant({ canGrantStoreManagement: false, storesEditable: true, assignments: [view("s1")] }), { kind: "denied" });
  assert.deepEqual(planStoreManagerGrant({ canGrantStoreManagement: true, storesEditable: false, assignments: [view("s1")] }), { kind: "denied" });
  assert.deepEqual(planStoreManagerGrant({ canGrantStoreManagement: false, storesEditable: false, assignments: [] }), { kind: "denied" });

  // 与门店状态函数配合：set 之后该分店的状态是「管理」，草稿里就有了管理分店（店长行随之显示选中）
  const afterSet = setUserAccessStoreState([view("s1")], "s1", "manage");
  assert.equal(getUserAccessStoreState(afterSet, "s1"), "manage");
  assert.equal(planStoreManagerGrant({ ...base, assignments: afterSet }).kind, "already", "设置后再点不会重复动草稿");
}

// ───────── 仓库管理：不限分店的受限委派者 ─────────
{
  const warehouseBase = {
    isDeviceMode: false,
    isAdmin: false,
    isStoreManager: false,
    isWarehouseManager: true,
    canManageStores: true,
    canManageRoles: true,
    canManagePos: true,
    currentUserGuid: "warehouse-1",
    targetUserGuid: "staff-1",
    targetStatus: 1,
    targetRoleNames: ["StoreStaff"],
    // 后端对仓库管理返回的分店 isPrimary 恒为 false，所以「可管理分店」是 0，资格不能依赖它。
    hasManageableStores: false,
  };

  assert.deepEqual(
    getUserAccessEligibility(warehouseBase),
    {
      canOpen: true,
      storesMode: "editable",
      rolesMode: "editable",
      permissionsMode: "editable",
      canManagePos: true,
      canGrantStoreManagement: true,
      reason: null,
    },
    "仓库管理没有主分店也能改店员的分店、角色、权限，并可授管理关系",
  );

  // 只有仓库管理角色（没有店长角色）同样放行；sean 这种同时有两个角色的账号以仓库管理为准。
  assert.equal(getUserAccessEligibility(warehouseBase).canOpen, true);
  assert.equal(
    getUserAccessEligibility({ ...warehouseBase, isStoreManager: true }).canGrantStoreManagement,
    true,
    "同时有店长角色时仍按仓库管理口径：可授管理关系",
  );

  // 店长账号可以被仓库管理调整（要能把店长改成店员），但不再有 POS / 全局权限分段（目标不是员工）。
  const storeManagerTarget = getUserAccessEligibility({
    ...warehouseBase,
    targetUserGuid: "manager-1",
    targetRoleNames: ["StoreManager"],
  });
  assert.equal(storeManagerTarget.canOpen, true, "仓库管理可以调整店长账号");
  assert.equal(storeManagerTarget.storesMode, "editable");
  assert.equal(storeManagerTarget.rolesMode, "editable");
  assert.equal(storeManagerTarget.permissionsMode, "hidden");
  assert.equal(storeManagerTarget.canManagePos, false);

  // 提权保护：管理员与仓库管理账号永远不可改，别名也一样。
  for (const roleName of [...adminAliases, "WarehouseManager", "仓库经理", "Warehouse", "仓库管理员", "WarehouseAdmin"]) {
    const denied = getUserAccessEligibility({
      ...warehouseBase,
      targetUserGuid: "protected-1",
      targetRoleNames: ["StoreStaff", roleName],
    });
    assert.equal(denied.canOpen, false, `${roleName} 账号不能被仓库管理修改`);
    assert.equal(denied.reason, "privilegedTarget");
  }

  // 本人、停用目标、设备模式照常拒绝 / 收窄。
  assert.equal(
    getUserAccessEligibility({ ...warehouseBase, targetUserGuid: "warehouse-1" }).reason,
    "self",
    "仓库管理不能改本人",
  );
  assert.equal(getUserAccessEligibility({ ...warehouseBase, isDeviceMode: true }).reason, "deviceMode");
  assert.equal(
    getUserAccessEligibility({ ...warehouseBase, targetStatus: 0 }).canManagePos,
    false,
    "停用目标不开放 POS 权限入口",
  );

  // 没有仓库管理标记的普通非管理员（没有店长角色）仍然无权；店长行为不变（仍不能授管理关系）。
  assert.equal(
    getUserAccessEligibility({ ...warehouseBase, isWarehouseManager: false }).reason,
    "noPermission",
  );
  assert.equal(
    getUserAccessEligibility({
      ...warehouseBase,
      isWarehouseManager: false,
      isStoreManager: true,
      hasManageableStores: true,
    }).canGrantStoreManagement,
    false,
    "店长仍然只能授查看关系",
  );
  assert.equal(
    getUserAccessEligibility({
      ...warehouseBase,
      isWarehouseManager: false,
      isStoreManager: true,
      hasManageableStores: false,
    }).canOpen,
    false,
    "店长没有可管理分店时仍然不能进入（仓库管理的放宽不外溢）",
  );

  // 角色目录：仓库管理和店长一样，只暴露员工角色，管理员 / 仓库管理 / 订货员等都不在可编辑目录里。
  const catalog: UserAccessRole[] = [
    { roleGUID: "r-admin", roleName: "Admin" },
    { roleGUID: "r-warehouse", roleName: "WarehouseManager" },
    { roleGUID: "r-staff", roleName: "StoreStaff" },
    { roleGUID: "r-order", roleName: "Order" },
  ] as UserAccessRole[];
  assert.deepEqual(
    limitUserAccessRolesForActor(catalog, false).map((role) => role.roleName),
    ["StoreStaff"],
    "店长只看到「店员」一行",
  );
  // 仓库管理可以设置店长：目录里多一行派生的「店长」（不可勾选，点它引导去分店设为「管理」）；
  // 管理员、仓库管理、订货员等永远不出现。
  const catalogWithManager = [
    ...catalog,
    { roleGUID: "r-manager", roleName: "StoreManager" },
    { roleGUID: "r-manager-cn", roleName: "店长" },
  ] as UserAccessRole[];
  assert.deepEqual(
    limitUserAccessRolesForActor(catalogWithManager, false, true).map((role) => role.roleName),
    ["StoreStaff", "StoreManager", "店长"],
    "仓库管理：店员 + 派生的店长，不含管理员 / 仓库管理 / 订货员",
  );
  assert.deepEqual(
    limitUserAccessRolesForActor(catalogWithManager, false).map((role) => role.roleName),
    ["StoreStaff"],
    "店长（非仓库管理）仍看不到店长行",
  );
  assert.equal(
    limitUserAccessRolesForActor(catalogWithManager, true, true).length,
    catalogWithManager.length,
    "管理员看到全部",
  );
}

console.log("user access management pure logic tests passed");
