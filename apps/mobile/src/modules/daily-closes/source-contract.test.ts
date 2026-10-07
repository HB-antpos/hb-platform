import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { IOS_REVIEW_EXCLUDED_ROUTE_NAMES } from "../ios-review/menu";
import { TAB_PATHS } from "../navigation/default-route";
import { PERMISSIONS } from "../../shared/utils/access";

/**
 * 日结记录的源码契约：路由薄壳、接口路径、与后端菜单/权限码的一致性、文案键齐全，
 * 以及几个只能靠读源码守住的坑（弹层压在 Paper Portal 下、守卫、占位区块不能整块隐藏）。
 * 界面本身没有 RN 渲染测试，这里守住最容易回归的结构。
 */
const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const repoRoot = resolve(mobileRoot, "../..");
const read = (relative: string) => readFileSync(resolve(mobileRoot, relative), "utf8");

// —— 路由薄壳：列表与明细各转发到对应屏幕 ——
const routeDir = "app/(shell)/daily-closes";
assert.ok(existsSync(resolve(mobileRoot, routeDir, "_layout.tsx")), "daily-closes 需要自己的 Stack 布局");
assert.match(read(`${routeDir}/index.tsx`), /from "@\/modules\/daily-closes\/DailyClosesScreen"/);
assert.match(read(`${routeDir}/detail.tsx`), /from "@\/modules\/daily-closes\/DailyCloseDetailScreen"/);
const layout = read(`${routeDir}/_layout.tsx`);
assert.match(layout, /name="index"/);
assert.match(layout, /name="detail"/);
assert.match(layout, /headerShown: false/, "两个屏幕自己渲染标题栏");
assert.equal(TAB_PATHS["daily-closes"], "/(shell)/daily-closes");
assert.ok(IOS_REVIEW_EXCLUDED_ROUTE_NAMES.includes("daily-closes" as never), "审核模式没有离线演示数据，必须排除日结记录入口");

// —— 接口：路径无 /api 前缀（apiClient 的 baseURL 已带），列表与明细两个端点 ——
const api = read("src/modules/daily-closes/api.ts");
assert.match(api, /const BASE_URL = "\/react\/v1\/pos-daily-closes";/);
assert.doesNotMatch(api, /"\/api\//, "路径不要重复 /api 前缀");
assert.match(api, /apiClient\.get\(BASE_URL, \{ params, signal \}\)/, "列表请求带 abort signal");
assert.match(api, /apiClient\.get\(`\$\{BASE_URL\}\/\$\{encodeURIComponent\(dailyCloseGuid\)\}`, \{ signal \}\)/, "明细请求带 abort signal 并编码编号");

// —— 与后端一致：菜单项、独立权限码 ——
const navigation = readFileSync(resolve(repoRoot, "services/backend/BlazorApp.Api/Services/NavigationService.cs"), "utf8");
const dailyCloseMenu = /RouteName = "daily-closes",[\s\S]*?\n\s*\},/.exec(navigation)?.[0] ?? "";
assert.match(dailyCloseMenu, /TitleKey = "tabs\.dailyCloses"/, "后端菜单标题键与移动端 tabs.dailyCloses 一致");
assert.match(dailyCloseMenu, /Permission = Permissions\.DailyCloseRecords\.View/, "后端菜单只认独立权限码");
const permissions = readFileSync(resolve(repoRoot, "services/backend/BlazorApp.Shared/Constants/Permissions.cs"), "utf8");
const backendCode = /public static class DailyCloseRecords\s*\{\s*public const string View = "([^"]+)";/.exec(permissions)?.[1];
assert.equal(PERMISSIONS.DailyCloseRecords.View, backendCode, "权限码必须与后端 Permissions.DailyCloseRecords.View 完全一致");
const zhPermissions = (JSON.parse(read("src/locales/zh/accessPermissions.json")) as { permissions: Record<string, unknown> }).permissions;
assert.ok(backendCode && backendCode in zhPermissions, "权限码需要在 accessPermissions.json 有说明（#590 已登记）");

// —— 守卫：两个屏幕都先过权限与审核模式守卫，且只认独立权限标志 ——
const shared = read("src/modules/daily-closes/daily-close-shared.tsx");
assert.match(shared, /state\.access\.canViewDailyCloseRecords/);
assert.doesNotMatch(shared, /canViewLegacyEmployeeLogs|canViewPosOperationAudits/, "日结记录权限不能借用员工操作日志的权限");
assert.match(shared, /iosReviewOfflineGuardActive/);
for (const screen of ["DailyClosesScreen.tsx", "DailyCloseDetailScreen.tsx"]) {
  assert.match(read(`src/modules/daily-closes/${screen}`), /useDailyClosesGuard\(\)/, `${screen} 必须先过守卫`);
}

// —— 弹层：BusinessSheet 是原生 Modal，Paper 的 Portal / Modal / Snackbar 会被压在它下面看不见 ——
const filterSheet = read("src/components/daily-closes/DailyCloseFilterSheet.tsx");
assert.match(filterSheet, /<BusinessSheet/);
const paperImports = /import\s*\{([^}]*)\}\s*from "react-native-paper"/.exec(filterSheet)?.[1] ?? "";
for (const forbidden of ["Portal", "Modal", "Snackbar", "Dialog", "Menu"]) {
  assert.doesNotMatch(paperImports, new RegExp(`\\b${forbidden}\\b`), `筛选面板里不能用 Paper 的 ${forbidden}（会被压在 BusinessSheet 下面）`);
}
assert.doesNotMatch(filterSheet, /MonthDatePickerField/, "MonthDatePickerField 是 Paper Modal 弹窗，自定义日期要用内嵌月历");
assert.match(filterSheet, /<MonthDatePicker/, "自定义日期用内嵌月历");

// —— 区块不整块隐藏：没有数据时用虚线占位 ——
const detailScreen = read("src/modules/daily-closes/DailyCloseDetailScreen.tsx");
assert.match(detailScreen, /hasTenderData\(detail\) \? <TenderTable[^>]*\/> : <DataPlaceholder \/>/);
assert.match(detailScreen, /hasCashCountData\(detail\) \? <CashCountPanel[^>]*\/> : <DataPlaceholder \/>/);
assert.match(detailScreen, /resolveBackfillNotice\(detail\)/, "补录记录显示蓝色提示条");
assert.match(detailScreen, /resolveSaveLogLink\(/, "查看保存记录按权限与来源决定是否显示");
// 保存记录入口：带预置参数跳员工操作日志（路由参数名要与那边 useLegacyRouteParams 一致）
assert.match(detailScreen, /pathname: "\/\(shell\)\/legacy-employee-logs"/);
const legacyScreen = read("src/modules/legacy-employee-logs/LegacyEmployeeLogsScreen.tsx");
for (const name of ["source", "stores", "preset", "device"]) {
  assert.match(legacyScreen, new RegExp(`${name}: first\\(params\\.${name}\\)`), `员工操作日志要支持路由参数 ${name}`);
}

// —— 金额与差额：只走统一的格式化（U+2212 减号、tabular-nums），不在界面里临时拼 ——
const componentSources = readdirSync(resolve(mobileRoot, "src/components/daily-closes"))
  .filter((file) => file.endsWith(".tsx"))
  .map((file) => [file, read(`src/components/daily-closes/${file}`)] as const);
for (const [file, source] of componentSources) {
  assert.doesNotMatch(source, /toFixed\(2\)/, `${file} 金额必须走 formatDailyCloseMoney`);
}

// —— 文案：中英文键一致（parity 脚本也会查），源码里用到的键都存在 ——
const zh = JSON.parse(read("src/locales/zh/screens/dailyCloses.json")) as Record<string, unknown>;
const en = JSON.parse(read("src/locales/en/screens/dailyCloses.json")) as Record<string, unknown>;
const flatten = (value: unknown, prefix = ""): string[] =>
  value && typeof value === "object" ? Object.entries(value).flatMap(([key, nested]) => flatten(nested, prefix ? `${prefix}.${key}` : key)) : [prefix];
const zhKeys = new Set(flatten(zh));
assert.deepEqual([...flatten(en)].sort(), [...zhKeys].sort(), "中英文文案键必须一致");

const sourceFiles = [
  ...readdirSync(resolve(mobileRoot, "src/modules/daily-closes")).filter((file) => file.endsWith(".tsx")).map((file) => `src/modules/daily-closes/${file}`),
  ...componentSources.map(([file]) => `src/components/daily-closes/${file}`),
];
const usedKeys = new Set<string>();
for (const file of sourceFiles) {
  for (const match of read(file).matchAll(/\bt\(\s*"([A-Za-z0-9_.]+)"/g)) usedKeys.add(match[1]);
}
assert.ok(usedKeys.size > 40, "应该扫描到足够多的文案键");
for (const key of usedKeys) assert.ok(zhKeys.has(key), `源码用到的文案键 ${key} 在 dailyCloses.json 里不存在`);
// logic 里返回的文案键（scope.*）与动态拼接的键集合
const dynamicKeys = [
  ...["all", "short", "over", "even", "none"].map((kind) => `status.${kind}`),
  ...["short", "over", "even", "none"].map((kind) => `statusLine.${kind}`),
  ...["today", "yesterday", "last7", "thisMonth", "custom"].map((preset) => `presets.${preset}`),
  ...["wpf", "handheld", "ipad"].map((kind) => `source.${kind}`),
  ...["cashOnly", "traceOnly", "generic"].map((kind) => `detail.notice.${kind}`),
  ...["cash", "card", "voucher"].map((method) => `detail.tenders.${method}`),
  ...["invalid", "reversed", "tooLong"].map((issue) => `filters.rangeIssue.${issue}`),
  ...Array.from({ length: 12 }, (_, index) => `months.${index + 1}`),
  ...Array.from({ length: 7 }, (_, index) => `weekdays.${index}`),
  "scope.allStores",
  "scope.oneStore",
  "scope.manyStores",
  "section.count",
  "section.countPartial",
  "dataSource.clientUpload",
  "dataSource.auditBackfill",
  "detail.cashCount.note",
  "detail.cashCount.coin",
  "detail.cashCount.noteSubtotal",
  "detail.cashCount.coinSubtotal",
  "detail.info.expand",
  "detail.info.collapse",
  "errors.loadFailed",
  "errors.detailFailed",
];
for (const key of dynamicKeys) assert.ok(zhKeys.has(key), `动态文案键 ${key} 在 dailyCloses.json 里不存在`);

// 全局文案：菜单标题键（后端 TitleKey = tabs.dailyCloses）与工作台入口
for (const locale of ["zh", "en"]) {
  const common = JSON.parse(read(`src/locales/${locale}/common.json`)) as { tabs: Record<string, string> };
  const workbench = JSON.parse(read(`src/locales/${locale}/screens/workbench.json`)) as { routes: Record<string, string> };
  assert.ok(common.tabs.dailyCloses, `${locale} common.json 缺 tabs.dailyCloses`);
  assert.ok(workbench.routes.dailyCloses, `${locale} workbench.json 缺 routes.dailyCloses`);
}
const i18nSource = read("src/shared/i18n/i18n.ts");
assert.match(i18nSource, /dailyCloses: dailyClosesZh/);
assert.match(i18nSource, /dailyCloses: dailyClosesEn/);
assert.match(i18nSource, /"dailyCloses"/, "ns 列表里要登记 dailyCloses 命名空间");

// 目录不应混进测试以外的 jest 用例（mobile 的 tsx 车道只认 .test.ts）
assert.deepEqual(
  readdirSync(resolve(mobileRoot, "src/modules/daily-closes")).filter((file) => /\.test\.tsx$/.test(file)),
  [],
  "mobile 没有 jest 车道，.test.tsx 会让 test-inventory 报错",
);
assert.ok(existsSync(join(mobileRoot, "src/modules/daily-closes/logic.test.ts")));

console.log("daily-closes source-contract.test.ts: ok");
