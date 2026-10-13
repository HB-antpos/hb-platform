import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const read = (relative: string) => readFileSync(resolve(directory, relative), "utf8");

const access = read("../../shared/utils/access.ts");
const api = read("./api.ts");
const page = read("../../../app/(shell)/product-query.tsx");
const hero = read("../../components/product-maintenance/ProductHeroCard.tsx");
const sheet = read("../../components/product-maintenance/ProductNameEditSheet.tsx");
const seed = read("../../../../../services/backend/BlazorApp.Shared/Constants/PermissionSeedData.cs");
const controller = read(
  "../../../../../services/backend/BlazorApp.Api/Controllers/React/ReactStoreProductMaintenanceController.cs",
);

// 权限码：移动端常量必须与后端种子里的码一致，且改名权限独立于改价的 Edit。
assert.match(access, /EditName: "StoreProducts\.EditName"/, "移动端必须登记 StoreProducts.EditName");
assert.match(seed, /Permissions\.StoreProducts\.EditName/, "后端权限种子必须包含 EditName");

// API：改名走 PUT products/{code}/name，记录走 GET products/{code}/name-history。
assert.match(api, /apiClient\.put\(\s*`\$\{BASE_PATH\}\/products\/\$\{encodeURIComponent\(productCode\)\}\/name`/);
assert.match(api, /\/products\/\$\{encodeURIComponent\(productCode\)\}\/name-history/);

// 页面入口：必须是「已登录 + 非设备模式 + 非离线 + 持有 EditName」，缺一个都会把全局改名暴露给不该有的人。
const gate = page.slice(page.indexOf("const canEditProductName ="), page.indexOf("const printerAutoReconnectPaused"));
for (const part of [
  "!isDeviceMode",
  "isAuthenticated",
  "!offlineMode",
  "access.hasPermission(PERMISSIONS.StoreProducts.EditName)",
]) {
  assert.ok(gate.includes(part), `改名入口条件缺少：${part}`);
}
assert.match(page, /onPressEditName=\{canEditProductName \? handleOpenNameEdit : undefined\}/, "没有权限时不能传入口");

// 保存中算「忙碌」：期间不能响应扫码/切商品，避免改名写入时页面已换了商品。
assert.match(page, /productTypeSaving \|\|\s*nameSaving \|\|/, "isProductQueryBusy 必须包含 nameSaving");
// 保存成功后要同步更新「初始详情」（脏检查基线），否则新名称会被判成未保存改动。
const saveHandler = page.slice(page.indexOf("const handleSaveProductName"), page.indexOf("const handleToggleAutoPricing"));
assert.match(saveHandler, /setInitialDetail/);
assert.match(saveHandler, /hqSyncMutationCoordinatorRef\.current\?\.fail\(mutation\)/, "失败要通知 HQ 同步协调器");

// 头卡片：两种版式都要有铅笔入口，且没传回调时不渲染。
assert.match(hero, /onPressEditName \? \(/);
assert.equal((hero.match(/\{editNameButton\}/g) ?? []).length, 2, "full 与 compact 版式都要渲染铅笔按钮");

// 弹层：名称不允许换行；展示修改记录。
assert.match(sheet, /replace\(\/\[\\r\\n\]\+\/g, " "\)/, "粘贴的换行必须替换成空格");
assert.match(sheet, /nameEdit\.history/);

// 后端：改名与记录接口必须要求已登录 + EditName，且不放行匿名绑定设备。
const requireName = controller.slice(
  controller.indexOf("RequireEditNamePermissionAsync()"),
  controller.indexOf("private IActionResult BuildMutationResult"),
);
assert.match(requireName, /IsAuthenticated != true/);
assert.match(requireName, /return Forbid\(\)/);
assert.match(requireName, /Permissions\.StoreProducts\.EditName/);
assert.match(controller, /\[HttpPut\("products\/\{productCode\}\/name"\)\][\s\S]*RequireEditNamePermissionAsync/);
assert.match(controller, /\[HttpGet\("products\/\{productCode\}\/name-history"\)\][\s\S]*RequireEditNamePermissionAsync/);

console.log("product-name-edit-contract.test.ts: ok");
