import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { canManagePosSettings, canViewPosSettings } from "./access";
import { POS_SETTINGS_PERMISSIONS, settingCopyId } from "./constants";
import { buildPosSettingsDeviceHref, buildPosSettingsStoreHref, firstParam } from "./routes";
import { IOS_REVIEW_EXCLUDED_ROUTE_NAMES } from "../ios-review/menu";
import { TAB_PATHS, getVisibleTabRouteNames } from "../navigation/default-route";
import { buildWorkbenchSections } from "../navigation/workbench";
import { getRoleMenuDefinitions } from "../identity-admin/role-menu-catalog";

const here = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(here, "../../..");
const repoRoot = resolve(mobileRoot, "../..");
const readJson = (path: string) => JSON.parse(readFileSync(resolve(mobileRoot, path), "utf8"));
const readText = (path: string) => readFileSync(resolve(mobileRoot, path), "utf8");

// ───────── 权限：入口看 View；编辑还要 Manage；审核模式 / 未登录都不可见 ─────────
const has = (...codes: string[]) => (code: string) => codes.includes(code);
assert.deepEqual(POS_SETTINGS_PERMISSIONS, { view: "PosSettings.View", manage: "PosSettings.Manage" });
assert.equal(canViewPosSettings(true, has("PosSettings.View"), false), true);
assert.equal(canViewPosSettings(false, has("PosSettings.View"), false), false, "未登录不可见");
assert.equal(canViewPosSettings(true, has("PosSettings.View"), true), false, "iOS 审核离线会话不可见");
assert.equal(canViewPosSettings(true, has("PosSettings.Manage"), false), false, "只有 Manage 没有 View 不能进入页面（接口读取需要 View）");
assert.equal(canViewPosSettings(true, has(), false), false);
assert.equal(canManagePosSettings(true, has("PosSettings.View", "PosSettings.Manage"), false), true);
assert.equal(canManagePosSettings(true, has("PosSettings.View"), false), false, "只有 View 只读：不能编辑、不显示保存");
assert.equal(canManagePosSettings(true, has("PosSettings.Manage"), false), false, "没有 View 不可编辑");
assert.equal(canManagePosSettings(true, has("PosSettings.View", "PosSettings.Manage"), true), false, "审核模式不可编辑");

// ───────── 路径 ─────────
assert.equal(buildPosSettingsStoreHref("S 01"), "/pos-settings/store?storeCode=S%2001");
assert.equal(buildPosSettingsDeviceHref("ORI/01"), "/pos-settings/device?deviceCode=ORI%2F01");
assert.equal(firstParam(["a", "b"]), "a");
assert.equal(firstParam(undefined), "");

// ───────── 路由与菜单三处登记（移动端部分） ─────────
assert.equal(TAB_PATHS["pos-settings"], "/(shell)/pos-settings");
assert.ok(IOS_REVIEW_EXCLUDED_ROUTE_NAMES.includes("pos-settings" as never), "iOS 审核模式必须排除收银端设置");
assert.equal(
  getVisibleTabRouteNames({ routeNames: ["pos-settings"], isDeviceMode: true }).includes("pos-settings"),
  false,
  "设备会话不展示收银端设置",
);
assert.equal(
  getVisibleTabRouteNames({ routeNames: ["pos-settings"], isDeviceMode: false }).includes("pos-settings"),
  true,
);
assert.deepEqual(
  getRoleMenuDefinitions("en").find((item) => item.platform === "mobile" && item.key === "pos-settings")?.permissionCodes,
  ["PosSettings.View"],
);
assert.deepEqual(
  buildWorkbenchSections(["pos-settings"]).flatMap((section) => section.items.map((item) => item.icon)),
  ["tune-variant"],
);
for (const file of ["_layout.tsx", "index.tsx", "store.tsx", "device.tsx"]) {
  assert.ok(existsSync(resolve(mobileRoot, "app/(shell)/pos-settings", file)), `缺少路由文件 ${file}`);
}

// ───────── 文案：标题键、权限文案（zh 与后端种子逐字一致）、每个设置键都有中英文名称 ─────────
assert.equal(readJson("src/locales/zh/common.json").tabs.posSettings, "收银端设置");
assert.equal(readJson("src/locales/en/common.json").tabs.posSettings, "POS Settings");
assert.equal(readJson("src/locales/zh/screens/workbench.json").routes.posSettings, "收银端设置");
assert.equal(readJson("src/locales/en/screens/workbench.json").routes.posSettings, "POS Settings");

const lookup = (json: { permissions: Record<string, { name: string; description: string }> }, code: string) =>
  json.permissions[code];
assert.deepEqual(lookup(readJson("src/locales/zh/accessPermissions.json"), "PosSettings.View"), {
  name: "查看收银端设置",
  description: "Web 页面 /system/pos-settings 与员工 App「收银端设置」- 查看全局、门店、设备的收银端设置与设备应用情况",
});
assert.deepEqual(lookup(readJson("src/locales/zh/accessPermissions.json"), "PosSettings.Manage"), {
  name: "管理收银端设置",
  description: "Web 页面 /system/pos-settings 与员工 App「收银端设置」- 修改全局、门店、设备的收银端设置并下发到收银机",
});
for (const code of ["PosSettings.View", "PosSettings.Manage"]) {
  const en = lookup(readJson("src/locales/en/accessPermissions.json"), code);
  assert.ok(en?.name && en.description, `${code} 缺少英文文案`);
  assert.ok(!/[一-鿿]/.test(`${en?.name}${en?.description}`), `${code} 英文文案不应含中文`);
}

// 后端目录里的每个设置键，中英文都必须有名称与说明（新增键忘了补文案会在这里挂掉）。
{
  const catalogPath = resolve(repoRoot, "services/backend/BlazorApp.Shared/Constants/PosSettingCatalog.cs");
  const source = readFileSync(catalogPath, "utf8");
  const keysBlock = source.slice(source.indexOf("class PosSettingKeys"), source.indexOf("record PosSettingDefinition"));
  const keys = [...keysBlock.matchAll(/public const string \w+ = "([^"]+)";/g)].map((match) => match[1]);
  assert.equal(keys.length, 9, "后端目录应有 9 个设置键");
  for (const language of ["zh", "en"] as const) {
    const copy = readJson(`src/locales/${language}/screens/posSettings.json`).settings as Record<string, { name?: string; hint?: string }>;
    for (const key of keys) {
      const entry = copy[settingCopyId(key)];
      assert.ok(entry?.name?.trim(), `${language} 缺少设置项 ${key} 的名称`);
      assert.ok(entry?.hint?.trim(), `${language} 缺少设置项 ${key} 的说明`);
    }
    assert.deepEqual(Object.keys(copy).sort(), keys.map(settingCopyId).sort(), `${language} 文案里不应有目录之外的多余项`);
  }
}
assert.equal(settingCopyId("cashMovement.enabled"), "cashMovement_enabled", "键里的点要换成下划线，避开 i18next 的 keySeparator");

// ───────── 源码契约：模块已注册命名空间；保存前必经确认弹层 ─────────
assert.ok(readText("src/shared/i18n/i18n.ts").includes("posSettings: posSettingsZh"), "i18n 必须注册 posSettings 命名空间");
assert.ok(readText("src/shared/i18n/i18n.ts").includes('"posSettings"'), "i18n ns 列表必须包含 posSettings");
for (const file of ["GlobalPanel.tsx", "store-detail-screen.tsx", "device-detail-screen.tsx"]) {
  const source = readText(`src/modules/pos-settings/${file}`);
  assert.ok(source.includes("useSaveFlow"), `${file} 必须走统一的保存流程`);
  assert.ok(source.includes("SaveConfirmSheet"), `${file} 保存前必须弹确认底部弹层`);
  assert.ok(source.includes("canManage ? <SaveBar"), `${file} 只有 Manage 才显示保存按钮`);
  assert.ok(source.includes("readOnly={!canManage}"), `${file} 只有 View 时分段控件只读`);
}
const flowSource = readText("src/modules/pos-settings/use-save-flow.ts");
assert.ok(flowSource.indexOf("setOpen(true)") < flowSource.indexOf("await save("), "请求只能在确认之后发出");

console.log("routes-access.test.ts: ok");
