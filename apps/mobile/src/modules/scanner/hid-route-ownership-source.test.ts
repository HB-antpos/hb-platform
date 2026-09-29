import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDir = dirname(fileURLToPath(import.meta.url));
const tabsDir = resolve(currentDir, "../../../app/(shell)");

function readTabSource(fileName: string): string {
  return readFileSync(resolve(tabsDir, fileName), "utf8");
}

function assertFocusedHidOwner(fileName: string, componentName: string): void {
  const source = readTabSource(fileName);

  assert.match(
    source,
    /import \{[^}]*useIsFocused[^}]*\} from "@react-navigation\/native";/s,
    `${componentName} 必须读取当前路由焦点`,
  );
  assert.match(
    source,
    /const isFocused = useIsFocused\(\);/,
    `${componentName} 必须持有当前路由焦点状态`,
  );

  const hidUsage = source.match(/useHidBarcodeScanner\(\{[\s\S]*?\n\s*\}\);/)?.[0] ?? "";
  assert.ok(hidUsage, `${componentName} 必须接入 HID 扫码 Hook`);
  assert.match(
    hidUsage,
    /enabled:\s*isFocused,/,
    `${componentName} 只有在当前路由聚焦时才能接收 HID 扫码`,
  );
}

assertFocusedHidOwner("home.tsx", "Home");
assertFocusedHidOwner("cart.tsx", "Cart");
assertFocusedHidOwner("warehouse.tsx", "Warehouse");

const productQuerySource = readTabSource("product-query.tsx");
const productQueryContentStart = productQuerySource.indexOf("function ProductQueryContent()");
const productQueryScreenStart = productQuerySource.indexOf("export default function ProductQueryScreen()");
assert.notEqual(productQueryContentStart, -1, "商品维护内容组件必须存在");
assert.notEqual(productQueryScreenStart, -1, "商品维护路由组件必须存在");

const productQueryContentSource = productQuerySource.slice(
  productQueryContentStart,
  productQueryScreenStart,
);
const productQueryScreenSource = productQuerySource.slice(productQueryScreenStart);
const productQueryHidUsage =
  productQueryContentSource.match(/useHidBarcodeScanner\(\{[\s\S]*?\n\s*\}\);/)?.[0] ?? "";

assert.match(
  productQueryContentSource,
  /const isFocused = useIsFocused\(\);/,
  "商品维护内容必须自行读取路由焦点，失焦时仍保持挂载",
);
assert.match(
  productQueryHidUsage,
  /enabled:\s*isFocused\s*&&\s*!scannerInputBlocked,/,
  "商品维护 HID 必须同时受路由焦点和页面 busy 状态门禁",
);
assert.match(
  productQueryContentSource,
  /const cameraScanDisabled\s*=\s*!isFocused\s*\|\|\s*scannerInputBlocked\s*\|\|\s*!cameraSession\.visible;/,
  "商品维护相机事件必须同时受路由焦点、页面 busy 状态和显式相机会话门禁",
);
assert.match(
  productQueryContentSource,
  /resetKey:\s*\[[\s\S]*?isFocused\s*\?\s*"focused"\s*:\s*"blurred"[\s\S]*?\]\.join\(":"\)/,
  "商品维护相机 resetKey 必须随路由焦点变化，使旧页面回调失效",
);
assert.match(
  productQueryContentSource,
  /if \(!isFocused\) \{\s*cameraForegroundGenerationRef\.current = null;\s*updateCameraSheetSession\(\s*\{ type: "blur" \},\s*cameraScanModeRef\.current,?\s*\);\s*\}/s,
  "商品维护失焦时必须使会话代次失效、清除恢复意图并关闭相机 sheet",
);
assert.match(
  productQueryContentSource,
  /if \(\s*!isFocusedRef\.current\s*\|\|\s*isProductQueryBusy\(\)\s*\|\|\s*!isCameraSheetSessionActive\(session, session\.generation\)\s*\) \{\s*return;\s*\}[\s\S]*?await handleLookup\(barcode, "scan", "camera"\);/,
  "商品维护必须在查询前同步拒绝关闭或失焦会话的迟到相机回调",
);
assert.match(
  productQueryContentSource,
  /cameraSession\.generation,[\s\S]*?suppressRepeatsUntilChange:\s*cameraScanMode === "continuous"/,
  "商品维护连续扫码 resetKey 必须纳入显式会话代次，关闭后重开允许同一条码",
);
assert.match(
  productQueryContentSource,
  /if \(!isFocused\) \{\s*return null;\s*\}/s,
  "商品维护失焦时必须卸载相机预览",
);
assert.doesNotMatch(
  productQueryScreenSource,
  /if \(!isFocused\)/,
  "商品维护路由失焦时不得卸载内容和未保存编辑状态",
);
assert.match(
  productQueryScreenSource,
  /return <ProductQueryContent \/>;/,
  "商品维护路由必须始终保持内容组件挂载",
);

const homeSource = readTabSource("home.tsx");
assert.match(
  homeSource,
  /disabled:\s*!isFocused\s*\|\|\s*!cameraSession\.visible,[\s\S]*?if \(\s*!isFocusedRef\.current\s*\|\|\s*!isCameraSheetSessionActive\(session, session\.generation\)\s*\) \{\s*return;\s*\}[\s\S]*?await scanResult\.handleBarcode\(barcode, "camera"\);/,
  "首页必须在加购前同步拒绝关闭或失焦会话的迟到相机回调",
);
assert.match(
  homeSource,
  /resetKey:\s*`\$\{isFocused \? "focused" : "blurred"\}:\$\{cameraSession\.generation\}:[^`]*`,/,
  "首页相机 resetKey 必须包含路由焦点和显式会话代次",
);
assert.match(
  homeSource,
  /scanResult\.feedback\.barcode === lastCameraBarcode[\s\S]*?\["not_found", "blocked", "error", "supply_paused"\]\.includes\(\s*scanResult\.feedback\.status,?\s*\)[\s\S]*?scanResult\.feedback\.message/,
  "首页连续加购队列的失败、无结果、暂停供货和阻止反馈必须在相机 sheet 内可见",
);
assert.match(
  productQueryContentSource,
  /lastCameraBarcode && queryFeedback\.type === "empty"[\s\S]*?t\("messages\.notFound"\)[\s\S]*?lastCameraBarcode && queryFeedback\.type === "error"[\s\S]*?queryFeedback\.message/,
  "商品维护相机连续查询的无结果和异常反馈必须在相机 sheet 内可见",
);

const warehouseSource = readTabSource("warehouse.tsx");
assert.match(
  warehouseSource,
  /cameraSheetSession\.generation,[\s\S]*?suppressRepeatsUntilChange:\s*cameraScanMode === "continuous"/,
  "仓库连续扫码 resetKey 必须纳入显式会话代次，关闭后重开允许同一条码",
);
assert.match(
  warehouseSource,
  /!isCameraSheetSessionActive\(cameraSheetSessionRef\.current, session\.sheetGeneration\)[\s\S]*?return;[\s\S]*?suspendCameraForResult\(session\);/,
  "仓库必须在任何查询或绑定前同步拒绝关闭或失焦会话的迟到相机回调",
);
const productLocationCameraLookupSource = warehouseSource.match(
  /const handleLookupLocationsForProductScan = useCallback\([\s\S]*?\n  \}, \[getErrorMessage, handleRequestBindLocation, pendingStorageLocationBind, t\]\);/
)?.[0] ?? "";
assert.match(
  productLocationCameraLookupSource,
  /if \(fromCamera\) \{\s*setCameraResultSummary\(null\);\s*\}/,
  "仓库商品货位相机每次捕获前必须清除上一笔摘要",
);
assert.match(
  productLocationCameraLookupSource,
  /const message = getErrorMessage\(error, "messages\.locationLookupFailed"\);[\s\S]*?setSnackbar\(message\);[\s\S]*?if \(fromCamera\) \{\s*setCameraResultSummary\(\{ title: message, detail: keyword \}\);\s*\}/,
  "仓库商品货位相机查询失败必须在相机 sheet 内显示本次条码和失败说明",
);

console.log("hid-route-ownership-source.test.ts: ok");
