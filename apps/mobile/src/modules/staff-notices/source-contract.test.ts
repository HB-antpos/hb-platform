import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * 「员工通知」的源码契约：锁住权限写法、接口路径、弹层体系、预检把关这些
 * 纯函数单测盖不到、但改坏了就是线上事故（误重置密码）的保证。
 */
const directory = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(directory, "../../..");
const read = (relative: string) => readFileSync(resolve(mobileRoot, relative), "utf8");

const api = read("src/modules/staff-notices/api.ts");
const hooks = read("src/modules/staff-notices/hooks.ts");
const sendSheet = read("src/modules/staff-notices/send-sheet.tsx");
const review = read("src/modules/staff-notices/review-screen.tsx");
const records = read("src/modules/staff-notices/records-screen.tsx");
const preview = read("src/modules/staff-notices/preview-sheet.tsx");
const progress = read("src/modules/staff-notices/progress-sheet.tsx");
const rules = read("src/modules/people/detail/detail-rules.ts");
const storeDetail = read("src/modules/people/detail/store-person-detail.tsx");
const adminDetail = read("src/modules/people/detail/admin-person-detail.tsx");
const list = read("src/modules/users/store-staff-list.tsx");

// 接口路径：基址已含 /api，这里不能再写一遍。
assert.match(api, /const BASE = "\/staff-notifications"/);
for (const path of ["templates", "preflight", "preview", "send", "batches"]) {
  assert.ok(api.includes(`\${BASE}/${path}`), `缺少接口 ${path}`);
}
assert.match(api, /template\.isEnabled/, "停用的模板不能出现在手机上");

// 权限：客户端不认别名，必须写「全局码 OR 店员码」。
const permissionPattern = /const canResetPasswords = access\.isAdmin\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.ResetPassword\)\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.ResetStoreStaffPassword\)/;
assert.match(review, permissionPattern, "批量核对页的权限写法");
assert.match(records, permissionPattern, "发送记录页的权限写法");
assert.match(list, permissionPattern, "人员列表的权限写法");

// 入口：沿用重置权限；设备模式、iOS 审核、本人、受保护目标都不出现。
assert.match(rules, /const canSendNotice = canResetPasswords\s*&& scope\.accountSession\s*&& !scope\.isDeviceMode\s*&& !isSelf\s*&& !isProtectedTarget\s*&& target\.active/);
assert.match(rules, /if \(permissions\.canSendNotice\) moreItems\.push\("sendNotice"\)/);
for (const detail of [storeDetail, adminDetail]) {
  assert.match(detail, /noticeVisible && permissions\.canSendNotice/, "弹层要再判一次权限");
  assert.match(detail, /key === "sendNotice"\) setNoticeVisible\(true\)/);
}
assert.match(storeDetail, /shouldOfferActivationCard\(\{/, "情境卡片的显示条件走纯函数");
assert.match(list, /const canViewNoticeRecords = sessionKind === "account" && !isDeviceMode && canResetPasswords/);
assert.match(list, /const canBatchNotice = Boolean\(\s*canViewNoticeRecords && managedStoreCode && isStoreManageable\(managedStoreCode, manageableStores\)/);
assert.match(list, /pathname: "\/users\/notices\/send"/);
assert.match(list, /"\/users\/notices"/, "顶栏进入发送记录");

// 弹层：必须走 Paper Portal 体系（PeopleSheet），原生 Modal 会压住 Snackbar / Dialog。
for (const source of [sendSheet, preview, progress]) {
  assert.match(source, /<PeopleSheet/);
  assert.doesNotMatch(source, /BusinessSheet/);
}

// App 里没有 WebView（加原生依赖就走不了 OTA），预览只用纯文本。
assert.match(preview, /preview\.emailText/);
assert.doesNotMatch(`${preview}${api}`, /emailHtml|react-native-webview/);

// 发送把关：预检没结束、不可发送、需确认没勾选都不能发。
assert.match(sendSheet, /status !== "blocked"/);
assert.match(sendSheet, /\(status !== "confirm" \|\| confirmed\)/, "需确认必须勾选");
assert.match(sendSheet, /!checking/, "预检进行中不能发");
assert.match(review, /!preflightQuery\.isFetching/, "预检进行中不能发");
assert.match(review, /disabled=\{pending \|\| \(needsCheck && !checked\)\}/, "有已登录过的人必须勾确认");
assert.match(review, /userGuids: \[\.\.\.selected\]/, "只发勾选的人");

// 预检不缓存；进度弹层卸载即停止轮询，批次结束自动停。
assert.match(hooks, /"preflight", request\][\s\S]*?gcTime: 0,\s*staleTime: 0/);
assert.match(hooks, /poll && !isBatchFinished\(query\.state\.data\) \? STAFF_NOTICE_POLL_MS : false/);
assert.match(hooks, /\["staffNotices", actorKey, "batches"\]/, "发送记录缓存按账号隔离");
