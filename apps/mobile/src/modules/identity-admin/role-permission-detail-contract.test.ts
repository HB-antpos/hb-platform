import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * 角色详情 / 权限详情 / 新建权限改版后必须保留的业务保证。
 * 页面依赖 React Native，无法在 tsx 里渲染，因此读源码文本锁住关键写法。
 */
const directory = dirname(fileURLToPath(import.meta.url));
const read = (name: string) => readFileSync(resolve(directory, name), "utf8");

const roleScreen = read("role-detail-screen.tsx");
const permissionScreen = read("permission-detail-screen.tsx");
const createScreen = read("permission-create-screen.tsx");
const selectionSheet = read("selection-sheet.tsx");
const assignSheets = read("permission-assign-sheets.tsx");
const membersSection = read("role-detail-members.tsx");
const rolesRoute = readFileSync(resolve(directory, "../../../app/(shell)/roles/[roleGuid].tsx"), "utf8");
const permissionRoute = readFileSync(resolve(directory, "../../../app/(shell)/permissions/[code].tsx"), "utf8");
const permissionNewRoute = readFileSync(resolve(directory, "../../../app/(shell)/permissions/new.tsx"), "utf8");

// 路由文件保持原样，旧链接 / 推送 / 书签继续可用。
assert.match(rolesRoute, /role-detail-screen/);
assert.match(permissionRoute, /permission-detail-screen/);
assert.match(permissionNewRoute, /permission-create-screen/);

// ───────── 保存接口与权限判断沿用 ─────────
for (const permission of ["Roles.Create", "Roles.Edit", "Roles.ManagePermissions", "Roles.ManageUsers"]) {
  assert.ok(roleScreen.includes(`assertCurrentSession("${permission}")`), `角色详情写入前必须校验 ${permission}`);
}
assert.match(roleScreen, /updateIdentityRole\(roleGuid, payload, actorKey\)/, "资料仍走 updateIdentityRole");
assert.match(roleScreen, /saveIdentityRolePermissions\(roleGuid, selected, actorKey\)/, "权限仍走 saveIdentityRolePermissions");
assert.match(roleScreen, /addIdentityRoleUsers\(roleGuid, selectedUsers, actorKey\)/, "成员添加仍走 addIdentityRoleUsers");
assert.match(roleScreen, /removeIdentityRoleUser\(roleGuid, userGuid, actorKey\)/, "成员移除仍走 removeIdentityRoleUser");

// ───────── 四个分段共用一个吸底保存栏；按顺序提交，失败即停 ─────────
assert.equal((roleScreen.match(/<StickyActionBar/g) ?? []).length, 1, "整页只能有一个吸底保存栏");
assert.doesNotMatch(roleScreen, /AdminScreen|footer=\{footer\}\s*>\s*\{?tab ===/, "不再使用旧的 AdminScreen 与分段各自的保存按钮");
const profileStep = roleScreen.indexOf('key: "profile"');
const permissionStep = roleScreen.indexOf('key: "permissions"');
assert.ok(profileStep > 0 && permissionStep > profileStep, "保存顺序必须是先资料、后权限");
assert.match(roleScreen, /runRoleSaveSequence\(steps\)/, "必须用按顺序保存的汇总函数，失败即停");
assert.match(roleScreen, /buildRoleSaveReport/, "必须明确告诉用户已保存 / 未保存的部分");
assert.match(roleScreen, /usePreventRemove\(isAuthenticated && \(dirty \|\| saving\) && !allowRemove/, "有未保存改动或正在保存时，离开页面必须确认");
assert.match(roleScreen, /permissionsMutation\.mutateAsync\(selected\)/, "权限步骤使用点击保存瞬间的快照");
assert.match(roleScreen, /profileMutation\.mutateAsync\(snapshot\)/, "资料步骤使用点击保存瞬间的快照");

// ───────── 系统角色 / 派生角色只读并带说明 ─────────
assert.match(roleScreen, /nameLocked=\{!isCreate && !canEditRoleName\(roleKind\)\}/, "系统角色与店长角色的名称只读");
assert.match(roleScreen, /membersEditable = canEditRoleMembers\(roleKind\)/, "店长角色的成员由分店派生，不能手动增删");
assert.match(roleScreen, /derived=\{roleKind\.isDerived\}/, "店长角色的成员段要带说明");
assert.match(roleScreen, /readOnly=\{permissionsReadOnly\}/);
assert.match(roleScreen, /permissionsReadOnly = !capabilities\.canManagePermissions \|\| writeForbidden \|\| saving \|\| implicitAllPermissions/, "隐式拥有全部权限的角色，权限集合只读");

// ───────── 成员可点进人员详情；返回回到 Hub 页签 ─────────
assert.match(roleScreen, /pathname: "\/users\/\[userGuid\]"/, "成员行跳转到统一后的人员详情");
assert.match(membersSection, /onPress=\{\(\) => onOpen\(item\)\}/, "成员行必须可点击");
assert.match(roleScreen, /backToHubTab\(router, "roles"\)/, "角色详情返回回到 Hub 的「角色」页签");
assert.match(permissionScreen, /backToHubTab\(router, "permissions"\)/, "权限详情返回回到 Hub 的「权限」页签");
assert.match(createScreen, /backToHubTab\(router, "permissions"\)/, "新建权限返回回到 Hub 的「权限」页签");

// ───────── 权限详情：分配角色统一为底部弹层；失败原因就地显示 ─────────
assert.doesNotMatch(permissionScreen, /BusinessSheet/, "分配弹层不再使用原生 Modal 的 BusinessSheet");
assert.match(permissionScreen, /<AssignRolesSheet/);
assert.match(permissionScreen, /assignIdentityPermissionRoles\(code, assignment\.selected, actorKey\)/, "分配角色保存语义沿用");
assert.match(permissionScreen, /assignIdentityPermissionUsers\(code, \{ addUserGuids: delta\.added, removeUserGuids: delta\.removed \}, actorKey\)/, "分配用户仍只提交增量");
assert.match(permissionScreen, /notice=\{assignNotice\}/, "弹层内的保存失败要就地显示");
assert.match(assignSheets, /locked=\{locked\}/, "管理员等系统角色必须锁定");
assert.match(assignSheets, /p\.assignLockedHint/, "锁定的系统角色必须写明原因");

// ───────── 选项弹层统一为底部弹层，导出名与 props 不变 ─────────
assert.match(selectionSheet, /export function IdentitySelectionSheet\(/);
assert.match(selectionSheet, /title, value, options, onSelect, onDismiss/);
assert.match(selectionSheet, /AdminBottomSheet/, "单选弹层改为底部弹层");
assert.doesNotMatch(selectionSheet, /contentContainerStyle=\{\{ margin: 16/, "不再是居中弹窗");

// ───────── 新建权限：没有新增后端没有的能力 ─────────
assert.match(createScreen, /createIdentitySysPermission\(toCreatePermissionInput\(draft\), actorKey\)/);
assert.doesNotMatch(createScreen, /updateIdentitySysPermission|editIdentitySysPermission/, "后端没有编辑权限码的接口，本次不新增");
assert.equal((createScreen.match(/<StickyActionBar/g) ?? []).length, 1, "新建权限使用吸底保存栏");

console.log("role-permission-detail-contract.test.ts: ok");
