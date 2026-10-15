import assert from "node:assert/strict";

import { isSelfStaffRow, splitSelfAndStaff } from "./staff-list-scope";
import type { StoreUserListItem } from "./types";

const row = (userGUID: string, roleNames: string[]): StoreUserListItem => ({
  userGUID,
  username: userGUID,
  status: 1,
  roleNames,
});

const manager = row("Manager-1", ["StoreManager"]);
const staffA = row("staff-a", ["StoreStaff"]);
const staffB = row("staff-b", ["StoreStaff"]);

// 本人置顶分组：GUID 比较忽略大小写与首尾空格，其余成员保持原顺序。
const grouped = splitSelfAndStaff([staffA, manager, staffB], " manager-1 ");
assert.equal(grouped.self, manager);
assert.deepEqual(grouped.staff, [staffA, staffB]);

// 列表里没有本人（管理员视角 / 本人不在该分店）时不凭空生成。
assert.equal(splitSelfAndStaff([staffA, staffB], "manager-1").self, null);
assert.equal(splitSelfAndStaff([staffA, staffB], "").self, null);
assert.deepEqual(splitSelfAndStaff([staffA, staffB], "").staff, [staffA, staffB]);

assert.equal(isSelfStaffRow(manager, "manager-1"), true);
assert.equal(isSelfStaffRow(staffA, "manager-1"), false);
assert.equal(isSelfStaffRow(staffA, ""), false);
console.log("staff-list-scope tests passed");
