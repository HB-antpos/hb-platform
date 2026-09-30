import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildWorkbenchSections } from "../navigation/workbench";

const currentDir = dirname(fileURLToPath(import.meta.url));
const warehouseSource = readFileSync(resolve(currentDir, "../../../app/(shell)/warehouse.tsx"), "utf8");

// 货柜管理已拆为工作台独立入口：商品和货位管理页不再内嵌货柜卡片，也不再为仅有 Container.View 的会话开放。
assert.equal(warehouseSource.includes("renderContainerEntry"), false, "商品和货位管理页不得再渲染货柜入口卡片");
assert.equal(warehouseSource.includes('"/containers"'), false, "商品和货位管理页不得再跳转货柜列表");
assert.equal(warehouseSource.includes("canViewContainers"), false, "商品和货位管理页的访问判定不得再看 Container.View");
assert.match(
  warehouseSource,
  /const hasWarehouseAccess = canUseWarehouseTools;/,
  "商品和货位管理页只按仓库工具权限放行"
);

// 货柜入口改由后端菜单 containers 驱动，归入仓库与采购并紧跟商品和货位管理。
assert.deepEqual(
  buildWorkbenchSections(["warehouse", "containers"]).map((section) => ({
    key: section.key,
    itemRouteNames: section.items.map((item) => item.routeName),
  })),
  [{ key: "warehouse-purchase", itemRouteNames: ["warehouse", "containers"] }],
  "货柜管理必须作为仓库与采购下的独立入口显示"
);

console.log("warehouse-entry-gating.test.ts: ok");
