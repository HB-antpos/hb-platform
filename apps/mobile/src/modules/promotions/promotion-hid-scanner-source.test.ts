import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// 促销页硬件扫码（Zebra DataWedge / HID 扫码枪）接线契约：同一时刻只有一个接收者，弹层打开时让出。
const currentDir = dirname(fileURLToPath(import.meta.url));
const read = (path: string) => readFileSync(resolve(currentDir, path), "utf8");

const screenSource = read("./promotions-screen.tsx");
assert.match(screenSource, /const isFocused = useIsFocused\(\);/, "促销页必须读取路由焦点");
const screenHidUsage = screenSource.match(/useHidBarcodeScanner\(\{[\s\S]*?\n\s*\}\);/)?.[0] ?? "";
assert.ok(screenHidUsage, "促销页必须接入 HID 扫码 Hook");
assert.match(screenHidUsage, /enabled:\s*hidEnabled,/, "促销页 HID 必须受统一门禁控制");
assert.match(
  screenSource,
  /const hidEnabled = isFocused && !storePickerVisible && !opening && Boolean\(activeScanHandler\);/,
  "促销页 HID 只在路由聚焦、无分店选择 / 打开详情、且当前屏已登记处理函数时接收",
);
assert.match(
  screenSource,
  /view\.kind === "list" \|\| view\.kind === "editor" \? scanHandlers\[view\.kind\] : undefined/,
  "扫码只分发给当前屏（只读详情页不接收）",
);
assert.match(
  screenSource,
  /\{hidEnabled && hidScanner\.mode === "textInput" && hidScanner\.textInputProps \? \(/,
  "文本模式的隐藏接收框必须随门禁卸载",
);

const listSource = read("./promotion-list-view.tsx");
assert.match(
  listSource,
  /useRegisterPromotionScanHandler\("list", handleHardwareScan, Boolean\(storeCode\) && !scanVisible\);/,
  "列表在相机扫码弹层打开时必须让出硬件扫码",
);
assert.match(listSource, /onChangeText=\{searchGuard\.onChangeText\}/, "列表搜索框必须经过扫码防护");

const editorSource = read("./promotion-editor-view.tsx");
assert.match(
  editorSource,
  /useRegisterPromotionScanHandler\(\s*"editor",\s*handleHardwareScan,\s*!scanVisible && !searchVisible && !dateVisible && !weightTarget && !saving,\s*\);/,
  "编辑页任一弹层打开或保存中必须让出硬件扫码",
);
for (const guard of ["nameGuard", "fixedPriceGuard", "startTimeGuard", "endTimeGuard", "maxApplicationsGuard"]) {
  assert.match(editorSource, new RegExp(`onChangeText=\\{${guard}\\.onChangeText\\}`), `编辑页 ${guard} 输入框必须经过扫码防护`);
}

const sheetSource = read("../../components/product-maintenance/ProductPromotionQuickCreateSheet.tsx");
const sheetHidUsage = sheetSource.match(/useHidBarcodeScanner\(\{[\s\S]*?\n\s*\}\);/)?.[0] ?? "";
assert.match(sheetHidUsage, /textInputOnly:\s*true,/, "弹层是独立窗口，必须用隐藏输入框接收扫码");
assert.match(sheetSource, /const hidEnabled = visible && view === "form" && !saving;/, "弹层只在表单屏且未保存时接收");
const sheetFormStart = sheetSource.lastIndexOf("<BusinessSheet");
const sheetFormEnd = sheetSource.lastIndexOf("</BusinessSheet>");
assert.ok(
  sheetSource.slice(sheetFormStart, sheetFormEnd).includes("{...hidScanner.textInputProps}"),
  "隐藏接收框必须渲染在弹层（Modal）内部",
);
for (const guard of ["searchGuard", "nameGuard", "priceGuard", "maxPerOrderGuard"]) {
  assert.match(sheetSource, new RegExp(`onChangeText=\\{${guard}\\.onChangeText\\}`), `弹层 ${guard} 输入框必须经过扫码防护`);
}

const productQuerySource = read("../../../app/(shell)/product-query.tsx");
assert.match(
  productQuerySource,
  /hqSyncRetrying \|\|\s*promotionSheetOpen,/,
  "促销弹层打开时商品查询页必须停用自己的扫码，避免两个接收者",
);

console.log("promotion-hid-scanner-source tests passed");
