import assert from "node:assert/strict";
import {
  backToHubTab,
  buildPermissionView,
  buildRoleSaveReport,
  canEditRoleMembers,
  canEditRoleName,
  classifyRole,
  classifyRoleWriteFailure,
  countProfileChanges,
  formatChangeBadge,
  getPermissionDelta,
  isGroupFullySelected,
  resolveFailureReasonKey,
  resolveSaveBarState,
  runRoleSaveSequence,
  summarizeRoleChanges,
  type RoleSaveStep,
} from "./role-detail-logic";
import { createPermissionDraft, togglePermission, togglePermissionCategory } from "./role-logic";

// ───────── 改动汇总 ─────────
const baseline = { roleName: "店员", description: "收银", isActive: true };
assert.equal(countProfileChanges(baseline, baseline), 0);
assert.equal(countProfileChanges({ ...baseline, roleName: " 店员 " }, baseline), 0, "首尾空白不算改动");
assert.equal(countProfileChanges({ roleName: "收银员", description: "", isActive: false }, baseline), 3);

let draft = createPermissionDraft(["Sale.Create", "Sale.Hold", "Reports.View"]);
assert.deepEqual(getPermissionDelta(draft), { added: [], removed: [] });
draft = togglePermission(draft, "Sale.Create"); // 取消
draft = togglePermission(draft, "Sale.PriceOverride"); // 新增
draft = togglePermission(draft, "Reports.Revenue.View"); // 新增
assert.deepEqual(getPermissionDelta(draft), {
  added: ["Reports.Revenue.View", "Sale.PriceOverride"],
  removed: ["Sale.Create"],
});
assert.deepEqual(getPermissionDelta(null), { added: [], removed: [] });
assert.deepEqual(getPermissionDelta({ selected: [" A ", "A", ""], baseline: ["A"] }), { added: [], removed: [] }, "去空白去重");

// 设计稿示例：3 项改动 · 权限 +2 −1
const summary = summarizeRoleChanges({ profileCount: 0, permission: getPermissionDelta(draft) });
assert.deepEqual(summary, { profileCount: 0, permissionAdded: 2, permissionRemoved: 1, total: 3, hasChanges: true });
assert.equal(formatChangeBadge(summary.permissionAdded, summary.permissionRemoved), "+2 −1");
assert.equal(formatChangeBadge(1, 0), "+1");
assert.equal(formatChangeBadge(0, 2), "−2");
assert.equal(formatChangeBadge(0, 0), "");
// 资料改动与权限改动合计
const mixed = summarizeRoleChanges({ profileCount: 2, permission: { added: ["A"], removed: [] } });
assert.equal(mixed.total, 3);
assert.equal(summarizeRoleChanges({ profileCount: 0, permission: { added: [], removed: [] } }).hasChanges, false);

// ───────── 保存栏可用状态 ─────────
const ok = { hasChanges: true, saving: false, frozen: false, profileInvalid: false, canWriteAnything: true };
assert.deepEqual(resolveSaveBarState(ok), { enabled: true, blocker: "none" });
assert.deepEqual(resolveSaveBarState({ ...ok, hasChanges: false }), { enabled: false, blocker: "noChanges" });
assert.deepEqual(resolveSaveBarState({ ...ok, saving: true }), { enabled: false, blocker: "saving" });
assert.deepEqual(resolveSaveBarState({ ...ok, frozen: true }), { enabled: false, blocker: "frozen" });
assert.deepEqual(resolveSaveBarState({ ...ok, profileInvalid: true }), { enabled: false, blocker: "invalid" });
assert.deepEqual(resolveSaveBarState({ ...ok, canWriteAnything: false }), { enabled: false, blocker: "readOnly" });

// ───────── 返回 Hub 页签 ─────────
{
  const trace: string[] = [];
  const makeRouter = (canGoBack: boolean) => ({
    canGoBack: () => canGoBack,
    back: () => { trace.push("back"); },
    replace: (href: unknown) => { trace.push(`replace:${String(href)}`); },
  });
  backToHubTab(makeRouter(true), "roles");
  backToHubTab(makeRouter(false), "roles");
  backToHubTab(makeRouter(false), "permissions");
  assert.deepEqual(trace, ["back", "replace:/users?tab=roles", "replace:/users?tab=permissions"]);
}

// ───────── 系统角色 / 派生角色 ─────────
assert.deepEqual(classifyRole("Admin"), { isSystem: true, isDerived: false });
assert.deepEqual(classifyRole("超级管理员"), { isSystem: true, isDerived: false });
assert.deepEqual(classifyRole(" StoreManager "), { isSystem: false, isDerived: true });
assert.deepEqual(classifyRole("店长"), { isSystem: false, isDerived: true });
assert.deepEqual(classifyRole("店员"), { isSystem: false, isDerived: false });
assert.equal(canEditRoleName(classifyRole("店员")), true);
assert.equal(canEditRoleName(classifyRole("Admin")), false);
assert.equal(canEditRoleName(classifyRole("店长")), false);
assert.equal(canEditRoleMembers(classifyRole("店长")), false, "店长成员由分店派生");
assert.equal(canEditRoleMembers(classifyRole("Admin")), true);
assert.equal(canEditRoleMembers(classifyRole("店员")), true);

// ───────── 权限分类视图：分类计数 / 搜索 / 仅看已选 / 全选本类 ─────────
const categories = [
  {
    category: "Sale",
    displayName: "销售",
    permissions: [
      { name: "PosTerminal.Sale.Create", displayName: "开单收银" },
      { name: "PosTerminal.Sale.Hold", displayName: "挂单与取单" },
      { name: "PosTerminal.Sale.PriceOverride", displayName: "改价" },
    ],
  },
  {
    category: "Reports",
    displayName: "报表",
    permissions: [
      { name: "Reports.Revenue.View", displayName: "营业额报表" },
      { name: "Reports.ProductSales.View", displayName: "商品销售明细", description: "按商品统计" },
    ],
  },
];
const selected = ["PosTerminal.Sale.Create", "PosTerminal.Sale.Hold", "Reports.ProductSales.View", "Legacy.Removed.Code"];

const all = buildPermissionView(categories, { keyword: "", onlySelected: false, selected });
assert.equal(all.total, 5);
assert.equal(all.selected, 3, "目录里不存在的历史权限码不计入已选，避免分子大于分母");
assert.deepEqual(all.groups.map((group) => [group.category, group.selected, group.total, group.permissions.length]), [
  ["Sale", 2, 3, 3],
  ["Reports", 1, 2, 2],
]);

const onlySelected = buildPermissionView(categories, { keyword: "", onlySelected: true, selected });
assert.deepEqual(onlySelected.groups.map((group) => group.permissions.map((permission) => permission.name)), [
  ["PosTerminal.Sale.Create", "PosTerminal.Sale.Hold"],
  ["Reports.ProductSales.View"],
]);
assert.equal(onlySelected.groups[0].total, 3, "仅看已选不改变分类的 已选/总数");
assert.equal(onlySelected.total, 5);

// 搜索：名称、编码、说明、分类名都能命中，大小写不敏感
assert.deepEqual(
  buildPermissionView(categories, { keyword: "revenue", onlySelected: false, selected }).groups.map((group) => group.category),
  ["Reports"],
);
assert.deepEqual(
  buildPermissionView(categories, { keyword: "挂单", onlySelected: false, selected }).groups[0].permissions.map((permission) => permission.name),
  ["PosTerminal.Sale.Hold"],
);
assert.deepEqual(
  buildPermissionView(categories, { keyword: "按商品", onlySelected: false, selected }).groups[0].permissions.map((permission) => permission.name),
  ["Reports.ProductSales.View"],
);
assert.deepEqual(
  buildPermissionView(categories, { keyword: "报表", onlySelected: false, selected }).groups[0].permissions.length,
  2,
  "分类名命中时整类可见",
);
// 搜索与仅看已选叠加；无匹配时没有任何分组
assert.deepEqual(
  buildPermissionView(categories, { keyword: "改价", onlySelected: true, selected }).groups,
  [],
);
assert.equal(buildPermissionView([], { keyword: "", onlySelected: false, selected }).total, 0);

// 全选本类：只作用于「可见」权限；已全选时再点变成清空
const visibleSale = all.groups[0].permissions.map((permission) => permission.name);
assert.equal(isGroupFullySelected(visibleSale, selected), false);
assert.equal(isGroupFullySelected([], selected), false);
let permissionDraft = createPermissionDraft(selected);
permissionDraft = togglePermissionCategory(permissionDraft, visibleSale);
assert.equal(isGroupFullySelected(visibleSale, permissionDraft.selected), true);
assert.deepEqual(getPermissionDelta(permissionDraft).added, ["PosTerminal.Sale.PriceOverride"]);
permissionDraft = togglePermissionCategory(permissionDraft, visibleSale);
assert.equal(isGroupFullySelected(visibleSale, permissionDraft.selected), false);
assert.deepEqual(getPermissionDelta(permissionDraft).removed.sort(), ["PosTerminal.Sale.Create", "PosTerminal.Sale.Hold"]);
// 搜索后只全选命中的那几项，不波及整个分类
const searched = buildPermissionView(categories, { keyword: "改价", onlySelected: false, selected: [] });
const searchedCodes = searched.groups[0].permissions.map((permission) => permission.name);
assert.deepEqual(searchedCodes, ["PosTerminal.Sale.PriceOverride"]);
assert.deepEqual(togglePermissionCategory(createPermissionDraft([]), searchedCodes).selected, ["PosTerminal.Sale.PriceOverride"]);

// ───────── 写入失败归类 ─────────
assert.deepEqual(classifyRoleWriteFailure({ response: { status: 403 } }), { kind: "forbidden", status: 403 });
assert.equal(classifyRoleWriteFailure({ status: 401 }).kind, "forbidden");
assert.equal(classifyRoleWriteFailure({ response: { status: 503 } }).kind, "uncertain");
assert.equal(classifyRoleWriteFailure(new Error("timeout")).kind, "uncertain", "没有服务端结论按结果不明确处理");
assert.deepEqual(classifyRoleWriteFailure({ apiBusinessError: true, code: "ROLE_NAME_EXISTS" }), { kind: "rejected", code: "ROLE_NAME_EXISTS" });
assert.deepEqual(
  classifyRoleWriteFailure({ response: { status: 400, data: { errorCode: "ROLE_NAME_EXISTS" } } }),
  { kind: "rejected", status: 400, code: "ROLE_NAME_EXISTS" },
);
assert.equal(resolveFailureReasonKey({ kind: "forbidden" }), "forbidden");
assert.equal(resolveFailureReasonKey({ kind: "uncertain" }), "uncertainWrite");
assert.equal(resolveFailureReasonKey({ kind: "rejected", code: "ROLE_NAME_EXISTS" }), "nameExists");
assert.equal(resolveFailureReasonKey({ kind: "rejected" }), "saveFailed");

// ───────── 保存顺序与部分失败 ─────────
const calls: string[] = [];
const step = (key: RoleSaveStep["key"], fail?: unknown): RoleSaveStep => ({
  key,
  run: async () => {
    calls.push(key);
    if (fail !== undefined) throw fail;
  },
});

async function main() {
  // 1) 资料 + 权限都成功：先资料后权限
  let outcome = await runRoleSaveSequence([step("profile"), step("permissions")]);
  assert.deepEqual(calls, ["profile", "permissions"]);
  assert.deepEqual(outcome, { completed: ["profile", "permissions"], failed: null, notAttempted: [] });
  let report = buildRoleSaveReport(outcome);
  assert.equal(report.tone, "success");
  assert.equal(report.messageKey, "savedBoth");

  // 2) 只有一类改动
  calls.length = 0;
  outcome = await runRoleSaveSequence([step("permissions")]);
  assert.equal(buildRoleSaveReport(outcome).messageKey, "savedPermissions");
  outcome = await runRoleSaveSequence([step("profile")]);
  assert.equal(buildRoleSaveReport(outcome).messageKey, "savedProfile");

  // 3) 资料失败：权限一步都不能发，且报告写明「权限也没有提交」
  calls.length = 0;
  outcome = await runRoleSaveSequence([step("profile", { apiBusinessError: true, code: "ROLE_NAME_EXISTS" }), step("permissions")]);
  assert.deepEqual(calls, ["profile"], "资料失败后不再发权限请求");
  assert.deepEqual(outcome.completed, []);
  assert.deepEqual(outcome.notAttempted, ["permissions"]);
  report = buildRoleSaveReport(outcome);
  assert.equal(report.tone, "error");
  assert.equal(report.messageKey, "profileFailedPermissionsPending");
  assert.equal(report.failedStep, "profile");
  assert.deepEqual(report.saved, []);
  assert.deepEqual(report.notAttempted, ["permissions"]);
  assert.equal(resolveFailureReasonKey(report.failure!), "nameExists");

  // 4) 资料成功、权限失败：写明资料已保存、权限未保存（草稿由调用方保留）
  calls.length = 0;
  outcome = await runRoleSaveSequence([step("profile"), step("permissions", { response: { status: 503 } })]);
  assert.deepEqual(calls, ["profile", "permissions"]);
  report = buildRoleSaveReport(outcome);
  assert.equal(report.messageKey, "profileSavedPermissionsFailed");
  assert.deepEqual(report.saved, ["profile"]);
  assert.equal(report.failedStep, "permissions");
  assert.equal(report.failure?.kind, "uncertain");

  // 5) 单步失败
  outcome = await runRoleSaveSequence([step("permissions", { response: { status: 403 } })]);
  report = buildRoleSaveReport(outcome);
  assert.equal(report.messageKey, "failedPermissions");
  assert.equal(report.failure?.kind, "forbidden");
  outcome = await runRoleSaveSequence([step("profile", new Error("boom"))]);
  assert.equal(buildRoleSaveReport(outcome).messageKey, "failedProfile");

  // 6) 空步骤不报错
  assert.deepEqual(await runRoleSaveSequence([]), { completed: [], failed: null, notAttempted: [] });

  console.log("role-detail-logic.test.ts: ok");
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
