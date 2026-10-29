import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { IOS_REVIEW_EXCLUDED_ROUTE_NAMES } from "../ios-review/menu";
import { TAB_PATHS } from "../navigation/default-route";
import { buildWorkbenchSections } from "../navigation/workbench";
import { PERMISSIONS } from "../../shared/utils/access";

/**
 * 自动价格策略的源码契约：路由薄壳、接口路径、与后端菜单 / 权限码 / Web 菜单预览的一致性、文案键齐全，
 * 以及只能靠读源码守住的坑（BusinessSheet 里不能用 Paper 的 Portal 类组件、守卫、禁止拖动类原生依赖）。
 */
const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const repoRoot = resolve(mobileRoot, "../..");
const read = (relative: string) => readFileSync(resolve(mobileRoot, relative), "utf8");

// —— 路由：列表 + 编辑两页 ——
const routeDir = "app/(shell)/pricing-strategies";
assert.ok(existsSync(resolve(mobileRoot, routeDir, "_layout.tsx")));
assert.match(read(`${routeDir}/index.tsx`), /from "@\/modules\/pricing-strategies\/PricingStrategiesScreen"/);
assert.match(read(`${routeDir}/edit.tsx`), /from "@\/modules\/pricing-strategies\/PricingStrategyEditScreen"/);
assert.match(read(`${routeDir}/_layout.tsx`), /headerShown: false/);
assert.match(read("src/modules/pricing-strategies/PricingStrategiesScreen.tsx"), /"\/\(shell\)\/pricing-strategies\/edit"/);
assert.equal(TAB_PATHS["pricing-strategies"], "/(shell)/pricing-strategies");
assert.ok(IOS_REVIEW_EXCLUDED_ROUTE_NAMES.includes("pricing-strategies" as never), "审核模式没有离线演示数据，必须排除");
assert.match(read("src/modules/navigation/default-route.ts"), /DEVICE_MODE_BLOCKED_ROUTE_NAMES[\s\S]*"pricing-strategies"/, "设备会话没有用户账号，设备模式不展示");
const sections = buildWorkbenchSections(["price-updates", "pricing-strategies"]);
assert.deepEqual(
  sections.map((section) => [section.key, section.items.map((item) => item.routeName)]),
  [["product-sales", ["price-updates", "pricing-strategies"]]],
  "工作台放在「商品与销售」，紧跟价格和标签",
);

// —— 接口：与 Web 同一组路径，不重复 /api 前缀 ——
const api = read("src/modules/pricing-strategies/api.ts");
assert.match(api, /const BASE_URL = "\/react\/v1\/pricing-strategies";/);
assert.doesNotMatch(api, /"\/api\//);
const webService = readFileSync(resolve(repoRoot, "apps/web/src/services/pricingStrategyService.ts"), "utf8");
assert.match(webService, /'\/api\/react\/v1\/pricing-strategies'/, "Web 与移动端调用同一组接口");
for (const endpoint of ["/grid", "/evaluate"]) assert.ok(api.includes(`\`\${BASE_URL}${endpoint}\``), `移动端要调用 ${endpoint}`);

// —— 与后端一致：菜单项与权限码 ——
const navigation = readFileSync(resolve(repoRoot, "services/backend/BlazorApp.Api/Services/NavigationService.cs"), "utf8");
const menu = /RouteName = "pricing-strategies",[\s\S]*?\n\s*\},/.exec(navigation)?.[0] ?? "";
assert.match(menu, /TitleKey = "tabs\.pricingStrategies"/);
assert.match(menu, /Permission = Permissions\.PricingStrategy\.View/, "入口只认 View，Edit 由接口再校验");
const permissions = readFileSync(resolve(repoRoot, "services/backend/BlazorApp.Shared/Constants/Permissions.cs"), "utf8");
const block = /public static class PricingStrategy\s*\{([\s\S]*?)\}/.exec(permissions)?.[1] ?? "";
assert.equal(/View = "([^"]+)"/.exec(block)?.[1], PERMISSIONS.PricingStrategy.View);
assert.equal(/Edit = "([^"]+)"/.exec(block)?.[1], PERMISSIONS.PricingStrategy.Edit);
const controller = readFileSync(resolve(repoRoot, "services/backend/BlazorApp.Api/Controllers/React/ReactPricingStrategiesController.cs"), "utf8");
assert.match(controller, /\[HttpPut\("\{id\}"\)\]\s*\[Authorize\(Policy = Permissions\.PricingStrategy\.Edit\)\]/, "写操作在后端按 Edit 授权");
const shared = read("src/modules/pricing-strategies/shared.tsx");
assert.match(shared, /PERMISSIONS\.PricingStrategy\.View/);
assert.match(shared, /PERMISSIONS\.PricingStrategy\.Edit/);
assert.match(shared, /iosReviewOfflineGuardActive/);
for (const screen of ["PricingStrategiesScreen.tsx", "PricingStrategyEditScreen.tsx"]) {
  assert.match(read(`src/modules/pricing-strategies/${screen}`), /usePricingGuard\(\)/, `${screen} 必须先过守卫`);
}
const webPreview = readFileSync(resolve(repoRoot, "apps/web/src/utils/expoRoleMenuPreview.ts"), "utf8");
assert.match(webPreview, /routeName: 'pricing-strategies',[\s\S]*?permissionCodes: \[P\.PricingStrategy\.View\]/, "Web 的 App 菜单预览要同步登记");

// —— 弹层：BusinessSheet 是原生 Modal，Paper 的 Portal / Dialog / Menu / Snackbar 会被压在它下面 ——
for (const file of ["IntervalSheet.tsx", "MultiSelectSheet.tsx"]) {
  const source = read(`src/modules/pricing-strategies/${file}`);
  assert.match(source, /<BusinessSheet/);
  const paperImports = /import\s*\{([^}]*)\}\s*from "react-native-paper"/.exec(source)?.[1] ?? "";
  for (const forbidden of ["Portal", "Modal", "Snackbar", "Dialog", "Menu"]) {
    assert.doesNotMatch(paperImports, new RegExp(`\\b${forbidden}\\b`), `${file} 里不能用 Paper 的 ${forbidden}`);
  }
}

// —— 纯 JS：不引入需要重建原生包的依赖（这次走 OTA） ——
const moduleSources = readdirSync(moduleDir)
  .filter((file) => /\.tsx?$/.test(file) && !file.endsWith(".test.ts"))
  .map((file) => [file, readFileSync(resolve(moduleDir, file), "utf8")] as const);
for (const [file, source] of moduleSources) {
  assert.doesNotMatch(source, /from "(@react-native-community\/slider|react-native-gesture-handler|react-native-reanimated)"/, `${file} 不能引入新的原生依赖`);
}

// —— 文案：中英文键一致，源码与动态拼接用到的键都存在 ——
const zh = JSON.parse(read("src/locales/zh/screens/pricingStrategies.json")) as Record<string, unknown>;
const en = JSON.parse(read("src/locales/en/screens/pricingStrategies.json")) as Record<string, unknown>;
const flatten = (value: unknown, prefix = ""): string[] =>
  value && typeof value === "object" ? Object.entries(value).flatMap(([key, nested]) => flatten(nested, prefix ? `${prefix}.${key}` : key)) : [prefix];
const zhKeys = new Set(flatten(zh));
assert.deepEqual([...flatten(en)].sort(), [...zhKeys].sort(), "中英文文案键必须一致");
const usedKeys = new Set<string>();
for (const [, source] of moduleSources) {
  for (const match of source.matchAll(/\bt\(\s*"([A-Za-z0-9_.]+)"/g)) usedKeys.add(match[1]);
}
assert.ok(usedKeys.size > 60, "应该扫描到足够多的文案键");
for (const key of usedKeys) assert.ok(zhKeys.has(key), `源码用到的文案键 ${key} 在 pricingStrategies.json 里不存在`);
// 错误码、转换说明、级别、算法、形状都是拼接出来的键
const intervals = read("src/modules/pricing-strategies/pricing-intervals.ts");
const errorCodes = /export type PricingErrorCode =([\s\S]*?);/.exec(intervals)?.[1].match(/"([A-Za-z]+)"/g)?.map((code) => code.slice(1, -1)) ?? [];
assert.ok(errorCodes.length > 30);
const draftCodes = /export type DraftIssueCode =([^;]*);/.exec(read("src/modules/pricing-strategies/logic.ts"))?.[1].match(/"([A-Za-z]+)"/g)?.map((code) => code.slice(1, -1)) ?? [];
const dynamicKeys = [
  ...errorCodes.map((code) => `errors.${code}`),
  ...draftCodes.map((code) => `errors.${code}`),
  ...["raisedMin", "stepToFixed", "stepToFixedEndIgnored", "expToCurve", "drops"].map((code) => `notes.${code}`),
  ...["Supplier", "Store", "Global"].map((level) => `level.${level}`),
  ...["Step", "Linear", "Exponential", "ArcUp", "ArcDown", "Fixed"].map((algorithm) => `algorithm.${algorithm}`),
  ...["Linear", "ArcUp", "ArcDown"].map((shape) => `interval.${shape}`),
  "errors.forbidden",
  "errors.notFound",
];
for (const key of dynamicKeys) assert.ok(zhKeys.has(key), `动态文案键 ${key} 在 pricingStrategies.json 里不存在`);
for (const locale of ["zh", "en"]) {
  const common = JSON.parse(read(`src/locales/${locale}/common.json`)) as { tabs: Record<string, string> };
  const workbench = JSON.parse(read(`src/locales/${locale}/screens/workbench.json`)) as { routes: Record<string, string> };
  assert.ok(common.tabs.pricingStrategies, `${locale} common.json 缺 tabs.pricingStrategies`);
  assert.ok(workbench.routes.pricingStrategies, `${locale} workbench.json 缺 routes.pricingStrategies`);
}
// 英文界面不出现中文标点（见移动端英文术语约定）
assert.doesNotMatch(JSON.stringify(en), /[「」（）：、]/);
const i18nSource = read("src/shared/i18n/i18n.ts");
assert.match(i18nSource, /pricingStrategies: pricingStrategiesZh/);
assert.match(i18nSource, /pricingStrategies: pricingStrategiesEn/);
assert.match(i18nSource, /"pricingStrategies"/);

// 分店 / 供应商下拉统一按名字排序（排序在使用处，不改接口缓存里的原序）
assert.match(read("src/modules/pricing-strategies/PricingStrategiesScreen.tsx"), /sortedStores\.map\([\s\S]*sortedSuppliers\.map\(/);
assert.match(read("src/modules/pricing-strategies/QuickTryCard.tsx"), /sortedStores\.map\([\s\S]*sortedSuppliers\.map\(/);
assert.match(read("src/modules/pricing-strategies/PricingStrategyEditScreen.tsx"), /"stores" \? sortedStores : sortedSuppliers/);

console.log("pricing-strategies source-contract.test.ts: ok");
