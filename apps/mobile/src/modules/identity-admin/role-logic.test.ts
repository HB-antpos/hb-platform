import assert from "node:assert/strict";
import {
  acceptSavedPermissionDraft,
  applyMenuPermissionChange,
  buildRoleMenuPreview,
  createPermissionDraft,
  filterPermissionCategories,
  getRoleCapabilities,
  expandPermissionAliases,
  isImplicitAllRole,
  isPermissionDraftDirty,
  isRoleDraftVerified,
  isUncertainRoleWrite,
  reconcilePermissionDraft,
  resolveRolePreviewCodes,
  togglePermission,
  togglePermissionCategory,
  validateRoleDraft,
  type RoleMenuDefinition,
} from "./role-logic";
import { getRoleMenuDefinitions } from "./role-menu-catalog";

assert.deepEqual(validateRoleDraft({ roleName: " ", description: "", isActive: true }), { roleName: "required" });
assert.deepEqual(validateRoleDraft({ roleName: "A", description: "", isActive: true }), { roleName: "tooShort" });
assert.deepEqual(validateRoleDraft({ roleName: "Admin", description: "x".repeat(201), isActive: true }), { description: "tooLong" });
assert.equal(isRoleDraftVerified({ roleName: " Staff ", description: " ", isActive: true }, { roleName: "Staff", isActive: true }), true);
assert.equal(isRoleDraftVerified({ roleName: "Staff", description: "", isActive: true }, { roleName: "Staff", isActive: false }), false);
assert.equal(isUncertainRoleWrite(Object.assign(new Error("timeout"), { status: 408 })), true);
assert.equal(isUncertainRoleWrite({ response: { status: 503 } }), true);
assert.equal(isUncertainRoleWrite({ response: { status: 400 } }), false);
assert.equal(isUncertainRoleWrite({ apiBusinessError: true, code: "ROLE_EXISTS" }), false);

const reader = getRoleCapabilities({
  isDeviceMode: false,
  isAdmin: false,
  hasPermission: (code) => code === "Roles.View",
});
assert.equal(reader.canView, true);
assert.equal(reader.canEdit, false);
const nonAdminWriter = getRoleCapabilities({ isDeviceMode: false, isAdmin: false, hasPermission: () => true });
assert.equal(nonAdminWriter.canView, true);
assert.equal(nonAdminWriter.canCreate || nonAdminWriter.canEdit || nonAdminWriter.canManagePermissions || nonAdminWriter.canManageUsers, false);
assert.equal(getRoleCapabilities({ isDeviceMode: true, isAdmin: true, hasPermission: () => true }).canView, false);
assert.equal(isImplicitAllRole({ roleName: "Admin", isSuperAdmin: false, implicitAllPermissions: false }, ["Admin"]), true);
assert.equal(isImplicitAllRole({ roleName: "Custom", isSuperAdmin: false, implicitAllPermissions: true }, []), true);
assert.deepEqual(expandPermissionAliases(["Reports.View"], [{ canonicalCode: "Reports.ProductMovement.View", aliasCodes: ["Reports.View"] }]), ["Reports.ProductMovement.View", "Reports.View"]);
assert.deepEqual(resolveRolePreviewCodes({
  selectedCodes: [],
  aliases: [],
}), []);

let draft = createPermissionDraft(["Users.View"]);
draft = togglePermission(draft, "Roles.View");
assert.equal(isPermissionDraftDirty(draft), true);
assert.deepEqual(reconcilePermissionDraft(draft, ["Orders.View"]).selected, ["Roles.View", "Users.View"]);
draft = togglePermissionCategory(draft, ["Users.View", "Roles.View"]);
assert.deepEqual(draft.selected, []);
draft = acceptSavedPermissionDraft(draft);
assert.equal(isPermissionDraftDirty(draft), false);

const filtered = filterPermissionCategories([
  { category: "users", displayName: "Users", permissions: [{ name: "Users.View", displayName: "View users" }] },
  { category: "orders", displayName: "Orders", permissions: [{ name: "Orders.Edit", displayName: "Edit orders" }] },
], "view");
assert.deepEqual(filtered.map((category) => category.category), ["users"]);

const menu: RoleMenuDefinition[] = [
  { key: "users", title: "Users", platform: "web", permissionCodes: ["Users.View"] },
  { key: "settings", title: "Settings", platform: "mobile", permissionCodes: [], fixed: true },
  { key: "settlements", title: "Settlements", platform: "web", permissionCodes: [], requireAdmin: true },
];
const preview = buildRoleMenuPreview(menu, [], { isSuperAdmin: false, implicitAllPermissions: false });
assert.equal(preview[0].visible, false);
assert.equal(preview[1].visible, true);
assert.equal(preview[2].visible, false);
assert.deepEqual(applyMenuPermissionChange([], preview[0], true), ["Users.View"]);
assert.deepEqual(applyMenuPermissionChange(["Users.View"], { ...preview[0], visible: true }, false), []);
assert.deepEqual(applyMenuPermissionChange(
  ["Reports.View", "Users.View"],
  { ...preview[0], permissionCodes: ["Reports.ProductMovement.View"], visible: true },
  false,
  [{ canonicalCode: "Reports.ProductMovement.View", aliasCodes: ["Reports.View"] }],
), ["Users.View"]);
assert.deepEqual(applyMenuPermissionChange([], { ...preview[0], permissionCodes: ["Orders.View", "Orders.Edit"] }, true), ["Orders.View"]);
assert.deepEqual(applyMenuPermissionChange([], { ...preview[0], permissionCodes: ["OrderFront", "Orders.View"] }, true, [], ["Orders.View"]), ["Orders.View"]);
assert.equal(buildRoleMenuPreview(menu, [], { isSuperAdmin: true, implicitAllPermissions: true }).every((item) => item.visible && item.readOnly), true);
const implicitPreview = buildRoleMenuPreview(menu, [], { isSuperAdmin: false, implicitAllPermissions: true });
assert.equal(implicitPreview[0].visible && implicitPreview[0].readOnly, true);
assert.equal(implicitPreview[2].visible, false);

const fullMenus = getRoleMenuDefinitions("en");
assert.equal(fullMenus.filter((item) => item.platform === "web").length, 45);
assert.equal(fullMenus.filter((item) => item.platform === "mobile").length, 31);
assert.deepEqual(
  fullMenus.find((item) => item.platform === "mobile" && item.key === "app-install")?.permissionCodes,
  ["System.ViewMobileAppInstallLinks"]
);
assert.deepEqual(
  fullMenus.find((item) => item.platform === "mobile" && item.key === "container-new-products")?.permissionCodes,
  ["Container.MobileNewProductsView"]
);
// 季节商品查询会展示其他分店库存，只认独立权限，不复用商品查询的 StoreProducts.View。
assert.deepEqual(
  fullMenus.find((item) => item.platform === "mobile" && item.key === "seasonal-product-insights")?.permissionCodes,
  ["SeasonalProductInsights.View"]
);
// 移动端收银用户条码入口只认独立的管理/打印权限，不复用 Web 的 Store.ManageOperations。
assert.deepEqual(
  fullMenus.find((item) => item.platform === "mobile" && item.key === "cash-register-users")?.permissionCodes,
  ["CashRegisterUsers.MobileManage", "CashRegisterUsers.MobilePrint"]
);
assert.equal(new Set(fullMenus.map((item) => `${item.platform}:${item.key}`)).size, fullMenus.length);
assert.deepEqual(fullMenus.find((item) => item.key === "/system/roles")?.permissionCodes, ["Roles.View"]);
assert.equal(fullMenus.find((item) => item.key === "settings")?.fixed, true);

console.log("role-logic.test.ts: ok");
