import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { getVisibleTabRouteNames, SUPPORTED_APP_MENU_ROUTE_NAMES, TAB_PATHS } from "../navigation/default-route";
import { buildWorkbenchSections } from "../navigation/workbench";

const mobileRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (path: string) => readFileSync(resolve(mobileRoot, path), "utf8");
const readJson = (path: string) => JSON.parse(read(path));

assert.equal(TAB_PATHS["price-updates"], "/(shell)/price-updates");
assert.equal(SUPPORTED_APP_MENU_ROUTE_NAMES.has("price-updates"), true, "后端菜单下发 price-updates 必须被识别");
assert.deepEqual(
  getVisibleTabRouteNames({ routeNames: ["price-updates"], isDeviceMode: true }),
  ["workbench", "price-updates", "settings"],
  "设备绑定分店也要能处理价格更新，设备模式不屏蔽该入口"
);
assert.match(read("app/(shell)/price-updates.tsx"), /PriceUpdatesScreen/);

assert.deepEqual(
  buildWorkbenchSections(["product-query", "price-updates"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [{ key: "product-sales", itemRouteNames: ["product-query", "price-updates"] }],
  "价格更新归入商品销售，且只依赖后端显式菜单"
);
assert.equal(
  buildWorkbenchSections(["product-query"]).some((section) =>
    section.items.some((item) => item.routeName === "price-updates")
  ),
  false
);

assert.equal(readJson("src/locales/zh/common.json").tabs.priceUpdates, "价格更新");
assert.equal(readJson("src/locales/en/common.json").tabs.priceUpdates, "Price updates");
assert.equal(readJson("src/locales/zh/screens/workbench.json").routes.priceUpdates, "价格更新");
assert.equal(readJson("src/locales/en/screens/workbench.json").routes.priceUpdates, "Price updates");

const i18nSource = read("src/shared/i18n/i18n.ts");
assert.match(i18nSource, /priceUpdates: priceUpdatesZh/);
assert.match(i18nSource, /priceUpdates: priceUpdatesEn/);
assert.match(i18nSource, /ns: \[[^\]]*"priceUpdates"/, "新命名空间必须登记到 ns 列表");

// 工作台角标链路：layout 查 count → access-context → workbench 按路由名查表
const layoutSource = read("app/(shell)/_layout.tsx");
assert.match(layoutSource, /getPriceUpdatePendingCount/);
assert.match(layoutSource, /pendingPriceUpdateCount: pendingPriceUpdateQuery\.data \?\? 0/);
assert.match(read("src/modules/navigation/access-context.tsx"), /pendingPriceUpdateCount: number/);
const workbenchSource = read("src/modules/workbench/workbench-screen.tsx");
assert.match(workbenchSource, /"price-updates": pendingPriceUpdateCount/);
assert.match(workbenchSource, /"employee-profile-review": pendingProfileReviewCount/, "员工档案审核角标必须保持原行为");

console.log("price-updates/route-contract.test.ts: ok");
