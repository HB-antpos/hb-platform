import {
  buildAccessRoute,
  buildDetailRoute,
  buildEditRoute,
  buildLegacyRedirect,
  buildLoginRecordsRoute,
  buildPosRoute,
} from "./detail-navigation";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

const target = {
  userGuid: "U-1",
  storeCode: "1008",
  userName: "Emily Chen",
  username: "emily.chen",
  active: true,
  roleNames: ["StoreStaff", "店员"],
};

assertEqual(
  buildDetailRoute(target),
  {
    pathname: "/users/[userGuid]",
    params: { userGuid: "U-1", storeCode: "1008", userName: "Emily Chen", username: "emily.chen", targetStatus: "1", roleNames: "StoreStaff|店员" },
  },
  "详情路由带 storeCode 与预填参数",
);
assertEqual(buildDetailRoute({ userGuid: "U-2" }), { pathname: "/users/[userGuid]", params: { userGuid: "U-2" } }, "没有预填时只有 userGuid");

assertEqual(buildEditRoute(target), { pathname: "/users/[userGuid]/edit", params: { userGuid: "U-1", storeCode: "1008" } }, "编辑页只带 storeCode");
assertEqual(buildEditRoute({ userGuid: "U-2" }), { pathname: "/users/[userGuid]/edit", params: { userGuid: "U-2" } }, "管理员版编辑不带 storeCode");

assertEqual(
  buildAccessRoute(target).params,
  { userGuid: "U-1", storeCode: "1008", userName: "Emily Chen", username: "emily.chen", targetStatus: "1", roleNames: "StoreStaff|店员", section: "stores" },
  "授权页默认 section=stores",
);
assertEqual(buildAccessRoute(target, { section: "permissions", copy: true }).params.copy, "1", "复制权限带 copy=1");
assertEqual(buildAccessRoute(target, { section: "permissions", copy: true }).params.section, "permissions", "复制权限落在直接权限段");
assertEqual("copy" in buildAccessRoute(target).params, false, "不复制时不带 copy");
assertEqual(buildAccessRoute({ userGuid: "U-3", active: false }).params.targetStatus, "0", "已停用 targetStatus=0");

{
  const route = buildPosRoute({ ...target, storeCode: "1008" }, { storeGuid: "G-1", storeName: "Bankstown" });
  assertEqual(route.pathname, "/users/[userGuid]/pos-terminal-permissions", "收银权限路由");
  assertEqual(route.params.storeCode, "1008", "收银权限 storeCode 必填");
  assertEqual([route.params.storeGuid, route.params.storeName], ["G-1", "Bankstown"], "能解析到的分店信息一并带上");
  assertEqual("storeGuid" in buildPosRoute({ userGuid: "U-1", storeCode: "1008" }).params, false, "解析不到不带 storeGuid");
}

assertEqual(buildLoginRecordsRoute("U-9"), { pathname: "/users/[userGuid]/login-records", params: { userGuid: "U-9" } }, "登录记录路由");

// 旧路由壳重定向：参数透传
assertEqual(
  buildLegacyRedirect({ userGuid: "U-1", storeCode: "1008", userName: "Emily", roleNames: "StoreStaff" }),
  { pathname: "/users/[userGuid]", params: { userGuid: "U-1", storeCode: "1008", userName: "Emily", roleNames: "StoreStaff" } },
  "旧 staff/[userGuid] 透传 storeCode 与预填参数",
);
assertEqual(
  buildLegacyRedirect({ userGuid: ["U-1", "x"], storeCode: undefined, partial: "" }),
  { pathname: "/users/[userGuid]", params: { userGuid: "U-1" } },
  "数组取首项，空值与 undefined 不透传",
);
assertEqual(
  buildLegacyRedirect({ userGuid: "new", storeCode: "1008" }),
  { pathname: "/users/new", params: { storeCode: "1008" } },
  "user-admin/new 重定向到 /users/new",
);

console.log("detail-navigation.test.ts: ok");
