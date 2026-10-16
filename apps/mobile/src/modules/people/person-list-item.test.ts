import type { IdentityUser } from "../identity-admin/types";
import type { StoreUserListItem } from "../users/types";
import {
  buildIdentityPersonItem,
  buildStaffPersonItem,
  formatPhoneForList,
  parseBackendUtc,
  pickPrimaryRole,
  resolveIdentityExceptionTags,
  resolveRelativeLogin,
  resolveRoleTone,
  resolveStaffExceptionTags,
  summarizeUserStores,
  type IdentityPersonFormatters,
} from "./person-list-item";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

function staff(overrides: Partial<StoreUserListItem> = {}): StoreUserListItem {
  return {
    userGUID: "u-1",
    username: "emily.chen",
    fullName: "Emily Chen",
    givenName: "Emily",
    familyName: "Chen",
    nickname: "Em",
    email: "emily@example.com",
    phone: "0412345678",
    status: 1,
    roleNames: ["StoreStaff"],
    ...overrides,
  };
}

function identity(overrides: Partial<IdentityUser> = {}): IdentityUser {
  return {
    userGUID: "g-1",
    username: "grace.liu",
    email: "grace@example.com",
    fullName: "Grace Liu",
    isActive: true,
    createdAt: "2026-01-01T00:00:00",
    updatedAt: "2026-01-01T00:00:00",
    roleNames: ["Admin"],
    storeNames: [],
    roles: [],
    stores: [],
    permissions: [],
    exactPermissions: [],
    ...overrides,
  };
}

// ── 手机号展示 ──
assertEqual(formatPhoneForList("0412345678"), "0412 345 678", "澳洲手机号加空格");
assertEqual(formatPhoneForList("0412 345 678"), "0412 345 678", "已带空格也规整");
assertEqual(formatPhoneForList("+61 412 345 678"), "+61 412 345 678", "其它格式原样");
assertEqual(formatPhoneForList(undefined), "", "缺省为空串");

// ── 店员例外标签：正常账号不挂标签 ──
assertEqual(resolveStaffExceptionTags(staff()), [], "正常员工没有标签");
assertEqual(resolveStaffExceptionTags(staff({ mustChangePassword: true })), ["pendingFirstLogin"], "待首次登录");
assertEqual(resolveStaffExceptionTags(staff({ loginEnabled: false })), ["cannotLogin"], "不能登录");
assertEqual(resolveStaffExceptionTags(staff({ loginEnabled: true })), [], "允许登录不挂标签");
assertEqual(resolveStaffExceptionTags(staff({ email: "emily@hb1008.store.local" })), ["noEmail"], "占位邮箱 = 无邮箱");
assertEqual(resolveStaffExceptionTags(staff({ email: undefined })), ["noEmail"], "空邮箱 = 无邮箱");
assertEqual(
  resolveStaffExceptionTags(staff({ mustChangePassword: true, loginEnabled: false, email: "" })),
  ["pendingFirstLogin", "cannotLogin", "noEmail"],
  "多个例外按固定顺序叠加",
);
assertEqual(
  resolveStaffExceptionTags(staff({ status: 0, mustChangePassword: true, loginEnabled: false, email: "" })),
  ["disabled"],
  "已停用只给「已停用」，不叠加其它标签",
);

// ── 店员适配 ──
{
  const item = buildStaffPersonItem(staff(), { isSelf: false, storeLabel: null, typeLabel: "临时工" });
  assertEqual(item.key, "u-1", "key");
  assertEqual(item.name, "Emily Chen", "主标题");
  assertEqual(item.nickname, "Em", "昵称小字");
  assertEqual(item.initials, "EC", "头像字母");
  assertEqual(item.meta, "@emily.chen · 临时工 · 0412 345 678", "元信息：@账号 · 类型 · 电话");
  assertEqual(item.tags, [], "正常员工无标签");
  assertEqual(item.inactive, false, "启用中");
  assertEqual(item.badge, null, "店员列表没有角色徽标");
}
{
  const item = buildStaffPersonItem(staff({ phone: undefined }), { isSelf: false, storeLabel: "Bankstown", typeLabel: "全职" });
  assertEqual(item.meta, "@emily.chen · Bankstown · 全职", "全部分店模式带分店，无电话则省略");
}
{
  const item = buildStaffPersonItem(
    staff({ userGUID: "me", username: "xiaoming.li", fullName: "李小明", givenName: undefined, familyName: undefined, nickname: undefined, email: "", phone: undefined, mustChangePassword: true }),
    { isSelf: true, storeLabel: null, typeLabel: "店长" },
  );
  assertEqual(item.name, "李小明", "中文全名");
  assertEqual(item.initials, "李", "中文头像取首字");
  assertEqual(item.meta, "@xiaoming.li · 店长", "本人行显示角色");
  assertEqual(item.tags, [], "本人行不挂例外标签（不可操作）");
  assertEqual(item.isSelf, true, "本人标记");
}
{
  const item = buildStaffPersonItem(staff({ status: 0 }), { isSelf: false, storeLabel: null, typeLabel: "临时工" });
  assertEqual(item.inactive, true, "停用账号置灰");
  assertEqual(item.tags, ["disabled"], "停用标签");
}

// ── 角色颜色与主角色 ──
assertEqual(resolveRoleTone("Admin"), "violet", "管理员紫");
assertEqual(resolveRoleTone("超级管理员"), "violet", "超级管理员紫");
assertEqual(resolveRoleTone("StoreManager"), "blue", "店长蓝");
assertEqual(resolveRoleTone("店长"), "blue", "中文店长蓝");
assertEqual(resolveRoleTone("WarehouseManager"), "amber", "仓库经理橙");
assertEqual(resolveRoleTone("仓库经理"), "amber", "中文仓库经理橙");
assertEqual(resolveRoleTone("StoreStaff"), "gray", "店员灰");
assertEqual(resolveRoleTone("订货员"), "gray", "订货员灰");
assertEqual(pickPrimaryRole([]), null, "没有角色没有徽标");
assertEqual(pickPrimaryRole(["StoreStaff", "StoreManager"])?.roleName, "StoreManager", "多角色取权限最高者");
assertEqual(pickPrimaryRole(["StoreStaff", "Admin", "StoreManager"])?.tone, "violet", "管理员优先");
assertEqual(pickPrimaryRole(["Order", "StoreStaff"])?.roleName, "Order", "同级取第一个");
assertEqual(pickPrimaryRole(["  ", ""]), null, "空白角色忽略");

// ── 分店摘要 ──
assertEqual(summarizeUserStores(identity({ roleNames: ["Admin"] })), { scope: "all" }, "管理员=全部分店");
assertEqual(summarizeUserStores(identity({ roleNames: ["StoreStaff"] })), { scope: "none" }, "没有分店");
assertEqual(
  summarizeUserStores(identity({
    roleNames: ["StoreManager"],
    stores: [
      { storeGUID: "s1", storeName: "Parramatta", storeCode: "1003", isPrimary: false },
      { storeGUID: "s2", storeName: "Bankstown", storeCode: "1008", isPrimary: true },
      { storeGUID: "s3", storeName: "Liverpool", storeCode: "1012", isPrimary: false },
    ],
  })),
  { scope: "stores", firstName: "Bankstown", relation: "manage", extraCount: 2 },
  "管理的分店排最前，其余计入 +N",
);
assertEqual(
  summarizeUserStores(identity({
    roleNames: ["StoreStaff"],
    stores: [{ storeGUID: "s1", storeName: "Bankstown", storeCode: "1008", isPrimary: false }],
  })),
  { scope: "stores", firstName: "Bankstown", relation: "view", extraCount: 0 },
  "只关联 = 查看",
);
assertEqual(
  summarizeUserStores(identity({ roleNames: ["StoreStaff"], storeNames: ["Bankstown", "Liverpool"] })),
  { scope: "stores", firstName: "Bankstown", relation: null, extraCount: 1 },
  "只有分店名（旧数据）不判断管理 / 查看",
);

// ── 相对时间 ──
const now = Date.parse("2026-10-16T10:00:00Z");
assertEqual(resolveRelativeLogin(undefined, now), null, "没有登录时间 = 从未登录");
assertEqual(resolveRelativeLogin("not a date", now), null, "无法解析 = 从未登录");
assertEqual(resolveRelativeLogin("2026-10-16T09:59:40Z", now), { unit: "justNow" }, "半分钟内刚刚");
assertEqual(resolveRelativeLogin("2026-10-16T10:05:00Z", now), { unit: "justNow" }, "时钟偏快按刚刚");
assertEqual(resolveRelativeLogin("2026-10-16T09:15:00Z", now), { unit: "minutes", count: 45 }, "45 分钟前");
assertEqual(resolveRelativeLogin("2026-10-16T07:30:00Z", now), { unit: "hours", count: 2 }, "2 小时前");
assertEqual(resolveRelativeLogin("2026-10-15T08:00:00Z", now), { unit: "yesterday" }, "26 小时前 = 昨天");
assertEqual(resolveRelativeLogin("2026-10-13T10:00:00Z", now), { unit: "days", count: 3 }, "3 天前");
assertEqual(resolveRelativeLogin("2026-09-01T10:00:00Z", now), { unit: "days", count: 45 }, "45 天前");
assertEqual(resolveRelativeLogin("2026-07-01T10:00:00Z", now), { unit: "months", count: 3 }, "超过 60 天按月");
assertEqual(resolveRelativeLogin("2024-10-01T10:00:00Z", now), { unit: "years", count: 2 }, "超过一年按年");
// 缺少时区标记按 UTC 解释，不能随设备时区漂移。
assertEqual(parseBackendUtc("2026-10-16T09:00:00"), Date.parse("2026-10-16T09:00:00Z"), "无时区标记补 Z");
assertEqual(parseBackendUtc("2026-10-16T09:00:00+11:00"), Date.parse("2026-10-15T22:00:00Z"), "带偏移按偏移");
assertEqual(resolveRelativeLogin("2026-10-16T07:30:00", now), { unit: "hours", count: 2 }, "无时区标记的相对时间也按 UTC");

// ── 管理员列表适配 ──
const formatters: IdentityPersonFormatters = {
  roleName: (name) => `角色:${name}`,
  stores: (summary) =>
    summary.scope === "all" ? "全部分店"
      : summary.scope === "none" ? "未分配分店"
      : `${summary.firstName}${summary.relation === "manage" ? " 管理" : summary.relation === "view" ? " 查看" : ""}${summary.extraCount ? ` +${summary.extraCount}` : ""}`,
  lastLogin: (login) => (login === null ? "从未登录" : login.unit === "justNow" ? "刚刚登录" : `登录:${login.unit}`),
};
{
  const item = buildIdentityPersonItem(
    identity({ lastLoginAt: "2026-10-16T09:59:50Z", roleNames: ["Admin"] }),
    formatters,
    now,
  );
  assertEqual(item.meta, "@grace.liu · 全部分店 · 刚刚登录", "管理员元信息");
  assertEqual(item.badge, { label: "角色:Admin", tone: "violet" }, "管理员徽标紫色");
  assertEqual(item.tags, [], "正常账号无标签");
  assertEqual(item.name, "Grace Liu", "姓名");
}
{
  const item = buildIdentityPersonItem(
    identity({
      userGUID: "g-2",
      username: "wei.chen",
      fullName: "陈伟",
      isActive: false,
      roleNames: ["StoreStaff"],
      stores: [{ storeGUID: "s1", storeName: "Bankstown", storeCode: "1008", isPrimary: false }],
      lastLoginAt: undefined,
    }),
    formatters,
    now,
  );
  assertEqual(item.inactive, true, "停用账号置灰");
  assertEqual(item.tags, ["disabled"], "停用标签");
  assertEqual(item.meta, "@wei.chen · Bankstown 查看 · 从未登录", "元信息含分店关系与从未登录");
  assertEqual(item.badge, { label: "角色:StoreStaff", tone: "gray" }, "店员灰色徽标");
  assertEqual(item.initials, "陈", "中文首字头像");
}
{
  const item = buildIdentityPersonItem(
    identity({ roleNames: [], email: "placeholder@hb1008.store.local" }),
    formatters,
    now,
  );
  assertEqual(item.badge, null, "没有角色没有徽标");
  assertEqual(item.tags, ["noEmail"], "占位邮箱提示无邮箱");
}
assertEqual(resolveIdentityExceptionTags({ isActive: true, email: "a@b.com" }), [], "有邮箱启用无标签");
assertEqual(resolveIdentityExceptionTags({ isActive: false, email: "" }), ["disabled"], "停用只给已停用");

// ── 管理员列表「不能登录」例外标签：只有 loginEnabled === false 才标，缺省（未知）和 true 都不标 ──
assertEqual(resolveIdentityExceptionTags({ isActive: true, email: "a@b.com", loginEnabled: false }), ["cannotLogin"], "不能登录");
assertEqual(resolveIdentityExceptionTags({ isActive: true, email: "a@b.com", loginEnabled: true }), [], "允许登录不挂标签");
assertEqual(resolveIdentityExceptionTags({ isActive: true, email: "a@b.com", loginEnabled: undefined }), [], "缺省 = 未知（旧后端），不标");
assertEqual(resolveIdentityExceptionTags({ isActive: true, email: "", loginEnabled: false }), ["cannotLogin", "noEmail"], "不能登录在无邮箱之前，与店员列表同序");
assertEqual(resolveIdentityExceptionTags({ isActive: false, email: "", loginEnabled: false }), ["disabled"], "已停用只给已停用，不叠加不能登录");
{
  const item = buildIdentityPersonItem(identity({ loginEnabled: false, roleNames: ["StoreManager"] }), formatters, now);
  assertEqual(item.tags, ["cannotLogin"], "被限制登录的店长账号（kevin 的情形）在管理员列表里标出");
  assertEqual(item.inactive, false, "不能登录不等于停用：头像不置灰");
  assertEqual(buildIdentityPersonItem(identity({ loginEnabled: true }), formatters, now).tags, [], "正常账号仍无标签");
  assertEqual(buildIdentityPersonItem(identity(), formatters, now).tags, [], "接口没返回 loginEnabled 时不标");
}

console.log("person-list-item.test.ts: ok");
