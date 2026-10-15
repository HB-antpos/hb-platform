import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const mobileRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (path: string) => readFileSync(resolve(mobileRoot, path), "utf8");
const readJson = (path: string) => JSON.parse(read(path)) as Record<string, unknown>;

// ---- 报表页在路由层拦截：任何入口（通知、深链接、底部页签）都绕不过去 ----
const reports = read("app/(shell)/reports.tsx");
assert.match(reports, /priceTasks\.reportsLocked/, "报表页必须读锁定状态");
assert.match(reports, /<Redirect/, "被锁时用 Redirect（replace）跳走，返回键不会在两页之间来回弹");
assert.match(reports, /buildPriceUpdatesRoute\(\{[\s\S]*overdueOnly: true[\s\S]*\}\)/, "跳到价格和标签的超期筛选");
assert.match(reports, /ReportsHubScreen/, "未锁定时照常显示报表");
assert.match(reports, /refreshPriceTasks\(\)/, "进入报表时重新拉汇总，避免拿旧缓存误放行");

// ---- 工作台：入口灰显 + 红条 + 点击改道，不进报表 ----
const workbench = read("src/modules/workbench/workbench-screen.tsx");
assert.match(workbench, /routeName === "reports" && priceTasks\.reportsLocked/, "工作台点击报表中心要被拦截");
assert.match(workbench, /buildPriceUpdatesRoute\(\{[\s\S]*overdueOnly: true[\s\S]*\}\)/);
assert.match(workbench, /locked=\{reportsLocked && item\.routeName === "reports"\}/, "报表中心入口带锁标");
assert.match(workbench, /itemsByRoute\.has\("reports"\)/, "没有报表入口的账号不显示锁定提示");

// ---- 布局：汇总只查一次，经导航上下文分发；每小时提醒挂在布局里 ----
const layout = read("app/(shell)/_layout.tsx");
assert.match(layout, /usePriceTaskOverview\(/);
assert.match(layout, /priceTasks: priceTaskOverview/);
assert.match(layout, /refreshPriceTasks/);
assert.match(layout, /<PriceReminderHost/);
assert.doesNotMatch(layout, /getPriceUpdatePendingCount/, "角标不再只按当前分店查数量，改用按分店汇总");
assert.match(layout, /pendingPriceUpdateCount: priceTaskOverview\.totalPending/, "角标 = 名下全部分店合计");

// ---- 价格和标签页：参数只在页面里解释一次；全选覆盖没加载的；批量不跨店 ----
const screen = read("src/modules/price-updates/price-updates-screen.tsx");
assert.match(screen, /parsePriceUpdatesParams\(/);
assert.match(screen, /loadAllTasks\(/, "全选要逐页拉齐没滚动加载的任务");
assert.match(screen, /resolveSelectedTasks\(/);
assert.match(screen, /showStoreStrip && !selecting/, "批量处理期间分店条收起，只处理当前分店");
assert.match(screen, /overdueOnly: tab === "pending" && kindFilter === "overdue"/);
assert.match(screen, /priceTasks\.reportsLocked/, "页面顶部的锁定横幅");

// ---- 每小时提醒宿主：只在前台、按本机记录的时间节流 ----
const host = read("src/modules/price-reminder/PriceReminderHost.tsx");
assert.match(host, /shouldShowPriceReminder\(/);
assert.match(host, /PRICE_REMINDER_TICK_MS/);
assert.match(host, /AppState\.addEventListener\("change"/, "回到前台先刷新汇总再检查");
assert.match(host, /isPriceLabelPrintBusy\(\)/, "打印中不弹");
assert.match(host, /AppAsyncStorage\.setString\(/, "弹出时间记在本机");

// ---- 后端接口路径与前端一致 ----
const api = read("src/modules/price-updates/api.ts");
assert.match(api, /price-update-tasks\/summary/);
assert.match(api, /overdueOnly: true/);

// ---- 新文案：中英 key 必须一致，且都有内容 ----
function flatten(value: unknown, prefix = ""): string[] {
  if (value && typeof value === "object") {
    return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) =>
      flatten(child, prefix ? `${prefix}.${key}` : key)
    );
  }
  return [prefix];
}
const zh = readJson("src/locales/zh/screens/priceUpdates.json");
const en = readJson("src/locales/en/screens/priceUpdates.json");
assert.deepEqual(flatten(zh).sort(), flatten(en).sort(), "priceUpdates 中英文案 key 必须一致");
for (const group of ["reminder", "lock", "overdueEmpty", "strip", "workbench"]) {
  assert.ok(group in zh && group in en, `缺少文案分组 ${group}`);
}
assert.equal(readJson("src/locales/zh/screens/priceUpdates.json").title, "价格和标签");
assert.equal(readJson("src/locales/en/screens/priceUpdates.json").title, "Prices & labels");

console.log("price-updates/report-lock-source-contract.test.ts: ok");
