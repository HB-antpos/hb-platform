// 收银权限页的源码契约测试：锁住改版后仍必须保留的业务保证（权限判断、弹层层级、保存流程、离页保护），
// 以及页面用到的每个 peoplePos 文案键在中英文里都存在。
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import peoplePosEn from "../../locales/en/screens/peoplePos.json";
import peoplePosZh from "../../locales/zh/screens/peoplePos.json";

const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const read = (path: string) => readFileSync(path, "utf8");
const moduleSource = (name: string) => read(resolve(moduleDir, name));
const stripComments = (source: string) =>
  source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");

const screen = stripComments(moduleSource("pos-terminal-permissions-screen.tsx"));
const sheet = stripComments(moduleSource("pos-permission-sheet.tsx"));
const changesSheet = stripComments(moduleSource("pos-permission-changes-sheet.tsx"));
const copySheet = stripComments(moduleSource("pos-permission-copy-sheet.tsx"));
const groupCard = stripComments(moduleSource("pos-permission-group-card.tsx"));
const summaryCard = stripComments(moduleSource("pos-permission-summary-card.tsx"));

// ───────── 1. 路由壳与参数：路径不变，页面仍按 userGuid + storeGuid 调接口 ─────────
assert.equal(
  read(resolve(mobileRoot, "app/(shell)/users/[userGuid]/pos-terminal-permissions.tsx")).trim(),
  'export { default } from "@/modules/users/pos-terminal-permissions-screen";',
  "路由文件只做薄壳转发"
);
for (const param of ["userGuid", "storeGuid", "storeCode", "userName", "storeName"]) {
  assert.match(screen, new RegExp(`${param}\\?: string \\| string\\[\\]`), `路由参数 ${param} 必须保留`);
}
assert.match(screen, /const hasRequiredParams = Boolean\(userGuid && storeGuid\)/, "没有员工或分店标识不能发请求");

// ───────── 2. 权限与资格：只认 ManagePosTerminalPermissions，且不能改本人 ─────────
assert.match(screen, /access\.hasPermission\(\s*PERMISSIONS\.Users\.ManagePosTerminalPermissions\s*\)/);
assert.match(screen, /const queryEnabled = hasRequiredParams && canManage && !isCurrentUser/, "无权限或本人时不拉取数据");
assert.match(screen, /else if \(!canManage \|\| isCurrentUser\)/, "无权限或本人时展示无权限空态而不是编辑器");

// ───────── 3. 保存流程：先关确认弹层再保存；保存只发白名单内的权限 ─────────
{
  const confirm = /const handleConfirmSave = useCallback\(\(\) => \{([\s\S]*?)\}, \[handleSave\]\);/.exec(screen);
  assert.ok(confirm, "必须保留 handleConfirmSave");
  const body = confirm![1];
  assert.ok(
    body.indexOf("setChangesVisible(false)") !== -1 &&
      body.indexOf("setChangesVisible(false)") < body.indexOf("void handleSave()"),
    "先关闭确认弹层再保存：保存失败的 Snackbar 在 Portal 弹层下会被遮住"
  );
  assert.match(
    screen,
    /grantedPermissionCodes: buildGrantedPosPermissionCodes\(\s*selectedCodes,\s*permissionsQuery\.data\.assignablePermissions\s*\)/,
    "保存 payload 必须过 assignablePermissions 白名单"
  );
  assert.match(screen, /operationInFlightRef\.current = true/, "保存与恢复仍用 ref 同步占位防双击");
}

// 吸底只有一个按钮「查看并保存」，打开的就是确认弹层；没有改动或处理中时禁用
assert.match(screen, /disabled=\{!dirty \|\| busy\}[\s\S]*?onPress=\{\(\) => setChangesVisible\(true\)\}/);
assert.match(screen, /t\("actions\.reviewAndSave"\)/);
assert.doesNotMatch(screen, /actions\.reviewChanges|actions\.save"/, "不再有「查看改动」和「保存」两个入口");
assert.equal((screen.match(/setChangesVisible\(true\)/g) ?? []).length, 1, "打开确认弹层只有吸底按钮一个入口");

// ───────── 4. 恢复角色默认：入口在概要卡，只在「本店自定义」出现，二次确认逻辑保留 ─────────
assert.match(summaryCard, /override \? \(\s*<Pressable[\s\S]*?onPress=\{onRestore\}/, "角色默认模式下不显示恢复入口");
assert.match(screen, /onRestore=\{handleRestore\}/);
assert.match(screen, /if \(busy \|\| mode !== "override" \|\| operationInFlightRef\.current\) return;/);
assert.match(
  screen,
  /Alert\.alert\(\s*t\("restore\.title"\),\s*t\("restore\.description"\),[\s\S]*?style: "destructive"[\s\S]*?restoreMutation\.mutateAsync/,
  "恢复前必须弹出破坏性确认"
);
assert.doesNotMatch(screen, /<Menu\b|Menu\.Item/, "恢复入口已从 ⋮ 菜单搬到概要卡");

// ───────── 5. 离页保护与会话失效：原样保留 ─────────
assert.match(screen, /usePreventRemove\(/);
assert.match(screen, /shouldPreventPosPermissionRemoval\(\{ dirty, busy, allowRemove \}\)/);
assert.match(screen, /shouldBypassPosPermissionRemovalGuard\(/);
assert.match(screen, /router\.replace\("\/\(auth\)\/login"\)/, "401 仍要回登录");
assert.match(screen, /Alert\.alert\(\s*t\("unsaved\.title"\)/, "有未保存改动离页仍要二次确认");

// ───────── 6. 弹层层级：全部走 Paper Portal + Modal，不得换成原生 Modal ─────────
assert.match(sheet, /import \{[^}]*\bModal\b[^}]*\bPortal\b[^}]*\} from "react-native-paper"/);
assert.match(sheet, /<Portal>\s*<Modal\b/);
for (const [name, source] of [
  ["pos-permission-sheet", sheet],
  ["pos-permission-changes-sheet", changesSheet],
  ["pos-permission-copy-sheet", copySheet],
  ["pos-permission-group-card", groupCard],
  ["pos-permission-summary-card", summaryCard],
  ["pos-terminal-permissions-screen", screen],
] as const) {
  assert.doesNotMatch(
    source,
    /import \{[^}]*\bModal\b[^}]*\} from "react-native"/,
    `${name} 不能引入原生 Modal（会压住 Snackbar 与 Dialog）`
  );
}
assert.match(changesSheet, /<PosPermissionSheet\b/);
assert.match(copySheet, /<PosPermissionSheet\b/);
assert.match(changesSheet, /dismissable=\{!busy\}/, "保存中不能点遮罩关闭确认弹层");

// ───────── 7. 确认弹层：高风险警示条只在新开高风险时出现，高风险标签只给「开启」行 ─────────
assert.match(changesSheet, /warning \? \(\s*<NoticeBox tone="red"/);
assert.match(changesSheet, /const showRisk = added && row\.highRisk/);
assert.match(changesSheet, /disabled=\{busy \|\| total === 0\}/, "没有改动不能确认保存");

// ───────── 8. 文案：代码里用到的 peoplePos 键中英文都存在 ─────────
function lookup(resource: unknown, key: string): unknown {
  return key.split(".").reduce<unknown>(
    (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
    resource
  );
}
const requireKey = (key: string, where: string) => {
  for (const [lang, resource] of [["zh", peoplePosZh], ["en", peoplePosEn]] as const) {
    assert.equal(typeof lookup(resource, key), "string", `${where} 用到的文案键 ${key} 在 ${lang} 里不存在`);
  }
};
const sources: [string, string][] = [
  ["screen", screen],
  ["changes-sheet", changesSheet],
  ["copy-sheet", copySheet],
  ["group-card", groupCard],
  ["summary-card", summaryCard],
  ["format", stripComments(moduleSource("pos-terminal-permissions-format.ts"))],
];
for (const [where, source] of sources) {
  for (const match of source.matchAll(/\bt\(\s*"([^"]+)"/g)) {
    const key = match[1];
    if (key.startsWith("common:")) continue;
    requireKey(key, where);
  }
}
// 动态拼出来的键族
for (const mode of ["inherited", "override"]) requireKey(`mode.${mode}`, "mode");
for (const preset of ["basic", "senior", "all", "none"]) requireKey(`presets.${preset}`, "presets");
for (const filter of ["all", "enabled", "highRisk", "changed"]) requireKey(`filters.${filter}`, "filters");
for (const kind of ["unauthorized", "forbidden", "notFound", "network"]) {
  requireKey(`errors.${kind}Title`, "errors");
  requireKey(`errors.${kind}Description`, "errors");
}
for (const empty of ["noResults", "noEnabled", "noChanges"]) requireKey(empty, "empty");
for (const key of ["sales", "discount", "payment", "returns", "history", "dailyClose", "installments", "drawerReceipt", "other"]) {
  requireKey(`groups.${key}`, "groups");
}
for (const scope of ["line", "order"]) requireKey(`discount.scopes.${scope}`, "discount");
for (const level of ["10", "20", "30", "40", "50", "manual"]) requireKey(`discount.levels.${level}`, "discount");
for (const level of ["10", "20", "30", "40", "50"]) requireKey(`discount.levelsShort.${level}`, "discount");
for (const kind of [
  "changePrice",
  "manualDiscountLine",
  "manualDiscountOrder",
  "quickDiscountLine",
  "quickDiscountOrder",
  "noReceiptReturn",
  "openCashDrawer",
]) {
  requireKey(`confirm.highRisk.impacts.${kind}`, "impacts");
}

console.log("pos-terminal-permissions-screen-contract.test.ts: ok");
