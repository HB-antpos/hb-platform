import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { IOS_REVIEW_EXCLUDED_ROUTE_NAMES } from "../ios-review/menu";
import { getRoleMenuDefinitions } from "../identity-admin/role-menu-catalog";
import { TAB_PATHS } from "../navigation/default-route";
import { buildWorkbenchSections } from "../navigation/workbench";
import { PERMISSIONS } from "../../shared/utils/access";
import { SPECIAL_CATEGORIES_BASE_URL } from "./api-contract";

/**
 * 特殊商品分类的源码契约：路由薄壳与登记、与后端菜单 / 权限码 / 控制器路由一致、中英文案键齐全且互相对应，
 * 以及拖动排序只用 RN 自带能力（不新增原生依赖）且保留上移 / 下移兜底。
 */
const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const repoRoot = resolve(mobileRoot, "../..");
const read = (relative: string) => readFileSync(resolve(mobileRoot, relative), "utf8");
const readRepo = (relative: string) => readFileSync(resolve(repoRoot, relative), "utf8");

test("路由薄壳、隐藏原生 header 与路由登记", () => {
  const routeDir = "app/(shell)/special-product-categories";
  const layout = read(`${routeDir}/_layout.tsx`);
  assert.match(layout, /headerShown: false/);
  for (const name of ["index", "category", "sync"]) assert.match(layout, new RegExp(`<Stack\\.Screen name="${name}"`));
  assert.match(read(`${routeDir}/index.tsx`), /from "@\/modules\/special-product-categories\/CategoryListScreen"/);
  assert.match(read(`${routeDir}/category.tsx`), /from "@\/modules\/special-product-categories\/CategoryProductsScreen"/);
  assert.match(read(`${routeDir}/sync.tsx`), /from "@\/modules\/special-product-categories\/SyncScreen"/);

  assert.equal(TAB_PATHS["special-product-categories"], "/(shell)/special-product-categories");
  assert.ok(IOS_REVIEW_EXCLUDED_ROUTE_NAMES.includes("special-product-categories" as never), "审核模式没有离线演示数据，必须排除");
  assert.match(read("src/modules/navigation/default-route.ts"), /DEVICE_MODE_BLOCKED_ROUTE_NAMES[\s\S]*"special-product-categories"/, "接口拒绝设备会话，设备模式不展示");
  assert.match(read("src/modules/navigation/primary-navigation.ts"), /routeName === "special-product-categories"/, "子页不占一级导航");
  const sections = buildWorkbenchSections(["pricing-strategies", "special-product-categories"]);
  assert.deepEqual(
    sections.map((section) => [section.key, section.items.map((item) => item.routeName)]),
    [["product-sales", ["pricing-strategies", "special-product-categories"]]],
    "工作台放在「商品与销售」，紧跟自动价格策略",
  );
  const screenPaths = read("src/modules/special-product-categories/shared.tsx");
  assert.match(screenPaths, /category: "\/\(shell\)\/special-product-categories\/category"/);
  assert.match(screenPaths, /sync: "\/\(shell\)\/special-product-categories\/sync"/);
});

test("角色菜单目录、权限常量与后端一致", () => {
  const menu = getRoleMenuDefinitions("zh").find((item) => item.platform === "mobile" && item.key === "special-product-categories");
  assert.deepEqual(menu?.permissionCodes, [PERMISSIONS.PosTerminal.SpecialProductsManage]);

  const permissions = readRepo("services/backend/BlazorApp.Shared/Constants/Permissions.cs");
  const block = /public static class SpecialProducts\s*\{([\s\S]*?)\}/.exec(permissions)?.[1] ?? "";
  assert.equal(/View = "([^"]+)"/.exec(block)?.[1], PERMISSIONS.PosTerminal.SpecialProductsView);
  assert.equal(/Manage = "([^"]+)"/.exec(block)?.[1], PERMISSIONS.PosTerminal.SpecialProductsManage);

  const navigation = readRepo("services/backend/BlazorApp.Api/Services/NavigationService.cs");
  const entry = /RouteName = "special-product-categories",[\s\S]*?\n\s*\},/.exec(navigation)?.[0] ?? "";
  assert.match(entry, /TitleKey = "tabs\.specialProductCategories"/);
  assert.match(entry, /Permission = Permissions\.PosTerminal\.SpecialProducts\.Manage/);
  const zhCommon = JSON.parse(read("src/locales/zh/common.json")) as { tabs: Record<string, string> };
  assert.ok(zhCommon.tabs.specialProductCategories, "tabs.specialProductCategories 文案存在");
});

test("接口路径与后端控制器一致", () => {
  assert.equal(SPECIAL_CATEGORIES_BASE_URL, "/react/v1/store-special-product-categories");
  const controllerPath = "services/backend/BlazorApp.Api/Controllers/React/StoreSpecialProductCategoriesController.cs";
  if (!existsSync(resolve(repoRoot, controllerPath))) return; // 后端并行开发期间可能尚未落盘，落盘后自动校验
  const controller = readRepo(controllerPath);
  assert.match(controller, /\[Route\("api\/react\/v1\/store-special-product-categories"\)\]/);
  const api = read("src/modules/special-product-categories/api-contract.ts");
  for (const endpoint of ["/order", "/products", "/candidates", "/assign", "/unmark", "/products/order", "/sync/targets", "/sync/preview", "/sync/execute"]) {
    assert.ok(api.includes(endpoint), `移动端要调用 ${endpoint}`);
    assert.ok(controller.includes(`"${endpoint.replace(/^\//, "")}"`) || controller.includes(`"{id}${endpoint}"`), `后端要有 ${endpoint}`);
  }
  // 写接口后端按 Manage 授权。
  assert.match(controller, /\[HttpPost\("assign"\)\]\s*\[Authorize\(Policy = Permissions\.PosTerminal\.SpecialProducts\.Manage\)\]/);
});

test("页面守卫、分店口径与不得新增原生依赖", () => {
  const shared = read("src/modules/special-product-categories/shared.tsx");
  assert.match(shared, /PERMISSIONS\.PosTerminal\.SpecialProductsManage/);
  assert.match(shared, /iosReviewOfflineGuardActive/);
  for (const screen of ["CategoryListScreen.tsx", "CategoryProductsScreen.tsx", "SyncScreen.tsx"]) {
    assert.match(read(`src/modules/special-product-categories/${screen}`), /useSpecialCategoriesGuard\(\)/, `${screen} 必须先过守卫`);
  }
  // 分店选择只用 managedStoreCodes()（主分店口径）。
  assert.match(read("src/modules/special-product-categories/use-special-product-categories.ts"), /access\.managedStoreCodes\(\)/);

  const pkg = JSON.parse(read("package.json")) as { dependencies: Record<string, string> };
  for (const name of Object.keys(pkg.dependencies)) {
    assert.doesNotMatch(name, /gesture-handler|reanimated|draggable|sortable|drag-sort/, `不得为拖动排序新增依赖：${name}`);
  }
  const moduleSources = readdirSync(moduleDir).filter((file) => /\.tsx?$/.test(file) && !file.endsWith(".test.ts")).map((file) => read(`src/modules/special-product-categories/${file}`)).join("\n");
  assert.doesNotMatch(moduleSources, /react-native-gesture-handler|react-native-reanimated|draggable/);
});

test("拖动排序：PanResponder 实现 + 上移 / 下移 / 无障碍兜底", () => {
  const list = read("src/modules/special-product-categories/DraggableCategoryList.tsx");
  assert.match(list, /PanResponder\.create/);
  assert.match(list, /onMoveShouldSetPanResponderCapture/);
  assert.match(list, /delayLongPress=\{350\}/, "整行长按起拖");
  assert.match(list, /accessibilityActions/, "读屏用户可用无障碍动作排序");
  assert.match(list, /icon="chevron-up"/);
  assert.match(list, /icon="chevron-down"/);
  // 列表页：拖动期间暂停外层滚动；排序提交失败要回滚并重新拉取。
  const screen = read("src/modules/special-product-categories/CategoryListScreen.tsx");
  assert.match(screen, /scrollEnabled=\{!dragging\}/);
  assert.match(screen, /setLocalOrder\(null\)[\s\S]*invalidateQueries/, "失败 / 冲突回滚并刷新");
});

test("中英文案：键一致，且代码里用到的键都存在", () => {
  const zh = JSON.parse(read("src/locales/zh/screens/specialProductCategories.json")) as Record<string, unknown>;
  const en = JSON.parse(read("src/locales/en/screens/specialProductCategories.json")) as Record<string, unknown>;
  const flatten = (value: unknown, prefix = ""): string[] =>
    value && typeof value === "object"
      ? Object.entries(value as Record<string, unknown>).flatMap(([key, nested]) => flatten(nested, prefix ? `${prefix}.${key}` : key))
      : [prefix];
  assert.deepEqual(flatten(zh).sort(), flatten(en).sort(), "中英文键必须一致");
  const keys = new Set(flatten(zh));
  const placeholders = (text: string) => [...text.matchAll(/\{\{(\w+)\}\}/g)].map((match) => match[1]).sort().join(",");
  const lookup = (tree: Record<string, unknown>, key: string) =>
    key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], tree) as string;
  for (const key of keys) assert.equal(placeholders(lookup(zh, key)), placeholders(lookup(en, key)), `${key} 的占位符中英文必须一致`);

  const sources = readdirSync(moduleDir)
    .filter((file) => /\.tsx$/.test(file))
    .map((file) => read(`src/modules/special-product-categories/${file}`))
    .join("\n");
  const used = new Set<string>(["title"]);
  for (const match of sources.matchAll(/"((?:actions|messages|errors|list|form|category|move|result|add|sync|skippedReason)\.[A-Za-z.]+)"/g)) used.add(match[1]);
  for (const key of used) assert.ok(keys.has(key), `代码用到的文案键缺失：${key}`);
  // 动态拼接的键：逐一检查可能的取值。
  for (const key of ["empty", "tooLong", "duplicate"]) assert.ok(keys.has(`form.issues.${key}`));
  for (const key of ["moved", "added"]) {
    assert.ok(keys.has(`result.${key}`));
    assert.ok(keys.has(`result.${key}WithSkipped`));
  }
  for (const key of ["noStorePrice", "productNotFound", "notSpecial", "categoryNotFound", "unknown"]) assert.ok(keys.has(`skippedReason.${key}`));
  for (const key of ["forbidden", "storeNotFound", "notFound", "nameDuplicate", "limitReached", "orderConflict", "assignConflict", "replaceNotConfirmed", "conflict"]) {
    assert.ok(keys.has(`errors.${key}`), `errors.${key}`);
  }
  assert.ok(keys.has("sync.storeFailedRolledBack"));
  // i18n 注册：命名空间已加入资源与 ns 列表。
  const i18n = read("src/shared/i18n/i18n.ts");
  assert.match(i18n, /specialProductCategories: specialProductCategoriesZh/);
  assert.match(i18n, /specialProductCategories: specialProductCategoriesEn/);
  assert.match(i18n, /"specialProductCategories", "faceMatch"/);
});
