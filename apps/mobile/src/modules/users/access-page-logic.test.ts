import assert from "node:assert/strict";
import {
  ACCESS_SAVE_ORDER,
  accessTargetFromIdentityUser,
  applyAccessStoreFilter,
  countAccessStoresByState,
  pinAccessStoresByState,
  accessTargetFromStoreUser,
  buildAccessChangeSummary,
  buildAccessMenuModel,
  buildAccessRedirectParams,
  buildAccessSaveReportLines,
  buildAccessStoreOptions,
  classifyAccessSaveOutcome,
  classifyAccessTargetError,
  countAssignedStores,
  countEnabledPermissions,
  countSelectedAccessRoles,
  describeAccessChanges,
  excludePosPermissionCategories,
  filterAccessStores,
  filterPermissionCategories,
  isPosTerminalPermissionCode,
  listAccessSections,
  mergeAccessRoleOptions,
  mergeAccessStoreOptions,
  mergeAccessTarget,
  pickPosPermissionCategories,
  parseAccessCopyParam,
  parseAccessSectionParam,
  parseAccessTargetStatus,
  pickAccessSection,
  planAccessSaveSteps,
  resolveAccessPermissionRow,
  resolveAccessRoleRow,
  resolveStoreStateOptions,
  runAccessSaveSteps,
  summarizeAccessPermissionCounts,
  type AccessSaveStep,
} from "./access-page-logic";
import type {
  AccessStoreOption,
  DirectPermissionDraft,
  UserAccessRole,
} from "./access-management-types";

// ───────────── 分段：解析 / 可见性 / 初始分段 ─────────────
assert.equal(parseAccessSectionParam("stores"), "stores");
assert.equal(parseAccessSectionParam(" Roles "), "roles");
assert.equal(parseAccessSectionParam("permissions"), "permissions");
assert.equal(parseAccessSectionParam("menu"), "menu");
assert.equal(parseAccessSectionParam("mobile"), "menu", "旧管理员授权页的 mobile 要映射成 menu");
assert.equal(parseAccessSectionParam("whatever"), null);
assert.equal(parseAccessSectionParam(undefined), null);
assert.equal(parseAccessCopyParam("1"), "toOthers", "copy=1 来自详情页的「复制权限给他人」");
assert.equal(parseAccessCopyParam("true"), "toOthers");
assert.equal(parseAccessCopyParam("fromColleague"), "fromColleague");
assert.equal(parseAccessCopyParam("0"), null);
assert.equal(parseAccessCopyParam(undefined), null);

assert.deepEqual(
  listAccessSections({ storesMode: "editable", rolesMode: "editable", permissionsMode: "editable", isAdmin: true }),
  ["stores", "roles", "permissions", "menu"],
  "管理员多一个菜单分段",
);
assert.deepEqual(
  listAccessSections({ storesMode: "editable", rolesMode: "editable", permissionsMode: "editable", isAdmin: false }),
  ["stores", "roles", "permissions"],
  "店长没有菜单分段",
);
assert.deepEqual(
  listAccessSections({ storesMode: "editable", rolesMode: "editable", permissionsMode: "hidden", isAdmin: true }),
  ["stores", "roles"],
  "权限分段不可见时菜单也不出现（菜单依附在直接权限数据上）",
);
assert.deepEqual(
  listAccessSections({ storesMode: "hidden", rolesMode: "hidden", permissionsMode: "hidden", isAdmin: true }),
  [],
);
assert.equal(pickAccessSection(["stores", "roles"], "roles"), "roles");
assert.equal(pickAccessSection(["stores", "roles"], "menu"), "stores", "期望分段不可见时落到第一个可见分段");
assert.equal(pickAccessSection(["roles"], null), "roles");
assert.equal(pickAccessSection([], "stores"), null);

// 旧管理员路由的重定向参数：透传，只把 section=mobile 映射成 menu。
assert.deepEqual(
  buildAccessRedirectParams({ userGuid: "u1", section: "mobile" }),
  { userGuid: "u1", section: "menu" },
);
assert.deepEqual(
  buildAccessRedirectParams({ userGuid: ["u1", "u2"], storeCode: "B1", section: "roles", copy: "1", empty: "", skip: undefined }),
  { userGuid: "u1", storeCode: "B1", section: "roles", copy: "1" },
  "数组取第一个，空值和 undefined 丢掉，其余原样透传",
);
assert.deepEqual(
  buildAccessRedirectParams({ userGuid: "u1", section: "unknown" }),
  { userGuid: "u1" },
  "无法识别的 section 丢掉，新页面回落到第一个分段",
);

// ───────────── 目标用户自取：预填 + 服务端为准 ─────────────
assert.equal(parseAccessTargetStatus("0"), 0);
assert.equal(parseAccessTargetStatus("1"), 1);
assert.equal(parseAccessTargetStatus(""), null);
assert.equal(parseAccessTargetStatus(undefined), null);
assert.equal(parseAccessTargetStatus("abc"), null);

const prefilled = mergeAccessTarget(
  "u1",
  { userName: "Amy Chen", username: "amy", targetStatus: "1", roleNames: ["StoreStaff"] },
  null,
);
assert.equal(prefilled.resolved, false);
assert.equal(prefilled.name.primary, "Amy Chen");
assert.equal(prefilled.status, 1);
assert.deepEqual(prefilled.roleNames, ["StoreStaff"]);

// 深链进入：路由参数全部缺失，服务端数据到位后「受保护角色 / 停用」才有依据。
const bare = mergeAccessTarget("u1", {}, null);
assert.equal(bare.resolved, false);
assert.deepEqual(bare.roleNames, []);
assert.equal(bare.status, 1, "未解析前沿用旧默认（启用），页面此时仍显示加载而不放行编辑");

const resolved = mergeAccessTarget(
  "u1",
  { userName: "旧名字", username: "old", targetStatus: "1", roleNames: ["StoreStaff"] },
  accessTargetFromIdentityUser({
    username: "emily.chen",
    fullName: "Emily Rose Chen",
    givenName: "Emily",
    middleName: "Rose",
    familyName: "Chen",
    nickname: "Em",
    isActive: false,
    roleNames: ["Admin"],
  }),
);
assert.equal(resolved.resolved, true);
assert.equal(resolved.status, 0, "服务端说已停用就以服务端为准，不再信路由参数");
assert.deepEqual(resolved.roleNames, ["Admin"], "服务端角色覆盖路由预填");
assert.equal(resolved.username, "emily.chen");
assert.equal(resolved.name.primary, "Emily Rose Chen");
assert.equal(resolved.name.nickname, "Em");
assert.equal(resolved.name.initials, "EC");

const fromStoreUser = mergeAccessTarget(
  "u2",
  {},
  accessTargetFromStoreUser({ username: "wang", fullName: "王芳", status: 1, roleNames: ["StoreStaff"] }),
);
assert.equal(fromStoreUser.status, 1);
assert.equal(fromStoreUser.name.primary, "王芳");
assert.equal(fromStoreUser.name.initials, "王");
// 老后端没有拆分姓名时，服务端 fullName 缺失也能回落到路由预填的姓名。
assert.equal(
  mergeAccessTarget("u3", { userName: "Jack Nguyen" }, accessTargetFromStoreUser({ username: "jack", status: 0, roleNames: [] })).name.primary,
  "Jack Nguyen",
);

// 目标拉取失败分类：HTTP 状态优先，业务信封按业务码区分。
assert.equal(classifyAccessTargetError({ response: { status: 401 } }), "unauthorized");
assert.equal(classifyAccessTargetError({ response: { status: 403 } }), "forbidden");
assert.equal(classifyAccessTargetError({ response: { status: 404 } }), "notFound");
assert.equal(classifyAccessTargetError({ apiBusinessError: true, code: "FORBIDDEN" }), "forbidden");
assert.equal(classifyAccessTargetError({ apiBusinessError: true, code: "GET_STORE_USER_FAILED" }), "notFound");
assert.equal(classifyAccessTargetError({ apiBusinessError: true }), "notFound");
assert.equal(classifyAccessTargetError(new Error("Network Error")), "network");

// ───────────── 分店：店长收窄 ─────────────
const store = (code: string, name = code, extra: Partial<AccessStoreOption> = {}): AccessStoreOption => ({
  storeGUID: `guid-${code.toLowerCase()}`,
  storeCode: code,
  storeName: name,
  ...extra,
});
const catalog = [store("B1", "Bankstown"), store("L1", "Liverpool"), store("P1", "Parramatta")];

assert.deepEqual(
  mergeAccessStoreOptions(catalog, [store("B1", "Bankstown 新")]).map((item) => item.storeName),
  ["Bankstown 新", "Liverpool", "Parramatta"],
  "同 GUID 去重、后者覆盖，并按名称排序",
);
assert.deepEqual(
  mergeAccessStoreOptions([store("A10", "Store 10"), store("A2", "Store 2"), store("Z9", ""), store("B1", "bankstown")]).map((item) => item.storeCode),
  ["B1", "A2", "A10", "Z9"],
  "统一按名字排：忽略大小写、数字按数值、名字为空退回编号",
);
assert.deepEqual(
  buildAccessStoreOptions({ isAdmin: true, catalog, assigned: [], managedStoreCodes: new Set() }).map((item) => item.storeCode),
  ["B1", "L1", "P1"],
  "管理员看到全部分店",
);
assert.deepEqual(
  buildAccessStoreOptions({
    isAdmin: false,
    catalog,
    actorManaged: [store("L1", "Liverpool")],
    assigned: [store("P1", "Parramatta")],
    managedStoreCodes: new Set(["l1"]),
  }).map((item) => item.storeCode),
  ["L1"],
  "店长只列自己管理的分店，即使目标被分配到其它分店也不显示（不会用全分店目录制造越权草稿）",
);
assert.deepEqual(
  filterAccessStores(catalog, " bank ").map((item) => item.storeCode),
  ["B1"],
);
assert.deepEqual(filterAccessStores(catalog, "p1").map((item) => item.storeCode), ["P1"], "也能按编码搜");
assert.equal(filterAccessStores(catalog, "").length, 3);

// 分店授权筛选：各档台数随编辑实时变；选中筛选时记下当时属于该档的分店，之后改档不让行消失。
{
  const assigned = [
    { storeGUID: catalog[0].storeGUID, isPrimary: true },
    { storeGUID: catalog[2].storeGUID, isPrimary: false },
  ];
  assert.deepEqual(
    countAccessStoresByState(catalog, assigned),
    { all: 3, manage: 1, view: 1, unassigned: 1 },
    "全部 / 管理 / 查看 / 未分配 的台数",
  );
  const pinnedManage = pinAccessStoresByState(catalog, assigned, "manage");
  assert.deepEqual(applyAccessStoreFilter(catalog, "", pinnedManage).map((item) => item.storeCode), ["B1"], "只看管理");
  assert.deepEqual(
    applyAccessStoreFilter(catalog, "", pinAccessStoresByState(catalog, assigned, "unassigned")).map((item) => item.storeCode),
    ["L1"],
    "只看未分配",
  );
  // 在「管理」筛选里把 B1 改成查看：台数变了，但这一行仍留在列表里，直到重新点筛选。
  const edited = [
    { storeGUID: catalog[0].storeGUID, isPrimary: false },
    { storeGUID: catalog[2].storeGUID, isPrimary: false },
  ];
  assert.equal(countAccessStoresByState(catalog, edited).manage, 0, "改档后台数实时更新");
  assert.deepEqual(applyAccessStoreFilter(catalog, "", pinnedManage).map((item) => item.storeCode), ["B1"], "改档后行不消失");
  assert.deepEqual(
    applyAccessStoreFilter(catalog, "", pinAccessStoresByState(catalog, edited, "view")).map((item) => item.storeCode),
    ["B1", "P1"],
    "重新点筛选按最新分配取",
  );
  assert.equal(pinAccessStoresByState(catalog, assigned, "all"), null, "全部不过滤");
  assert.deepEqual(
    applyAccessStoreFilter(catalog, "parra", pinAccessStoresByState(catalog, assigned, "view")).map((item) => item.storeCode),
    ["P1"],
    "与关键字搜索叠加",
  );
  assert.equal(applyAccessStoreFilter(catalog, "bank", pinAccessStoresByState(catalog, assigned, "view")).length, 0, "叠加后可能为空");
}

// 三档按钮：管理员三档；店长没有「管理」档；目标已是「管理」时店长看到只读。
assert.deepEqual(resolveStoreStateOptions({ currentState: "view", canGrantManage: true, editable: true }), [
  { state: "unassigned", disabled: false },
  { state: "view", disabled: false },
  { state: "manage", disabled: false },
]);
assert.deepEqual(
  resolveStoreStateOptions({ currentState: "view", canGrantManage: false, editable: true }).map((item) => item.state),
  ["unassigned", "view"],
  "店长看不到「管理」这一档",
);
const lockedManage = resolveStoreStateOptions({ currentState: "manage", canGrantManage: false, editable: true });
assert.deepEqual(lockedManage.map((item) => item.state), ["unassigned", "view", "manage"]);
assert.ok(lockedManage.every((item) => item.disabled), "目标已是管理时店长不能把它降级，整行只读");
assert.ok(
  resolveStoreStateOptions({ currentState: "view", canGrantManage: true, editable: false }).every((item) => item.disabled),
  "分段只读时全部不可点",
);

assert.equal(
  countAssignedStores(
    [
      { storeGUID: "guid-b1", isPrimary: true },
      { storeGUID: "guid-l1", isPrimary: false },
      { storeGUID: "guid-other", isPrimary: false },
    ],
    new Set(["guid-b1", "guid-l1", "guid-p1"]),
  ),
  2,
  "分店计数只数可见分店里已分配（查看 + 管理）的",
);

// ───────────── 角色：店长派生 / 高权限 / 店长收窄 ─────────────
const role = (name: string, id = name.toLowerCase()): UserAccessRole => ({ roleGUID: id, roleName: name });
const manager = role("StoreManager");
const staff = role("StoreStaff");
const finance = role("Finance");
const admin = role("Admin");
const baseRow = {
  selectedRoleGuids: ["storestaff"],
  hasManagedStoreAssignment: false,
  isAdmin: true,
  rolesEditable: true,
  storesEditable: true,
  canGrantStoreManagement: true,
  busy: false,
};

const managerRow = resolveAccessRoleRow({ role: manager, ...baseRow });
assert.equal(managerRow.derived, true);
assert.equal(managerRow.selected, false, "没有管理分店时店长角色未选中");
assert.equal(managerRow.canGuideManagerGrant, true, "管理员点店长行是引导，不是无反应");
assert.equal(managerRow.disabled, false);
assert.equal(managerRow.highPrivilege, false, "店长行用「派生」标签，不再叠加高权限标签");
assert.equal(resolveAccessRoleRow({ role: manager, ...baseRow, hasManagedStoreAssignment: true }).selected, true, "有管理分店即派生出店长");
assert.equal(resolveAccessRoleRow({ role: manager, ...baseRow, canGrantStoreManagement: false, isAdmin: false }).canGuideManagerGrant, false);
assert.equal(
  resolveAccessRoleRow({ role: manager, ...baseRow, canGrantStoreManagement: false, isAdmin: false }).disabled,
  true,
  "非管理员的店长行保持锁定",
);
assert.equal(
  resolveAccessRoleRow({ role: manager, ...baseRow, storesEditable: false }).canGuideManagerGrant,
  false,
  "分店分段不可编辑时不引导",
);
assert.equal(resolveAccessRoleRow({ role: staff, ...baseRow }).selected, true);
assert.equal(resolveAccessRoleRow({ role: staff, ...baseRow }).disabled, false);
assert.equal(resolveAccessRoleRow({ role: finance, ...baseRow }).selected, false);
assert.equal(resolveAccessRoleRow({ role: admin, ...baseRow }).highPrivilege, true, "管理员角色带高权限标签");
assert.equal(resolveAccessRoleRow({ role: role("WarehouseManager"), ...baseRow }).highPrivilege, true);
assert.equal(resolveAccessRoleRow({ role: finance, ...baseRow }).highPrivilege, false);
assert.equal(
  resolveAccessRoleRow({ role: admin, ...baseRow, isAdmin: false, canGrantStoreManagement: false }).disabled,
  true,
  "非管理员不能动高权限角色",
);
// 仓库管理：不是管理员，但能授「管理」分店关系 → 可以设置店长。点「店长」派生行是引导（不被「仅管理员」锁死），
// 而管理员 / 仓库管理角色仍然不可动，也不会被引导。
const warehouseRow = { ...baseRow, isAdmin: false, canGrantStoreManagement: true };
const warehouseManagerRow = resolveAccessRoleRow({ role: manager, ...warehouseRow });
assert.equal(warehouseManagerRow.derived, true);
assert.equal(warehouseManagerRow.canGuideManagerGrant, true, "仓库管理点店长行是引导");
assert.equal(warehouseManagerRow.disabled, false, "仓库管理的店长行不被锁死");
assert.equal(
  resolveAccessRoleRow({ role: manager, ...warehouseRow, storesEditable: false }).canGuideManagerGrant,
  false,
  "分店分段不可编辑时仓库管理也不引导",
);
assert.equal(resolveAccessRoleRow({ role: admin, ...warehouseRow }).disabled, true, "仓库管理不能动管理员角色");
assert.equal(resolveAccessRoleRow({ role: admin, ...warehouseRow }).canGuideManagerGrant, false);
assert.equal(
  resolveAccessRoleRow({ role: role("WarehouseManager"), ...warehouseRow }).disabled,
  true,
  "仓库管理不能动仓库管理角色",
);
assert.equal(resolveAccessRoleRow({ role: staff, ...warehouseRow }).disabled, false, "员工角色仍可勾选");

assert.equal(resolveAccessRoleRow({ role: staff, ...baseRow, busy: true }).disabled, true);
assert.equal(resolveAccessRoleRow({ role: staff, ...baseRow, rolesEditable: false }).disabled, true);

assert.equal(
  countSelectedAccessRoles([manager, staff, finance], ["storestaff"], true),
  2,
  "角色计数含派生出来的店长",
);
assert.equal(countSelectedAccessRoles([staff], ["storestaff"], false), 1, "店长只看到「店员」一行");
assert.deepEqual(
  mergeAccessRoleOptions([finance, staff], [staff, admin]).map((item) => item.roleName),
  ["Admin", "Finance", "StoreStaff"],
);

// ───────────── 直接权限：来源标签 / 收银不在这里 / 计数 ─────────────
assert.equal(isPosTerminalPermissionCode("Permissions.PosTerminal.Sales.AddItem"), true);
assert.equal(isPosTerminalPermissionCode("PosTerminal.Payment.TakeCash"), true);
assert.equal(isPosTerminalPermissionCode("Reports.View"), false);

const cat = (name: string, codes: string[]) => ({
  category: name,
  displayName: name,
  permissions: codes.map((code) => ({ name: code })),
});
const categories = [
  cat("Reports", ["Reports.View", "Reports.Sales.View", "Reports.Hours.View"]),
  cat("POS", ["PosTerminal.Sales.AddItem", "Permissions.PosTerminal.Payment.TakeCash"]),
  cat("Staff", ["Staff.Review", "Staff.Schedule"]),
];
assert.deepEqual(
  excludePosPermissionCategories(categories).map((item) => [item.category, item.permissions.length]),
  [["Reports", 3], ["Staff", 2]],
  "收银权限整类不在授权页显示（统一去收银权限页）",
);
// 账号级收银默认值与「排除收银」互补：两者合起来正好是全部权限，授权页底部分组据此展示，旧能力不丢。
assert.deepEqual(
  pickPosPermissionCategories(categories).map((item) => [item.category, item.permissions.map((permission) => permission.name)]),
  [["POS", ["PosTerminal.Sales.AddItem", "Permissions.PosTerminal.Payment.TakeCash"]]],
  "只留收银权限码",
);
assert.equal(
  excludePosPermissionCategories(categories).flatMap((item) => item.permissions).length
    + pickPosPermissionCategories(categories).flatMap((item) => item.permissions).length,
  categories.flatMap((item) => item.permissions).length,
  "收银 / 非收银两部分互补，不重不漏",
);
assert.deepEqual(
  pickPosPermissionCategories([cat("Mixed", ["Reports.View", "PosTerminal.Payment.TakeCash"])]).map((item) => item.permissions.map((permission) => permission.name)),
  [["PosTerminal.Payment.TakeCash"]],
  "同一分类里混有收银码时只留收银码",
);
assert.deepEqual(
  filterPermissionCategories(
    excludePosPermissionCategories(categories),
    " 审核 ",
    (permission) => (permission.name === "Staff.Review" ? "员工资料审核" : permission.name),
  ).map((item) => [item.category, item.permissions.map((permission) => permission.name)]),
  [["Staff", ["Staff.Review"]]],
  "搜索只留命中的权限，空分类整体去掉",
);
assert.equal(filterPermissionCategories(categories, "  ", () => "").length, 3);

const draft: DirectPermissionDraft = {
  baselineCodes: ["Reports.Hours.View", "Staff.Review", "PosTerminal.Sales.AddItem"],
  selectedCodes: ["Reports.Hours.View", "Staff.Review", "PosTerminal.Sales.AddItem"],
  inheritedPermissionCodes: ["Reports.View", "Reports.Sales.View", "Staff.Schedule", "PosTerminal.Payment.TakeCash"],
};
const rowOf = (code: string, implicitAll = false) =>
  resolveAccessPermissionRow({
    draft,
    code,
    implicitAll,
    inheritedRoleNames: code.startsWith("Reports") ? ["店长"] : [],
  });
assert.deepEqual(rowOf("Reports.View"), { kind: "inherited", checked: true, locked: true, roleNames: ["店长"] }, "继承来的权限开关置灰，并标出继承自哪个角色");
assert.deepEqual(rowOf("Staff.Review"), { kind: "direct", checked: true, locked: false, roleNames: [] });
assert.deepEqual(rowOf("Nope.Code"), { kind: "none", checked: false, locked: false, roleNames: [] }, "没开放的显示「未开放」，可以打开");
assert.deepEqual(rowOf("Nope.Code", true), { kind: "implicit", checked: true, locked: true, roleNames: [] }, "管理员默认拥有，不能改");
assert.deepEqual(
  countEnabledPermissions(categories[0].permissions, (code) => rowOf(code)),
  { enabled: 3, total: 3 },
);
assert.deepEqual(
  countEnabledPermissions([{ name: "Reports.View" }, { name: "Nope.Code" }], (code) => rowOf(code)),
  { enabled: 1, total: 2 },
  "分类标题的「已开放 / 总数」",
);

// 三个数字：不含收银权限码；有效 = 继承 + 直接；直接只数不是继承来的。
const counts = summarizeAccessPermissionCounts({ draft, implicitAll: false, catalogCodes: [] });
assert.deepEqual(counts, { effective: 5, inherited: 3, direct: 2 });
assert.equal(counts.effective, counts.inherited + counts.direct);
assert.deepEqual(
  summarizeAccessPermissionCounts({
    draft: { ...draft, selectedCodes: [...draft.selectedCodes, "Reports.View"] },
    implicitAll: false,
    catalogCodes: [],
  }),
  { effective: 5, inherited: 3, direct: 2 },
  "既继承又直接授权的，只算继承",
);
assert.deepEqual(
  summarizeAccessPermissionCounts({
    draft,
    implicitAll: true,
    catalogCodes: ["Reports.View", "Staff.Review", "PosTerminal.Sales.AddItem"],
  }),
  { effective: 2, inherited: 3, direct: 2 },
  "管理员默认拥有：有效数取目录里的全部（不含收银权限）",
);

// ───────────── 改动汇总 ─────────────
const stores = [
  { storeGUID: "guid-b1", isPrimary: false },
  { storeGUID: "guid-l1", isPrimary: false },
];
const noChange = buildAccessChangeSummary({
  storeAssignments: stores,
  baselineStoreAssignments: stores,
  roleOptions: [staff, finance],
  selectedRoleGuids: ["storestaff"],
  baselineRoleGuids: ["storestaff"],
  permissionDraft: { baselineCodes: ["A"], selectedCodes: ["A"], inheritedPermissionCodes: [] },
});
assert.equal(noChange.total, 0);
assert.deepEqual(noChange.items, []);
assert.equal(noChange.managerDerived, null);

const changed = buildAccessChangeSummary({
  // Bankstown 查看→管理；Liverpool 查看→未分配；Parramatta 新分配为查看。
  storeAssignments: [
    { storeGUID: "guid-b1", isPrimary: true },
    { storeGUID: "guid-p1", isPrimary: false },
  ],
  baselineStoreAssignments: stores,
  roleOptions: [manager, staff, finance],
  selectedRoleGuids: ["storestaff", "finance"],
  baselineRoleGuids: ["storestaff"],
  permissionDraft: {
    baselineCodes: ["Staff.Review", "Cash.View"],
    selectedCodes: ["Staff.Review", "Reports.Hours.View"],
    inheritedPermissionCodes: [],
  },
});
assert.equal(changed.total, 6, "分店 3 + 角色 1 + 权限 2（新增 1 个、移除 1 个）各算一项");
assert.deepEqual(changed.counts, { stores: 3, roles: 1, permissions: 2 });
assert.deepEqual(changed.items[0], { section: "stores", storeGuid: "guid-b1", from: "view", to: "manage" });
assert.deepEqual(changed.items[1], { section: "stores", storeGuid: "guid-l1", from: "view", to: "unassigned" });
assert.deepEqual(changed.items[2], { section: "stores", storeGuid: "guid-p1", from: "unassigned", to: "view" });
assert.deepEqual(changed.items[3], { section: "roles", roleGuid: "finance", roleName: "Finance", change: "added" });
assert.deepEqual(changed.items[4], { section: "permissions", code: "Reports.Hours.View", change: "added" });
assert.deepEqual(changed.items[5], { section: "permissions", code: "Cash.View", change: "removed" });
assert.equal(changed.managerDerived, "gain", "出现「管理」分店 → 店长角色自动获得（派生，不计入改动数）");

assert.equal(
  buildAccessChangeSummary({
    storeAssignments: [{ storeGUID: "guid-b1", isPrimary: false }],
    baselineStoreAssignments: [{ storeGUID: "guid-b1", isPrimary: true }],
    roleOptions: [],
    selectedRoleGuids: [],
    baselineRoleGuids: [],
    permissionDraft: null,
  }).managerDerived,
  "lose",
);
// 店长看不到的分店（不在可见集合里）发生的变化不计入。
assert.equal(
  buildAccessChangeSummary({
    storeAssignments: [{ storeGUID: "guid-hidden", isPrimary: false }],
    baselineStoreAssignments: [],
    visibleStoreGuids: new Set(["guid-b1"]),
    roleOptions: [],
    selectedRoleGuids: [],
    baselineRoleGuids: [],
    permissionDraft: null,
  }).total,
  0,
);

const described = describeAccessChanges(
  changed.items,
  {
    store: (item) => `${item.storeGuid}：${item.from} → ${item.to}`,
    role: (item) => `角色 ${item.roleName} ${item.change}`,
    permission: (item) => `权限 ${item.code} ${item.change}`,
  },
  2,
);
assert.deepEqual(described.parts, ["guid-b1：view → manage", "guid-l1：view → unassigned"]);
assert.equal(described.hiddenCount, 4);
assert.deepEqual(
  describeAccessChanges([], { store: () => "", role: () => "", permission: () => "" }),
  { parts: [], hiddenCount: 0 },
);

// ───────────── 菜单分段（管理员） ─────────────
const menuDefinitions = [
  { key: "settings", title: "我的", platform: "mobile" as const, permissionCodes: [], fixed: true },
  { key: "reports", title: "报表", platform: "mobile" as const, permissionCodes: ["Reports.View"] },
  { key: "reports", title: "报表(重复)", platform: "mobile" as const, permissionCodes: ["Reports.View"] },
  { key: "/system/users", title: "用户管理", platform: "web" as const, permissionCodes: ["Users.View"] },
  { key: "device-management", title: "设备管理", platform: "mobile" as const, permissionCodes: ["Orders.View"] },
];
const menuModel = buildAccessMenuModel({
  definitions: menuDefinitions,
  draft: {
    baselineCodes: ["Orders.View"],
    selectedCodes: ["Orders.View"],
    inheritedPermissionCodes: ["Reports.View"],
  },
  assignableCodes: ["Orders.View", "Reports.View"],
  state: { isSuperAdmin: false, implicitAllPermissions: false },
});
assert.deepEqual(menuModel.items.map((item) => item.key), ["settings", "reports", "device-management"], "只取移动端菜单并按 key 去重");
assert.equal(menuModel.items[0].visible, true, "固定入口始终可见");
assert.equal(menuModel.items[0].locked, true, "固定入口不能改");
assert.equal(menuModel.items[1].visible, true, "角色继承的菜单可见");
assert.equal(menuModel.items[1].locked, true, "角色继承的菜单要在角色里改");
assert.equal(menuModel.items[2].visible, true, "直接授权的菜单可见");
assert.equal(menuModel.items[2].locked, false, "直接授权且可分配的菜单可以关");
assert.equal(menuModel.openCount, 3);
assert.deepEqual(menuModel.sections.bottom.map((item) => item.key), ["settings"]);
assert.deepEqual(menuModel.sections.reports.map((item) => item.key), ["reports"]);
assert.deepEqual(menuModel.sections.operations.map((item) => item.key), ["device-management"]);
assert.deepEqual(
  buildAccessMenuModel({ definitions: menuDefinitions, draft: null, assignableCodes: [], state: null }).items,
  [],
  "权限数据没加载出来时不给菜单",
);

// ───────────── 保存顺序与部分失败 ─────────────
assert.deepEqual([...ACCESS_SAVE_ORDER], ["stores", "roles", "permissions"]);
assert.deepEqual(planAccessSaveSteps({ permissions: true, stores: true }), ["stores", "permissions"], "固定顺序：分店 → 角色 → 直接权限，只挑有改动的");
assert.deepEqual(planAccessSaveSteps({ roles: false }), []);

function recorder(failOn: string[] = []) {
  const log: string[] = [];
  const step = (key: AccessSaveStep["key"]): AccessSaveStep => ({
    key,
    run: async () => {
      log.push(key);
      if (failOn.includes(key)) throw new Error(`${key} failed`);
    },
  });
  return { log, step };
}

async function runSaveSequenceTests() {
  {
    const { log, step } = recorder();
    const outcome = await runAccessSaveSteps([step("stores"), step("roles"), step("permissions")]);
    assert.deepEqual(log, ["stores", "roles", "permissions"], "按给定顺序依次提交");
    assert.deepEqual(outcome, { saved: ["stores", "roles", "permissions"], failed: null, notRun: [], aborted: false });
    assert.equal(classifyAccessSaveOutcome(outcome), "success");
  }
  {
    // 中间一步失败：停下，前面成功的记为已保存，后面的不再执行，错误原样交回。
    const { log, step } = recorder(["roles"]);
    const outcome = await runAccessSaveSteps([step("stores"), step("roles"), step("permissions")]);
    assert.deepEqual(log, ["stores", "roles"], "失败后不再执行后面的步骤");
    assert.deepEqual(outcome.saved, ["stores"]);
    assert.equal(outcome.failed?.key, "roles");
    assert.equal((outcome.failed?.error as Error).message, "roles failed", "错误不能被吞");
    assert.deepEqual(outcome.notRun, ["permissions"]);
    assert.equal(classifyAccessSaveOutcome(outcome), "partial");
  }
  {
    const { log, step } = recorder(["stores"]);
    const outcome = await runAccessSaveSteps([step("stores"), step("permissions")]);
    assert.deepEqual(log, ["stores"]);
    assert.deepEqual(outcome.saved, []);
    assert.deepEqual(outcome.notRun, ["permissions"]);
    assert.equal(classifyAccessSaveOutcome(outcome), "failed", "第一步就失败 = 全部失败，一项都没保存");
  }
  {
    // 保存途中登录身份变化：余下步骤放弃，不再用别的身份继续写。
    const { log, step } = recorder();
    let calls = 0;
    const outcome = await runAccessSaveSteps([step("stores"), step("roles")], {
      isCurrent: () => {
        calls += 1;
        return calls <= 1;
      },
    });
    assert.deepEqual(log, ["stores"]);
    assert.deepEqual(outcome.saved, ["stores"]);
    assert.deepEqual(outcome.notRun, ["roles"]);
    assert.equal(outcome.aborted, true);
    assert.equal(classifyAccessSaveOutcome(outcome), "aborted");
  }
  assert.equal(
    classifyAccessSaveOutcome(await runAccessSaveSteps([])),
    "nothing",
    "没有任何改动时什么都不提交",
  );

  // 部分失败要明确告诉用户：哪些已保存、哪一步失败（含原因）、哪些没执行，以及草稿仍保留可重试。
  const reportText = {
    sectionLabel: (key: AccessSaveStep["key"]) => ({ stores: "分店", roles: "角色", permissions: "直接权限" })[key],
    join: (items: string[]) => items.join("、"),
    savedLine: (sections: string) => `已保存：${sections}`,
    failedLine: (section: string, reason: string) => `保存失败：${section}（${reason}）`,
    notRunLine: (sections: string) => `未执行：${sections}`,
    retryHint: "未保存的改动仍然保留",
    reasonOf: (error: unknown) => (error as Error).message,
  };
  {
    const { step } = recorder(["roles"]);
    const outcome = await runAccessSaveSteps([step("stores"), step("roles"), step("permissions")]);
    assert.deepEqual(buildAccessSaveReportLines(outcome, reportText), [
      "已保存：分店",
      "保存失败：角色（roles failed）",
      "未执行：直接权限",
      "未保存的改动仍然保留",
    ]);
  }
  {
    const { step } = recorder(["stores"]);
    const outcome = await runAccessSaveSteps([step("stores")]);
    assert.deepEqual(
      buildAccessSaveReportLines(outcome, reportText),
      ["保存失败：分店（stores failed）", "未保存的改动仍然保留"],
      "第一步失败：没有「已保存」行，也没有「未执行」行",
    );
  }
  {
    const { step } = recorder();
    const outcome = await runAccessSaveSteps([step("stores"), step("roles")]);
    assert.deepEqual(buildAccessSaveReportLines(outcome, reportText), [], "全部成功不需要失败说明");
  }
}

runSaveSequenceTests()
  .then(() => console.log("access-page-logic.test.ts: ok"))
  .catch((error) => {
    console.error(error);
    process.exit(1);
  });
