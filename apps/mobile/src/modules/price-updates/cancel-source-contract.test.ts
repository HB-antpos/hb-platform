import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const mobileRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (path: string) => readFileSync(resolve(mobileRoot, path), "utf8");
const readJson = (path: string) => JSON.parse(read(path)) as Record<string, Record<string, string>>;

// ---- 接口：与 Web「按商品」同一个 cancel 接口，只传 productCode（不传 storeCodes = 所有分店），备注为空不发送 ----
const api = read("src/modules/price-updates/api.ts");
assert.match(api, /export async function cancelPriceUpdateTasksForProduct\(/);
assert.match(api, /apiClient\.post\(`\$\{MONITOR_BASE_PATH\}\/cancel`, \{\s*productCode,/, "撤销走 store-price-update-tasks/cancel，按商品提交");
const cancelFn = api.slice(
  api.indexOf("export async function cancelPriceUpdateTasksForProduct"),
  api.indexOf("export async function getPendingStoreCountForProduct")
);
assert.doesNotMatch(cancelFn.replace(/\/\*[\s\S]*?\*\//g, ""), /storeCodes/, "请求体不传 storeCodes：撤销覆盖所有分店，不只当前分店");
assert.match(api, /\.\.\.\(trimmedNote \? \{ note: trimmedNote \} : \{\}\)/, "备注为空不发送");
assert.match(api, /`\$\{MONITOR_BASE_PATH\}\/by-product`/, "确认框用 by-product 查有未完成任务的分店数");
assert.match(api, /catch \{\s*return null;/, "查分店数失败不阻塞撤销，退回不带数量的说明");

// ---- 权限：专用码 Warehouse.CancelPriceUpdateTasks（旧后端阶段认 ManageProducts），设备会话不显示 ----
const access = read("src/shared/utils/access.ts");
assert.match(access, /Warehouse: \{[\s\S]*ManageProducts: "Warehouse\.ManageProducts"[\s\S]*CancelPriceUpdateTasks: "Warehouse\.CancelPriceUpdateTasks"/);
const screen = read("src/modules/price-updates/price-updates-screen.tsx");
assert.match(
  screen,
  /canCancelPriceTasks\(\{[\s\S]*isDeviceMode[\s\S]*PERMISSIONS\.Warehouse\.CancelPriceUpdateTasks[\s\S]*PERMISSIONS\.Warehouse\.ManageProducts/,
  "撤销入口 = 账号会话 + 专用码（或旧的 ManageProducts）"
);
assert.match(screen, /showMultiSelect = \(canOperate \|\| canCancel\)/, "只有撤销权限（没有改价权限）的账号也能进多选");
assert.match(screen, /selecting && \(canOperate \|\| canCancel\)/, "底部批量栏对只有撤销权限的账号也出现");
assert.match(screen, /\{canOperate \? \(\s*<Button\s+mode="contained"\s+icon="printer-outline"/, "没有改价权限的账号不显示「更新并打印」");

// ---- 流程：必须二次确认；撤销按商品、覆盖所有分店；结果按类别提示；撤销后刷新 ----
assert.match(screen, /setCancelTargets\(\[task\]\)/, "卡片「撤销」先打开确认框，不直接提交");
assert.match(screen, /setCancelTargets\(tasks\)/, "批量「撤销所选」先打开确认框，不直接提交");
assert.match(screen, /getPendingStoreCountForProduct\(task\.productCode\)/, "单个商品打开确认框时补查分店数");
assert.match(screen, /runCancelByProduct\(\{[\s\S]*cancelPriceUpdateTasksForProduct\(productCode, trimmedNote\)/, "按商品逐个撤销");
assert.doesNotMatch(screen, /cancelPriceUpdateTasks\(/, "不再按任务 ID 撤销（那只会撤当前分店的几条）");
for (const key of ["cancelSuccess", "cancelPartial", "cancelInterrupted", "cancelNothing", "cancelFailed"]) {
  assert.match(screen, new RegExp(`messages\\.${key}`), `结果提示 ${key} 必须接入`);
}
assert.match(screen, /clearSelection\(\);[\s\S]{0,200}setCancelTargets\(\[\]\);\s*await refreshAll\(\)/, "撤销后清除选择并刷新列表");

const dialog = read("src/modules/price-updates/cancel-tasks-dialog.tsx");
assert.match(dialog, /cancelDialog\.scopeNote/, "确认框必须写明撤销范围是所有分店");
assert.match(dialog, /cancelDialog\.descriptionWithCount/, "查到分店数时写「目前有 N 家分店还没完成」");

const cards = read("src/modules/price-updates/price-update-task-cards.tsx");
assert.match(cards, /canCancel \? \(\s*<Menu\.Item[\s\S]*actions\.cancelTask/, "卡片「更多」里才有撤销，不占主操作位");
assert.match(cards, /\(canOperate \|\| canCancel\) && !selecting/, "只有撤销权限的账号卡片也显示「更多」");

// ---- 文案：中英文齐全，且对话框写明不改价格、不可恢复 ----
for (const lang of ["zh", "en"]) {
  const messages = readJson(`src/locales/${lang}/screens/priceUpdates.json`);
  assert.ok(messages.actions.cancelTask, `${lang} actions.cancelTask`);
  assert.ok(messages.selection.cancelSelected, `${lang} selection.cancelSelected`);
  for (const key of ["title", "description", "descriptionWithCount", "batchDescription", "scopeNote", "warning", "noteLabel", "notePlaceholder", "confirm"]) {
    assert.ok(messages.cancelDialog[key], `${lang} cancelDialog.${key}`);
  }
  for (const key of ["cancelSuccess", "cancelPartial", "cancelNothing", "cancelInterrupted", "cancelFailed"]) {
    assert.ok(messages.messages[key], `${lang} messages.${key}`);
  }
}
assert.match(readJson("src/locales/zh/screens/priceUpdates.json").cancelDialog.warning, /不会修改任何分店价格[\s\S]*无法恢复/);

// ---- 权限码：移动端权限文案与后端种子保持一致（access-permission-presentation.test.ts 会校验码集合），且写明所有分店 / 不可恢复 ----
for (const lang of ["zh", "en"]) {
  const perms = (JSON.parse(read(`src/locales/${lang}/accessPermissions.json`)) as { permissions: Record<string, { name: string; description: string }> }).permissions;
  assert.ok(perms["Warehouse.CancelPriceUpdateTasks"]?.name, `${lang} 权限名`);
  assert.ok(perms["Warehouse.CancelPriceUpdateTasks"]?.description, `${lang} 权限说明`);
}
