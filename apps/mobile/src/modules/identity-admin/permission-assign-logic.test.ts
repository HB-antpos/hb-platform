import assert from "node:assert/strict";
import { buildAssignRoleRows, countAssignedRoles, createRoleAssignmentDraft, getRoleAssignmentDelta, toggleRoleAssignment } from "./permission-logic";
import { formatChangeBadge } from "./role-detail-logic";

const roles = [
  { roleGUID: "g-admin", roleName: "Admin", description: "全部功能" },
  { roleGUID: "g-manager", roleName: "StoreManager", description: "店长" },
  { roleGUID: "g-finance", roleName: "Finance" },
  { roleGUID: "g-staff", roleName: "StoreStaff", description: "收银" },
];
const localized: Record<string, string> = { Admin: "管理员", StoreManager: "店长", Finance: "财务", StoreStaff: "店员" };
const isLocked = (role: { roleName: string }) => role.roleName === "Admin";
const localizedName = (role: { roleName: string }) => localized[role.roleName] ?? role.roleName;
const base = { isLocked, localizedName };

// 无过滤：全部可见
assert.equal(buildAssignRoleRows(roles, { ...base, keyword: "", onlySelected: false, selectedGuids: [] }).length, 4);

// 仅看已选：保留已勾选 + 锁定的管理员
assert.deepEqual(
  buildAssignRoleRows(roles, { ...base, keyword: "", onlySelected: true, selectedGuids: ["g-manager", "g-finance"] }).map((role) => role.roleGUID),
  ["g-admin", "g-manager", "g-finance"],
);
assert.deepEqual(
  buildAssignRoleRows(roles, { ...base, keyword: "", onlySelected: true, selectedGuids: [] }).map((role) => role.roleGUID),
  ["g-admin"],
);

// 搜索：英文名 / 中文显示名 / 说明
assert.deepEqual(buildAssignRoleRows(roles, { ...base, keyword: "finance", onlySelected: false, selectedGuids: [] }).map((role) => role.roleGUID), ["g-finance"]);
assert.deepEqual(buildAssignRoleRows(roles, { ...base, keyword: "财务", onlySelected: false, selectedGuids: [] }).map((role) => role.roleGUID), ["g-finance"]);
assert.deepEqual(buildAssignRoleRows(roles, { ...base, keyword: "收银", onlySelected: false, selectedGuids: [] }).map((role) => role.roleGUID), ["g-staff"]);
// 搜索 + 仅看已选叠加
assert.deepEqual(buildAssignRoleRows(roles, { ...base, keyword: "店", onlySelected: true, selectedGuids: ["g-staff"] }).map((role) => role.roleGUID), ["g-staff"]);

// 已选计数：锁定角色算已选；不存在的 GUID 不计入
assert.deepEqual(countAssignedRoles(roles, ["g-manager", "g-finance", "g-gone"], isLocked), { selected: 3, total: 4 });
assert.deepEqual(countAssignedRoles(roles, [], isLocked), { selected: 1, total: 4 });

// 保存按钮：增减数量写在「保存（+1）」里
let draft = createRoleAssignmentDraft(["g-manager", "g-finance"]);
assert.deepEqual(getRoleAssignmentDelta(draft), { added: [], removed: [] });
draft = toggleRoleAssignment(draft, "g-staff");
let delta = getRoleAssignmentDelta(draft);
assert.equal(formatChangeBadge(delta.added.length, delta.removed.length), "+1");
draft = toggleRoleAssignment(draft, "g-finance");
delta = getRoleAssignmentDelta(draft);
assert.equal(formatChangeBadge(delta.added.length, delta.removed.length), "+1 −1");

console.log("permission-assign-logic.test.ts: ok");
