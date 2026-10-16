import assert from "node:assert/strict";
import {
  buildCopyConfirmRows,
  buildPermissionCopyInput,
  createPermissionNameResolver,
  filterPermissionCopyCandidates,
  isPermissionCopyPreviewCurrent,
  listCopySkippedPermissionNames,
  resolveCopyStoreFilter,
  resolveCopyTargetName,
  summarizePermissionCopyPreview,
  togglePermissionCopySelection,
} from "./access-permission-copy";
import type { UserDirectPermissionCopyResult } from "./access-management-types";
import type { StoreUserListItem } from "./types";

function user(overrides: Partial<StoreUserListItem> & { userGUID: string }): StoreUserListItem {
  return { username: overrides.userGUID, status: 1, roleNames: ["StoreStaff"], ...overrides };
}

// 候选人：排除当前员工、操作者本人、已停用、店长/管理员、重复行；支持搜索。
const users: StoreUserListItem[] = [
  user({ userGUID: "CUR-1", fullName: "Amy Chen" }),
  user({ userGUID: "me-1", fullName: "Lisa Ma", roleNames: ["StoreManager"] }),
  user({ userGUID: "ben-1", fullName: "Ben Wu", username: "ben@x.com" }),
  user({ userGUID: "cara-1", fullName: "Cara Liu", email: "cara@mail.com" }),
  user({ userGUID: "david-1", fullName: "David Kim", status: 0 }),
  user({ userGUID: "boss-2", fullName: "Other Manager", roleNames: ["店长"] }),
  user({ userGUID: "flag-1", fullName: "Flagged Manager", isStoreManager: true }),
  user({ userGUID: "admin-1", fullName: "Admin", roleNames: ["Admin"] }),
  user({ userGUID: "ben-1", fullName: "Ben Wu Duplicate" }),
];
const input = { currentEmployeeGuid: "cur-1", actorUserGuid: "ME-1" };
assert.deepEqual(
  filterPermissionCopyCandidates(users, input).map((item) => item.userGUID),
  ["ben-1", "cara-1"],
  "候选人只含同店在职店员：排除当前员工、操作者本人、已停用、各类店长/管理员与重复行（GUID 忽略大小写）",
);
assert.deepEqual(
  filterPermissionCopyCandidates(users, { ...input, query: "CARA@mail" }).map((item) => item.userGUID),
  ["cara-1"],
  "搜索支持姓名、用户名与邮箱，忽略大小写",
);
assert.deepEqual(filterPermissionCopyCandidates(users, { ...input, query: "nobody" }), []);
assert.deepEqual(
  filterPermissionCopyCandidates([user({ userGUID: "x", roleNames: [] })], { currentEmployeeGuid: "cur" }).length,
  1,
  "没有角色信息的在职员工仍可作为候选人",
);

// 选择：复制给他人可多选，从同事复制只能单选且再次点击取消。
assert.deepEqual(togglePermissionCopySelection([], "a", "toOthers"), ["a"]);
assert.deepEqual(togglePermissionCopySelection(["a"], "B", "toOthers"), ["a", "B"]);
assert.deepEqual(togglePermissionCopySelection(["a", "b"], "A", "toOthers"), ["b"], "取消勾选忽略 GUID 大小写");
assert.deepEqual(togglePermissionCopySelection(["a"], "b", "fromColleague"), ["b"], "单选会替换原选择");
assert.deepEqual(togglePermissionCopySelection(["a"], "A", "fromColleague"), [], "单选再次点击取消");
assert.deepEqual(togglePermissionCopySelection(["a"], "  ", "toOthers"), ["a"], "空 GUID 忽略");

// 请求方向：toOthers 来源=当前员工；fromColleague 来源=所选同事、目标只有当前员工。
assert.deepEqual(buildPermissionCopyInput("toOthers", "cur", ["a", "b", "a"], "merge"), {
  sourceUserGuid: "cur",
  targetUserGuids: ["a", "b"],
  mode: "merge",
});
assert.deepEqual(buildPermissionCopyInput("fromColleague", "cur", ["a", "b"], "replace"), {
  sourceUserGuid: "a",
  targetUserGuids: ["cur"],
  mode: "replace",
}, "从同事复制只取第一个所选同事作为来源");
assert.equal(buildPermissionCopyInput("toOthers", "cur", [], "merge"), null, "未选人不生成请求");
assert.equal(buildPermissionCopyInput("toOthers", "cur", ["CUR"], "merge"), null, "不能复制给自己");

// 预览汇总。
const preview: UserDirectPermissionCopyResult = {
  sourceUserGuid: "cur",
  mode: "merge",
  preview: true,
  sourceDirectPermissionCodes: ["A", "B", "C", "D", "E", "F"],
  transferablePermissionCodes: ["A", "B", "C", "D", "E"],
  skippedPermissionCodes: ["F"],
  targets: [
    { userGuid: "ben-1", added: ["A", "B", "C", "D"], removed: [], unchangedCount: 1 },
    { userGuid: "cara-1", added: ["A"], removed: ["Z"], unchangedCount: 4 },
    { userGuid: "dan-1", added: [], removed: [], unchangedCount: 5 },
  ],
};
const summary = summarizePermissionCopyPreview(preview);
assert.equal(summary.sourceCount, 6);
assert.equal(summary.skippedCount, 1);
assert.equal(summary.totalAdded, 5);
assert.equal(summary.totalRemoved, 1);
assert.deepEqual(summary.changedTargets.map((item) => item.userGuid), ["ben-1", "cara-1"]);
assert.equal(summary.hasChanges, true);
assert.equal(
  summarizePermissionCopyPreview({ ...preview, targets: [preview.targets[2]] }).hasChanges,
  false,
  "所有目标都无变化时不允许提交",
);

// 预览必须与当前选择一致才可提交。
const currentInput = { sourceUserGuid: "CUR", targetUserGuids: ["BEN-1", "cara-1", "dan-1"], mode: "merge" as const };
assert.equal(isPermissionCopyPreviewCurrent(preview, currentInput), true, "来源/目标集合/方式一致（忽略大小写与顺序）");
assert.equal(isPermissionCopyPreviewCurrent(preview, { ...currentInput, mode: "replace" }), false, "方式变化后旧预览作废");
assert.equal(isPermissionCopyPreviewCurrent(preview, { ...currentInput, targetUserGuids: ["ben-1"] }), false, "目标变化后旧预览作废");
assert.equal(isPermissionCopyPreviewCurrent(preview, { ...currentInput, sourceUserGuid: "other" }), false, "来源变化后旧预览作废");
assert.equal(isPermissionCopyPreviewCurrent(null, currentInput), false);
assert.equal(isPermissionCopyPreviewCurrent(preview, null), false);

// 权限名解析：目录里找得到用名称（忽略大小写），找不到显示 code。
const resolveName = createPermissionNameResolver([
  { code: "Attendance.Leave.Create", name: "登记请假" },
  { code: "Warehouse.View", name: "" },
]);
assert.equal(resolveName("attendance.leave.create"), "登记请假");
assert.equal(resolveName("Unknown.Code"), "Unknown.Code", "目录里没有时回退为 code");
assert.equal(resolveName("Warehouse.View"), "Warehouse.View", "名称为空时回退为 code");

// 目标展示名：映射缺失时退回 GUID。
const names = new Map([["ben-1", "Ben Wu"]]);
assert.equal(resolveCopyTargetName(names, "BEN-1"), "Ben Wu");
assert.equal(resolveCopyTargetName(names, "zzz"), "zzz");

// 搜索：姓 / 名 / 昵称也能命中（走 buildPersonSearchText）。
const nameUsers: StoreUserListItem[] = [
  user({ userGUID: "n1", fullName: "Emily Rose Chen", givenName: "Emily", middleName: "Rose", familyName: "Chen", nickname: "Em" }),
  user({ userGUID: "n2", fullName: "王芳", givenName: "芳", familyName: "王" }),
];
assert.deepEqual(
  filterPermissionCopyCandidates(nameUsers, { currentEmployeeGuid: "x", query: "em" }).map((item) => item.userGUID),
  ["n1"],
  "昵称 / 名可搜",
);
assert.deepEqual(
  filterPermissionCopyCandidates(nameUsers, { currentEmployeeGuid: "x", query: "rose" }).map((item) => item.userGUID),
  ["n1"],
  "中间名可搜",
);
assert.deepEqual(
  filterPermissionCopyCandidates(nameUsers, { currentEmployeeGuid: "x", query: "王" }).map((item) => item.userGUID),
  ["n2"],
);

// 确认页逐人行：有变化的排前面，没变化的沉底；展示名按目录解析。
const confirmNames = new Map([["ben-1", "Ben Wu"], ["cara-1", "Cara Liu"], ["dan-1", "Dan Lee"]]);
const permissionName = createPermissionNameResolver([
  { code: "A", name: "现金管理" },
  { code: "Z", name: "旧权限" },
]);
const rows = buildCopyConfirmRows(
  { ...preview, targets: [preview.targets[2], preview.targets[0], preview.targets[1]] },
  (guid) => resolveCopyTargetName(confirmNames, guid),
  permissionName,
);
assert.deepEqual(rows.map((row) => row.userGuid), ["ben-1", "cara-1", "dan-1"], "有变化的排前面，没变化的排最后");
assert.deepEqual(rows[0].added, ["现金管理", "B", "C", "D"], "增加的权限用展示名，目录里没有的显示 code");
assert.equal(rows[0].added.length, 4, "+4 的数量来自预览结果");
assert.deepEqual(rows[1].removed, ["旧权限"], "覆盖方式下会被移除的也列出来");
assert.equal(rows[2].noChange, true);
assert.equal(rows[2].unchangedCount, 5, "已有全部 5 项，不变");
assert.equal(rows[0].name, "Ben Wu");
assert.deepEqual(listCopySkippedPermissionNames(preview, permissionName), ["F"], "被跳过的项原样列出");

// 候选同事的分店范围：店长只能在自己管理的分店里选；管理员默认目标员工所属分店并可看全部分店。
const mgrOptions = [
  { storeCode: "B1", storeName: "Bankstown" },
  { storeCode: "L1", storeName: "Liverpool" },
];
assert.deepEqual(
  resolveCopyStoreFilter({ isAdmin: false, storeOptions: mgrOptions, targetStoreCodes: ["L1"], routeStoreCode: "" }),
  { options: mgrOptions, initialStoreCode: "L1", allowAll: false },
  "店长没带分店时取目标所属且自己管理的分店",
);
assert.equal(
  resolveCopyStoreFilter({ isAdmin: false, storeOptions: mgrOptions, targetStoreCodes: [], routeStoreCode: "b1" }).initialStoreCode,
  "B1",
  "路由带入的分店优先（忽略大小写，返回目录里的写法）",
);
assert.equal(
  resolveCopyStoreFilter({ isAdmin: false, storeOptions: mgrOptions, targetStoreCodes: ["ZZ"], routeStoreCode: "ZZ" }).initialStoreCode,
  "B1",
  "店长不能选自己不管理的分店，回落到第一个管理分店",
);
assert.deepEqual(
  resolveCopyStoreFilter({ isAdmin: false, storeOptions: [], targetStoreCodes: [], routeStoreCode: "" }),
  { options: [], initialStoreCode: "", allowAll: false },
);
const adminFilter = resolveCopyStoreFilter({ isAdmin: true, storeOptions: mgrOptions, targetStoreCodes: ["L1"], routeStoreCode: "" });
assert.equal(adminFilter.allowAll, true, "管理员可以看全部分店的同事");
assert.equal(adminFilter.initialStoreCode, "L1", "管理员没有 storeCode 时默认目标员工所属分店");
assert.equal(
  resolveCopyStoreFilter({ isAdmin: true, storeOptions: mgrOptions, targetStoreCodes: [], routeStoreCode: "" }).initialStoreCode,
  "",
  "目标没有分店时默认「全部分店」",
);
assert.deepEqual(
  resolveCopyStoreFilter({ isAdmin: true, storeOptions: [], targetStoreCodes: [], routeStoreCode: "X9" }).options,
  [{ storeCode: "X9", storeName: "X9" }],
  "管理员的分店目录没加载出来时，路由带入的分店仍可选",
);

console.log("access-permission-copy.test.ts: ok");
