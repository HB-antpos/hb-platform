import {
  buildPrefillTitle,
  firstRouteParam,
  parseDetailRouteParams,
  personFromIdentityUser,
  personFromStoreProfile,
} from "./person-view";
import type { IdentityUserDetail } from "@/modules/identity-admin/types";
import type { StoreUserProfile } from "@/modules/users/types";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

// ── 路由参数：只作预填，缺失不报错 ──
assertEqual(firstRouteParam([" a ", "b"]), "a", "数组取第一个并去空白");
assertEqual(firstRouteParam("  "), undefined, "空白视为缺失");

assertEqual(
  parseDetailRouteParams({
    userGuid: "U-1",
    storeCode: "1008",
    userName: "Emily Chen",
    username: "emily.chen",
    targetStatus: "1",
    roleNames: "StoreStaff|店员",
  }),
  {
    userGuid: "U-1",
    storeCode: "1008",
    prefill: { userName: "Emily Chen", username: "emily.chen", active: true, roleNames: ["StoreStaff", "店员"] },
  },
  "完整旧参数",
);
assertEqual(
  parseDetailRouteParams({ userGuid: "U-2" }),
  { userGuid: "U-2", storeCode: undefined, prefill: { userName: undefined, username: undefined, active: undefined, roleNames: [] } },
  "深链只有 userGuid：全部预填缺省",
);
assertEqual(parseDetailRouteParams({ userGuid: "U-3", targetStatus: "0" }).prefill.active, false, "targetStatus=0 表示已停用");
assertEqual(buildPrefillTitle({ roleNames: [] }), null, "没有任何预填则没有占位标题");
assertEqual(buildPrefillTitle({ username: "emily", roleNames: [] }), { name: "emily", username: "emily" }, "只有账号时用账号做标题");
assertEqual(buildPrefillTitle({ userName: "Emily Chen", username: "emily", roleNames: [] }), { name: "Emily Chen", username: "emily" }, "优先姓名");

// ── 店长版 profile 适配 ──
const profile: StoreUserProfile = {
  userGUID: "U-1",
  username: "emily.chen",
  fullName: "Emily Chen",
  givenName: "Emily",
  familyName: "Chen",
  nickname: "Em",
  status: 1,
  roleNames: ["StoreStaff"],
  mustChangePassword: true,
  loginEnabled: false,
  lastLoginTime: "2026-10-15T08:00:00Z",
};
const fromStore = personFromStoreProfile(profile);
assertEqual(fromStore.active, true, "status=1 为启用");
assertEqual(fromStore.givenName, "Emily", "姓名拆分字段透传");
assertEqual(fromStore.loginEnabled, false, "loginEnabled 透传");
assertEqual(fromStore.lastLoginAt, "2026-10-15T08:00:00Z", "lastLoginTime 映射到 lastLoginAt");
assertEqual(personFromStoreProfile({ ...profile, status: 0 }).active, false, "status=0 为停用");

// ── 管理员版适配 ──
const identity: IdentityUserDetail = {
  userGUID: "U-9",
  username: "xiaoming.li",
  email: "xm.li@example.com",
  fullName: "李小明",
  isActive: false,
  createdAt: "2025-11-04T00:00:00Z",
  updatedAt: "2026-10-12T00:00:00Z",
  roleNames: ["店长"],
  storeNames: [],
  roles: [],
  stores: [],
  permissions: [],
  exactPermissions: [],
};
const fromIdentity = personFromIdentityUser(identity);
assertEqual(fromIdentity.active, false, "isActive=false 为停用");
assertEqual(fromIdentity.loginEnabled, undefined, "接口没返回 loginEnabled（旧后端 / 未查询）：保持 undefined，界面不标也不显示开关");
assertEqual(personFromIdentityUser({ ...identity, loginEnabled: false }).loginEnabled, false, "管理员详情 loginEnabled=false 透传（界面标「不能登录」）");
assertEqual(personFromIdentityUser({ ...identity, loginEnabled: true }).loginEnabled, true, "管理员详情 loginEnabled=true 透传");
assertEqual(fromIdentity.roleNames, ["店长"], "角色透传");

console.log("person-view.test.ts: ok");
